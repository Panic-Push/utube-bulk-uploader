using System.Text;

namespace UTubeBulkUploader.Core;

/// <summary>Technical log for troubleshooting, one file per day, kept 14 days. Never contains tokens.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _folder;

    public static void Init(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.GetFiles(folder, "app-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_folder is null) return;
        var now = DateTimeOffset.Now;
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder, $"app-{now:yyyy-MM-dd}.log"),
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }
}
