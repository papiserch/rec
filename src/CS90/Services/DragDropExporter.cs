using NAudio.Wave;

namespace CS90.Services;

/// <summary>
/// Writes a real 32-bit float WAV for the requested region into a managed export folder.
/// Files are intentionally never deleted: DAWs may keep referencing the original path.
/// </summary>
public static class DragDropExporter
{
    public static string ExportDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CS90", "Exports");

    /// <summary>Copy frames [startFrame, endFrame) of the source WAV to a new file. The source is never modified.</summary>
    public static string ExportRegion(string sourcePath, long startFrame, long endFrame)
    {
        Directory.CreateDirectory(ExportDirectory);
        string dest = Path.Combine(ExportDirectory, $"CS90_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");

        using var reader = new WaveFileReader(sourcePath);
        int frameBytes = reader.WaveFormat.BlockAlign;
        long totalFrames = reader.Length / frameBytes;
        startFrame = Math.Clamp(startFrame, 0, totalFrames);
        endFrame = Math.Clamp(endFrame, startFrame, totalFrames);

        reader.Position = startFrame * frameBytes;
        long remaining = (endFrame - startFrame) * frameBytes;

        using var writer = new WaveFileWriter(dest, reader.WaveFormat);
        var buf = new byte[frameBytes * 16384];
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int read = reader.Read(buf, 0, want);
            if (read <= 0) break;
            writer.Write(buf, 0, read);
            remaining -= read;
        }
        return dest;
    }
}
