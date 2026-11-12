using NAudio.Wave;

namespace CS90.Audio;

/// <summary>Streams a region of the recorded WAV from disk to the default output device.</summary>
public sealed class AudioPlaybackEngine : IDisposable
{
    private sealed class LimitedProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private long _remaining; // samples
        public LimitedProvider(ISampleProvider src, long samples) { _src = src; _remaining = samples; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int want = (int)Math.Min(count, _remaining);
            int read = _src.Read(buffer, offset, want);
            _remaining -= read;
            return read;
        }
    }

    private AudioFileReader? _reader;
    private WaveOutEvent? _out;
    private long _startFrame;
    private int _rate;

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;

    /// <summary>Raised on a background thread when playback reached the end or was stopped.</summary>
    public event Action? Stopped;

    public void Play(string path, long startFrame, long endFrame)
    {
        Stop();
        _reader = new AudioFileReader(path);
        _rate = _reader.WaveFormat.SampleRate;
        int ch = _reader.WaveFormat.Channels;
        long total = _reader.Length / (4L * ch);
        startFrame = Math.Clamp(startFrame, 0, total);
        endFrame = Math.Clamp(endFrame, startFrame, total);
        if (endFrame - startFrame < 1) { CleanUp(); return; }

        _startFrame = startFrame;
        _reader.Position = startFrame * 4L * ch;
        var limited = new LimitedProvider(_reader, (endFrame - startFrame) * ch);

        _out = new WaveOutEvent { DesiredLatency = 80 };
        _out.PlaybackStopped += (s, e) => Stopped?.Invoke();
        _out.Init(limited);
        _out.Play();
    }

    /// <summary>Playhead position in frames, derived from what the device has actually played.</summary>
    public long PositionFrames
    {
        get
        {
            if (_out == null || _reader == null) return _startFrame;
            long bytes = _out.GetPosition();
            int frameBytes = 4 * _reader.WaveFormat.Channels;
            return _startFrame + bytes / frameBytes;
        }
    }

    public int SampleRate => _rate;

    public void Stop()
    {
        var o = _out; _out = null;
        if (o != null)
        {
            try { o.Stop(); } catch { }
            o.Dispose();
        }
        CleanUp();
    }

    private void CleanUp()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => Stop();
}
