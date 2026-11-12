using NAudio.Wave;

namespace CS90.Audio;

/// <summary>
/// Streams stereo 32-bit IEEE float samples to a WAV file on disk.
/// The header is patched on <see cref="Dispose"/>, so STOP always yields a valid file.
/// </summary>
public sealed class AudioFileWriter : IDisposable
{
    private readonly WaveFileWriter _writer;
    public string Path { get; }
    public int SampleRate { get; }
    public PeakCache Peaks { get; } = new();
    public long FramesWritten { get; private set; }

    public AudioFileWriter(string path, int sampleRate)
    {
        Path = path;
        SampleRate = sampleRate;
        _writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2));
    }

    /// <summary>Write interleaved stereo float frames.</summary>
    public void Write(float[] interleaved, int frames)
    {
        _writer.WriteSamples(interleaved, 0, frames * 2);
        for (int i = 0; i < frames; i++)
            Peaks.AddFrame(Math.Abs(interleaved[i * 2]) >= Math.Abs(interleaved[i * 2 + 1])
                ? interleaved[i * 2] : interleaved[i * 2 + 1]);
        FramesWritten += frames;
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        Peaks.Complete();
        _writer.Dispose();
    }
}
