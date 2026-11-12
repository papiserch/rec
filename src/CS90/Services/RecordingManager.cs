using CS90.Audio;

namespace CS90.Services;

public enum TransportState { Idle, Recording, Playing }

/// <summary>
/// Owns the current recording and coordinates capture, playback, selection and export.
/// Has no WPF dependencies; the UI subscribes to events and polls <see cref="PositionFrames"/>.
/// </summary>
public sealed class RecordingManager : IDisposable
{
    public static string RecordingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS90", "Recordings");

    private readonly AudioCaptureEngine _capture = new();
    private readonly AudioPlaybackEngine _playback = new();
    private AudioFileWriter? _activeWriter;
    private long _playheadFrame;

    public TransportState State { get; private set; } = TransportState.Idle;
    public string? FilePath { get; private set; }
    public int SampleRate { get; private set; }
    public long TotalFrames { get; private set; }
    public PeakCache Peaks { get; private set; } = new();

    /// <summary>Selection in frames; null when nothing is selected.</summary>
    public (long start, long end)? Selection { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? ErrorRaised;

    public bool HasRecording => FilePath != null && TotalFrames > 0 && State != TransportState.Recording;

    public RecordingManager()
    {
        _capture.Error += m => ErrorRaised?.Invoke(m);
        _playback.Stopped += OnPlaybackStopped;
    }

    // ---- transport -------------------------------------------------------------------------

    public bool Record()
    {
        if (State == TransportState.Recording) return true;
        if (!AudioCaptureEngine.HasPlaybackDevice())
        {
            ErrorRaised?.Invoke("No playback device found.");
            return false;
        }
        StopPlayback();
        DeleteCurrentFile(); // a new recording replaces the old one (CLEAR is confirmed in the UI first if needed)

        Directory.CreateDirectory(RecordingsDirectory);
        FilePath = Path.Combine(RecordingsDirectory, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        try
        {
            _activeWriter = _capture.Start(FilePath);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke("Could not start recording: " + ex.Message);
            FilePath = null;
            return false;
        }
        SampleRate = _activeWriter.SampleRate;
        Peaks = _activeWriter.Peaks;
        TotalFrames = 0;
        Selection = null;
        _playheadFrame = 0;
        SetState(TransportState.Recording);
        return true;
    }

    public void Stop()
    {
        if (State == TransportState.Recording)
        {
            TotalFrames = _activeWriter?.FramesWritten ?? 0;
            _capture.Stop();
            TotalFrames = _activeWriter?.FramesWritten ?? TotalFrames;
            _activeWriter = null;
            SetState(TransportState.Idle);
        }
        else if (State == TransportState.Playing)
        {
            StopPlayback();
        }
    }

    public void Play()
    {
        if (!HasRecording || FilePath == null) return;
        long start, end;
        if (Selection is { } sel && sel.end > sel.start) { start = sel.start; end = sel.end; }
        else
        {
            // No selection: play the whole recording, starting from the clicked playhead if it is set.
            start = _playheadFrame >= TotalFrames ? 0 : _playheadFrame;
            end = TotalFrames;
        }
        try
        {
            _playback.Play(FilePath, start, end);
            SetState(TransportState.Playing);
        }
        catch (Exception ex)
        {
            ErrorRaised?.Invoke("Playback failed: " + ex.Message);
            SetState(TransportState.Idle);
        }
    }

    private void StopPlayback()
    {
        if (State == TransportState.Playing)
        {
            _playheadFrame = _playback.PositionFrames;
            _playback.Stop();
            SetState(TransportState.Idle);
        }
    }

    private void OnPlaybackStopped()
    {
        if (State != TransportState.Playing) return;
        _playheadFrame = Selection is { } s && s.end > s.start ? s.start : 0;
        SetState(TransportState.Idle);
    }

    public void Clear()
    {
        Stop();
        DeleteCurrentFile();
        Peaks = new PeakCache();
        TotalFrames = 0;
        Selection = null;
        _playheadFrame = 0;
        SetState(TransportState.Idle);
    }

    private void DeleteCurrentFile()
    {
        if (FilePath != null)
        {
            try { File.Delete(FilePath); } catch { /* file may be locked; harmless */ }
            FilePath = null;
        }
    }

    // ---- position / selection --------------------------------------------------------------

    public long PositionFrames => State switch
    {
        TransportState.Recording => _activeWriter?.FramesWritten ?? 0,
        TransportState.Playing => _playback.PositionFrames,
        _ => _playheadFrame
    };

    public long LiveFrames => State == TransportState.Recording ? (_activeWriter?.FramesWritten ?? 0) : TotalFrames;

    public void Seek(long frame)
    {
        if (State == TransportState.Recording) return;
        _playheadFrame = Math.Clamp(frame, 0, TotalFrames);
        if (State == TransportState.Playing)
        {
            // Jump while playing: restart from the new position (to the selection end or the file end).
            long end = Selection is { } s && s.end > _playheadFrame ? s.end : TotalFrames;
            try { _playback.Play(FilePath!, _playheadFrame, end); } catch { }
        }
        StateChanged?.Invoke();
    }

    public void SetSelection(long a, long b)
    {
        if (State == TransportState.Recording) return;
        long s = Math.Clamp(Math.Min(a, b), 0, TotalFrames);
        long e = Math.Clamp(Math.Max(a, b), 0, TotalFrames);
        Selection = e - s > 0 ? (s, e) : null;
        StateChanged?.Invoke();
    }

    public void ClearSelection()
    {
        Selection = null;
        StateChanged?.Invoke();
    }

    // ---- export ----------------------------------------------------------------------------

    /// <summary>Exports the selection, or the whole recording when nothing is selected. Returns the WAV path.</summary>
    public string? ExportForDrag()
    {
        if (!HasRecording || FilePath == null) return null;
        var (s, e) = Selection ?? (0L, TotalFrames);
        return DragDropExporter.ExportRegion(FilePath, s, e);
    }

    private void SetState(TransportState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _playback.Dispose();
        _capture.Dispose();
    }
}
