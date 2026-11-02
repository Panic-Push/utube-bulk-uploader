using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UTubeBulkUploader.Core;

public static class LogStatus
{
    public const string Renamed = "Renamed";
    public const string Uploaded = "Uploaded";
    /// <summary>Saved on YouTube by night mode; the file is still uploading.</summary>
    public const string Uploading = "Uploading";
    public const string Failed = "Failed";
    public const string Undone = "Undone";
    /// <summary>The app was told to forget this clip (Settings > Forget everything): it counts as new again.</summary>
    public const string Forgotten = "Forgotten";
    public static string InBatch(int n) => $"In batch {n}";
    public static bool IsInBatch(string s) => s.StartsWith("In batch", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One row of the log file: one clip renamed in one run.</summary>
public sealed class LogRow
{
    public string RunId { get; set; } = "";
    public DateTime DateTime { get; set; }
    public string FolderPath { get; set; } = "";
    public string OldName { get; set; } = "";
    public string NewName { get; set; } = "";
    public long Size { get; set; }
    public DateTime? RecordingDate { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StatusTime { get; set; }
    public string Note { get; set; } = "";
    /// <summary>The video's YouTube link, once night mode has saved it.</summary>
    public string YouTubeLink { get; set; } = "";

    public string CurrentPath => Path.Combine(FolderPath, Status == LogStatus.Undone ? OldName : NewName);

    /// <summary>Video ID from the YouTube link (e.g. "ujumx3_wAKU"), or empty.</summary>
    public string YouTubeVideoId
    {
        get
        {
            var m = System.Text.RegularExpressions.Regex.Match(YouTubeLink, @"(?:youtu\.be/|[?&]v=|/shorts/|/video/)([\w-]{4,})");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}

public sealed class LogLockedException(string path, Exception inner)
    : IOException($"The log file \"{Path.GetFileName(path)}\" is open in another program (probably Excel). " +
                  "Your changes are saved safely inside the app and will be written to the log once you close it.", inner);

/// <summary>
/// Reads and writes the CSV log. Every save also goes to a backup copy in the app's own folder,
/// so nothing is lost if the log is open in Excel at the time.
/// </summary>
public sealed class LogStore
{
    public static readonly string[] Header =
        ["Run ID", "Date/time", "Folder path", "Old name", "New name", "Size (bytes)", "Recording date", "Fingerprint", "Status", "Status time", "Note",
         "Full path", "YouTube link", "YouTube video ID"];

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public string LogPath { get; }
    public string BackupPath { get; }
    public List<LogRow> Rows { get; private set; } = new();
    /// <summary>True when the last save couldn't reach the log file (backup has newer data).</summary>
    public bool HasUnwrittenChanges { get; private set; }

    public LogStore(string logPath, string? backupDir = null)
    {
        LogPath = logPath;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(logPath).ToUpperInvariant())))[..12];
        BackupPath = Path.Combine(backupDir ?? AppPaths.StateDir, $"log-backup-{key}.csv");
    }

    public void Load()
    {
        bool mainExists = File.Exists(LogPath);
        bool backupExists = File.Exists(BackupPath);
        string? source = null;
        if (mainExists && backupExists)
            source = File.GetLastWriteTimeUtc(BackupPath) > File.GetLastWriteTimeUtc(LogPath).AddSeconds(2) ? BackupPath : LogPath;
        else if (mainExists) source = LogPath;
        else if (backupExists) source = BackupPath;

        Rows = source is null ? new List<LogRow>() : Parse(ReadShared(source));
        HasUnwrittenChanges = source == BackupPath && mainExists;
        if (HasUnwrittenChanges) TrySave();
    }

    /// <summary>Saves; throws <see cref="LogLockedException"/> if the log is open elsewhere.</summary>
    public void Save()
    {
        var text = Serialize(Rows);
        WriteAtomic(BackupPath, text);
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteAtomic(LogPath, text);
            HasUnwrittenChanges = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HasUnwrittenChanges = true;
            throw new LogLockedException(LogPath, ex);
        }
    }

    /// <summary>Saves, swallowing a locked-file error. Returns false if the log couldn't be written.</summary>
    public bool TrySave()
    {
        try { Save(); return true; }
        catch (LogLockedException) { return false; }
    }

    /// <summary>The latest row for each fingerprint.</summary>
    public Dictionary<string, LogRow> LatestByFingerprint()
    {
        var d = new Dictionary<string, LogRow>(StringComparer.Ordinal);
        foreach (var r in Rows)
            if (!string.IsNullOrEmpty(r.Fingerprint)) d[r.Fingerprint] = r; // rows are in time order
        return d;
    }

    /// <summary>Fingerprints of clips the app has renamed and not undone.</summary>
    public HashSet<string> DoneFingerprints() =>
        LatestByFingerprint().Values.Where(r => r.Status is not (LogStatus.Undone or LogStatus.Forgotten)).Select(r => r.Fingerprint).ToHashSet(StringComparer.Ordinal);

    /// <summary>Clips the app currently remembers (renamed, waiting, uploaded or failed).</summary>
    public List<LogRow> Remembered() =>
        LatestByFingerprint().Values.Where(r => r.Status is not (LogStatus.Undone or LogStatus.Forgotten)).ToList();

    /// <summary>
    /// Forgets every clip: each remembered row is marked Forgotten, so the clips count as new again
    /// (they can be renamed and uploaded again). Nothing is deleted; the history stays in the log.
    /// Returns how many clips were forgotten. Throws <see cref="LogLockedException"/> if the log is open elsewhere.
    /// </summary>
    public int ForgetAll()
    {
        var now = DateTime.Now;
        var rows = Remembered();
        foreach (var r in rows)
        {
            var was = r.Status;
            r.Status = LogStatus.Forgotten;
            r.StatusTime = now;
            r.Note = (r.Note + $" Forgotten (was {was}).").Trim();
        }
        // Also any older rows for the same clips, so nothing still counts as done.
        foreach (var r in Rows.Where(r => r.Status is not (LogStatus.Undone or LogStatus.Forgotten)))
        {
            r.Status = LogStatus.Forgotten;
            r.StatusTime = now;
        }
        if (rows.Count > 0) Save();
        return rows.Count;
    }

    // ---------- CSV ----------

    public static string Serialize(IEnumerable<LogRow> rows)
    {
        var sb = new StringBuilder();
        AppendLine(sb, Header);
        foreach (var r in rows)
        {
            AppendLine(sb, [
                r.RunId,
                r.DateTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                r.FolderPath,
                r.OldName,
                r.NewName,
                r.Size.ToString(CultureInfo.InvariantCulture),
                r.RecordingDate?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "",
                r.Fingerprint,
                r.Status,
                r.StatusTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
                r.Note,
                r.CurrentPath,
                r.YouTubeLink,
                r.YouTubeVideoId,
            ]);
        }
        return sb.ToString();
    }

    public static List<LogRow> Parse(string text)
    {
        var records = ParseCsv(text);
        var rows = new List<LogRow>();
        foreach (var f in records.Skip(1)) // header
        {
            if (f.Count < 9 || f.All(string.IsNullOrWhiteSpace)) continue;
            string Get(int i) => i < f.Count ? f[i] : "";
            rows.Add(new LogRow
            {
                RunId = Get(0),
                DateTime = ParseTime(Get(1)) ?? default,
                FolderPath = Get(2),
                OldName = Get(3),
                NewName = Get(4),
                Size = long.TryParse(Get(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
                RecordingDate = ParseTime(Get(6)),
                Fingerprint = Get(7),
                Status = Get(8),
                StatusTime = ParseTime(Get(9)) ?? default,
                Note = Get(10),
                YouTubeLink = Get(12),
            });
        }
        return rows;
    }

    private static DateTime? ParseTime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        // Excel may re-save dates in the local format if the user edits and saves the file.
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d)) return d;
        return null;
    }

    private static void AppendLine(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var v = fields[i] ?? "";
            if (v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || v.StartsWith(' ') || v.EndsWith(' '))
                sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
            else
                sb.Append(v);
        }
        sb.Append("\r\n");
    }

    public static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var field = new StringBuilder();
        var record = new List<string>();
        bool inQuotes = false;
        int i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                records.Add(record); record = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
        return records;
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8Bom);
        File.Move(tmp, path, overwrite: true);
    }
}
