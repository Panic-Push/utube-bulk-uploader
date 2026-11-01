using System.Buffers.Binary;

namespace UTubeBulkUploader.Core;

/// <summary>Reads a clip's recording time from its metadata. Returns null if not found.</summary>
public interface IRecordingDateReader
{
    DateTime? Read(string path);
}

/// <summary>Tries each reader in turn and returns the first date found.</summary>
public sealed class ChainedDateReader(params IRecordingDateReader[] readers) : IRecordingDateReader
{
    public DateTime? Read(string path)
    {
        foreach (var r in readers)
        {
            try
            {
                var d = r.Read(path);
                if (d is not null && RecordingDateSanity.IsPlausible(d.Value)) return d;
            }
            catch
            {
                // A reader failing on one file must never stop the scan.
            }
        }
        return null;
    }
}

public static class RecordingDateSanity
{
    /// <summary>Rejects zero/placeholder dates that cameras write when the clock isn't set.</summary>
    public static bool IsPlausible(DateTime d) =>
        d.Year >= 1995 && d <= DateTime.Now.AddDays(2);
}

/// <summary>
/// Reads the creation time from the 'mvhd' box of MP4 and MOV files (container metadata).
/// Works on any OS, so it is also used as a fallback after the Windows property system.
/// </summary>
public sealed class Mp4DateReader : IRecordingDateReader
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v", ".3gp" };

    public DateTime? Read(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path))) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        var moov = FindBox(fs, 0, fs.Length, "moov");
        if (moov is null) return null;
        var mvhd = FindBox(fs, moov.Value.dataStart, moov.Value.end, "mvhd");
        if (mvhd is null) return null;

        fs.Position = mvhd.Value.dataStart;
        Span<byte> buf = stackalloc byte[12];
        if (fs.Read(buf) < 12) return null;
        byte version = buf[0];
        ulong seconds = version == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(4, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(4, 4));
        if (seconds == 0) return null;
        try
        {
            return Epoch1904.AddSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (long dataStart, long end)? FindBox(FileStream fs, long start, long end, string type)
    {
        Span<byte> header = stackalloc byte[16];
        long pos = start;
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(header[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string boxType = System.Text.Encoding.ASCII.GetString(header.Slice(4, 4));
            long headerLen = 8;
            if (size == 1)
            {
                if (fs.Read(header.Slice(8, 8)) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerLen) return null; // corrupt
            if (boxType == type) return (pos + headerLen, Math.Min(pos + size, end));
            pos += size;
        }
        return null;
    }
}
