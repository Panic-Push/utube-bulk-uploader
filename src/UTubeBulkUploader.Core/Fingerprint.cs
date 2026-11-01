using System.Security.Cryptography;

namespace UTubeBulkUploader.Core;

/// <summary>
/// Identifies a clip regardless of its name or drive letter:
/// file size plus a SHA-256 hash of the first and last 1 MB.
/// </summary>
public static class Fingerprint
{
    public const int ChunkSize = 1024 * 1024;

    public static string Compute(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.RandomAccess);
        long length = fs.Length;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[ChunkSize];

        int read = ReadFully(fs, buf, (int)Math.Min(ChunkSize, length));
        sha.AppendData(buf, 0, read);

        if (length > ChunkSize)
        {
            long tailStart = Math.Max(ChunkSize, length - ChunkSize);
            fs.Position = tailStart;
            read = ReadFully(fs, buf, (int)(length - tailStart));
            sha.AppendData(buf, 0, read);
        }

        var hash = Convert.ToHexString(sha.GetHashAndReset())[..32].ToLowerInvariant();
        return $"{length}-{hash}";
    }

    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
