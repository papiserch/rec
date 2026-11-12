using NAudio.Wave;

namespace CS90.Audio;

/// <summary>
/// Converts raw WASAPI loopback buffers (float or integer PCM, any channel count)
/// into interleaved stereo float at a fixed target sample rate.
/// </summary>
public sealed class FrameConverter
{
    private readonly int _targetRate;
    private WaveFormat? _fmt;
    private double _pos;           // fractional read position for resampling
    private float _prevL, _prevR;  // last input frame of previous buffer

    public FrameConverter(int targetRate) => _targetRate = targetRate;

    public void SetFormat(WaveFormat fmt)
    {
        _fmt = fmt;
        _pos = 0;
        _prevL = _prevR = 0;
    }

    public float[] Convert(byte[] buffer, int bytes, out int framesOut)
    {
        var fmt = _fmt ?? throw new InvalidOperationException("Format not set");
        int ch = fmt.Channels;
        int bps = fmt.BitsPerSample / 8;
        int frames = bytes / (ch * bps);
        var stereo = new float[frames * 2];

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);

        for (int f = 0; f < frames; f++)
        {
            float l = ReadSample(buffer, (f * ch) * bps, bps, isFloat);
            float r = ch > 1 ? ReadSample(buffer, (f * ch + 1) * bps, bps, isFloat) : l;
            stereo[f * 2] = l;
            stereo[f * 2 + 1] = r;
        }

        if (fmt.SampleRate == _targetRate)
        {
            framesOut = frames;
            return stereo;
        }
        return Resample(stereo, frames, fmt.SampleRate, out framesOut);
    }

    private static float ReadSample(byte[] b, int offset, int bps, bool isFloat)
    {
        if (isFloat && bps == 4) return BitConverter.ToSingle(b, offset);
        switch (bps)
        {
            case 2: return BitConverter.ToInt16(b, offset) / 32768f;
            case 3:
                int v = (b[offset] << 8) | (b[offset + 1] << 16) | (b[offset + 2] << 24);
                return (v >> 8) / 8388608f;
            case 4: return BitConverter.ToInt32(b, offset) / 2147483648f;
            default: return 0f;
        }
    }

    // Linear interpolation resampler (only used if the default device changes to a different rate mid-recording).
    private float[] Resample(float[] src, int frames, int srcRate, out int framesOut)
    {
        double step = (double)srcRate / _targetRate;
        var list = new List<float>((int)(frames / step * 2) + 8);
        while (_pos < frames)
        {
            int i = (int)Math.Floor(_pos);
            double t = _pos - i;
            float l0 = i == 0 ? _prevL : src[(i - 1) * 2];
            float r0 = i == 0 ? _prevR : src[(i - 1) * 2 + 1];
            float l1 = src[i * 2], r1 = src[i * 2 + 1];
            // position p refers to input index p-1..p, so interpolate between previous and current frame
            list.Add((float)(l0 + (l1 - l0) * t));
            list.Add((float)(r0 + (r1 - r0) * t));
            _pos += step;
        }
        _pos -= frames;
        if (frames > 0)
        {
            _prevL = src[(frames - 1) * 2];
            _prevR = src[(frames - 1) * 2 + 1];
        }
        framesOut = list.Count / 2;
        return list.ToArray();
    }
}
