using System.Text.Json;

namespace UTubeBulkUploader.Core;

public static class AppPaths
{
    public const string AppName = "UTube Bulk Uploader";
    public const string LogFileName = "UTube Bulk Uploader Log.csv";

    /// <summary>%LOCALAPPDATA%\UTube Bulk Uploader (created on first use).</summary>
    public static string StateDir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// The user's real Desktop. On Windows this goes through the known-folder API,
    /// so a OneDrive-redirected Desktop is found correctly.
    /// </summary>
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string DefaultLogPath => Path.Combine(Desktop, LogFileName);
}

public sealed class AppSettings
{
    public static readonly string[] DefaultVideoExtensions = [".mp4", ".mov", ".mts", ".m2ts", ".avi", ".mxf"];

    /// <summary>Null means the default (Desktop).</summary>
    public string? LogFilePath { get; set; }
    public List<string> VideoExtensions { get; set; } = DefaultVideoExtensions.ToList();
    public bool ConfirmBeforeRename { get; set; } = true;
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    // Night mode (used from stage 3 on).
    public int NightlyUploadLimit { get; set; } = 100;
    public int BatchSize { get; set; } = 15;
    public string NightStartTime { get; set; } = "23:00";
    public bool NightAutoStart { get; set; }
    public string? ChromePath { get; set; }
    /// <summary>Night mode spends at least this long on each video.</summary>
    public int SecondsPerVideo { get; set; } = 45;
    /// <summary>An upload whose percentage hasn't moved for this long is treated as stuck.</summary>
    public int StuckSeconds { get; set; } = 90;

    public const int MinPerVideoSeconds = 20, MaxPerVideoSeconds = 600;
    public const int MinStuckSeconds = 30, MaxStuckSeconds = 720;

    public string EffectiveLogPath => string.IsNullOrWhiteSpace(LogFilePath) ? AppPaths.DefaultLogPath : LogFilePath!;

    public HashSet<string> VideoExtensionSet =>
        new(VideoExtensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppPaths.StateDir, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings: fall back to defaults rather than refusing to start.
        }
        return new AppSettings();
    }

    public void Save()
    {
        BatchSize = Math.Clamp(BatchSize, 1, 15);
        NightlyUploadLimit = Math.Max(1, NightlyUploadLimit);
        SecondsPerVideo = Math.Clamp(SecondsPerVideo, MinPerVideoSeconds, MaxPerVideoSeconds);
        StuckSeconds = Math.Clamp(StuckSeconds, MinStuckSeconds, MaxStuckSeconds);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Times typed as minutes:seconds, e.g. "0:45", "1:30", "10:00".</summary>
public static class TimeText
{
    public static string Format(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>Accepts "1:30", "0:45", "10:00", "1.30" and plain seconds like "90".</summary>
    public static bool TryParse(string text, out int seconds)
    {
        seconds = 0;
        var t = text.Trim().Replace('.', ':');
        if (t.Length == 0) return false;
        var parts = t.Split(':');
        if (parts.Length == 1) return int.TryParse(parts[0], out seconds) && seconds >= 0;
        if (parts.Length != 2 || parts[1].Length != 2) return false;
        if (!int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var s) || m < 0 || s < 0 || s > 59) return false;
        seconds = m * 60 + s;
        return true;
    }
}
