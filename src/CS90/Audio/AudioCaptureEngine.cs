using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace CS90.Audio;

/// <summary>
/// Records the default Windows playback device through WASAPI loopback.
/// The capture callback only enqueues buffers; a dedicated thread converts and writes to disk,
/// so neither disk I/O nor the UI can cause dropouts.
/// </summary>
public sealed class AudioCaptureEngine : IDisposable
{
    private sealed class DeviceWatcher : IMMNotificationClient
    {
        private readonly Action _changed;
        public DeviceWatcher(Action changed) => _changed = changed;
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id)
        { if (flow == DataFlow.Render && role == Role.Multimedia) _changed(); }
        public void OnDeviceStateChanged(string id, DeviceState state) { }
        public void OnDeviceAdded(string id) { }
        public void OnDeviceRemoved(string id) { }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher;
    private readonly object _sync = new();

    private WasapiLoopbackCapture? _capture;
    private WasapiOut? _silence; // keeps the loopback stream alive during silence so timing stays exact
    private BlockingCollection<(byte[] data, int len, WaveFormat fmt)>? _queue;
    private Thread? _writerThread;
    private AudioFileWriter? _writer;
    private FrameConverter? _converter;
    private volatile bool _recording;
    private volatile bool _restarting;

    public bool IsRecording => _recording;
    public AudioFileWriter? Writer => _writer;
    public long FramesWritten => _writer?.FramesWritten ?? 0;
    public int SampleRate => _writer?.SampleRate ?? 0;

    /// <summary>Raised (on a background thread) when recording ended unexpectedly and could not recover.</summary>
    public event Action<string>? Error;

    public AudioCaptureEngine()
    {
        _watcher = new DeviceWatcher(OnDefaultDeviceChanged);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
    }

    public static bool HasPlaybackDevice()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            using var d = e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return d != null;
        }
        catch { return false; }
    }

    public AudioFileWriter Start(string path)
    {
        lock (_sync)
        {
            if (_recording) throw new InvalidOperationException("Already recording");

            using var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            int rate = dev.AudioClient.MixFormat.SampleRate;

            _writer = new AudioFileWriter(path, rate);
            _converter = new FrameConverter(rate);
            _queue = new BlockingCollection<(byte[], int, WaveFormat)>(new ConcurrentQueue<(byte[], int, WaveFormat)>());
            _recording = true;

            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "CS90 writer", Priority = ThreadPriority.AboveNormal };
            _writerThread.Start();

            StartCapture();
            return _writer;
        }
    }

    private void StartCapture()
    {
        var capture = new WasapiLoopbackCapture(); // default render device
        capture.DataAvailable += (s, e) =>
        {
            if (!_recording || e.BytesRecorded == 0) return;
            var copy = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            try { _queue?.Add((copy, copy.Length, capture.WaveFormat)); } catch (InvalidOperationException) { }
        };
        capture.RecordingStopped += (s, e) =>
        {
            if (!_recording || _restarting || !ReferenceEquals(s, _capture)) return;
            // Stopped while still recording: device was removed or invalidated -> try to follow the new default device.
            if (e.Exception != null) RestartCapture();
        };
        _capture = capture;

        try
        {
            var dev = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var silent = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
            silent.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(dev.AudioClient.MixFormat.SampleRate, 2)).ToSampleProvider());
            silent.Play();
            _silence = silent;
        }
        catch { _silence = null; /* non-fatal: only affects timing during total silence */ }

        capture.StartRecording();
    }

    private void StopCapture()
    {
        var cap = _capture; _capture = null;
        try { cap?.StopRecording(); } catch { }
        try { cap?.Dispose(); } catch { }
        var sil = _silence; _silence = null;
        try { sil?.Stop(); sil?.Dispose(); } catch { }
    }

    private void OnDefaultDeviceChanged()
    {
        if (_recording) RestartCapture();
    }

    private void RestartCapture()
    {
        lock (_sync)
        {
            if (!_recording || _restarting) return;
            _restarting = true;
            try
            {
                StopCapture();
                Thread.Sleep(150);
                StartCapture();
            }
            catch (Exception ex)
            {
                Error?.Invoke("Playback device lost: " + ex.Message);
            }
            finally { _restarting = false; }
        }
    }

    private void WriterLoop()
    {
        var q = _queue!;
        WaveFormat? last = null;
        int sinceFlush = 0;
        foreach (var item in q.GetConsumingEnumerable())
        {
            if (last == null || !ReferenceEquals(last, item.fmt) && !last.Equals(item.fmt))
            {
                _converter!.SetFormat(item.fmt);
                last = item.fmt;
            }
            var frames = _converter!.Convert(item.data, item.len, out int n);
            if (n > 0) _writer!.Write(frames, n);
            if (++sinceFlush >= 50) { _writer!.Flush(); sinceFlush = 0; }
        }
    }

    /// <summary>Stops capture, drains the queue and finalizes the WAV. Returns the finished file path.</summary>
    public string? Stop()
    {
        lock (_sync)
        {
            if (!_recording) return null;
            _recording = false;
            StopCapture();
            _queue?.CompleteAdding();
            _writerThread?.Join();
            var path = _writer?.Path;
            _writer?.Dispose();
            _queue?.Dispose();
            _queue = null; _writerThread = null;
            return path;
        }
    }

    public void Dispose()
    {
        Stop();
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        _enumerator.Dispose();
    }
}
