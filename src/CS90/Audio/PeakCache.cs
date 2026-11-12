namespace CS90.Audio;

/// <summary>
/// Base-level min/max peaks (one bin per <see cref="FramesPerBin"/> frames, mono mix of both channels).
/// Memory use is tiny compared with the audio itself, so long recordings never need to be loaded in RAM.
/// </summary>
public sealed class PeakCache
{
    public const int FramesPerBin = 256;

    private readonly object _lock = new();
    private float[] _min = new float[4096];
    private float[] _max = new float[4096];
    private int _count;

    private float _curMin = float.MaxValue, _curMax = float.MinValue;
    private int _curFrames;

    public int BinCount { get { lock (_lock) return _count; } }

    public void AddFrame(float sample)
    {
        if (sample < _curMin) _curMin = sample;
        if (sample > _curMax) _curMax = sample;
        if (++_curFrames >= FramesPerBin) FlushBin();
    }

    private void FlushBin()
    {
        lock (_lock)
        {
            if (_count == _min.Length)
            {
                Array.Resize(ref _min, _count * 2);
                Array.Resize(ref _max, _count * 2);
            }
            _min[_count] = _curMin;
            _max[_count] = _curMax;
            _count++;
        }
        _curMin = float.MaxValue; _curMax = float.MinValue; _curFrames = 0;
    }

    /// <summary>Flush the partially filled last bin (call when recording stops).</summary>
    public void Complete()
    {
        if (_curFrames > 0) FlushBin();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _count = 0;
            _curMin = float.MaxValue; _curMax = float.MinValue; _curFrames = 0;
        }
    }

    /// <summary>Min/max over bins [startBin, endBin).</summary>
    public (float min, float max) Range(int startBin, int endBin)
    {
        lock (_lock)
        {
            startBin = Math.Max(0, startBin);
            endBin = Math.Min(_count, endBin);
            if (endBin <= startBin) return (0, 0);
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = startBin; i < endBin; i++)
            {
                if (_min[i] < mn) mn = _min[i];
                if (_max[i] > mx) mx = _max[i];
            }
            return (mn, mx);
        }
    }
}
