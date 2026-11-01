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
    /// <summary>Pause between batches in night mode.</summary>
    public int NightGapMinutes { get; set; } = 2;
    /// <summary>"practice" or "real".</summary>
    public string NightTarget { get; set; } = "practice";

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
        NightGapMinutes = Math.Clamp(NightGapMinutes, 0, 60);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
