using System.Text.Json;

namespace UTubeBulkUploader.Core;

public sealed class RenameItem
{
    public string Folder { get; set; } = "";
    public string OldName { get; set; } = "";
    public string NewName { get; set; } = "";
    public string TempName { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long Size { get; set; }
    public DateTime? RecordingDate { get; set; }

    public string OldPath => Path.Combine(Folder, OldName);
    public string NewPath => Path.Combine(Folder, NewName);
    public string TempPath => Path.Combine(Folder, TempName);
}

public enum JournalKind { Rename, Undo }

/// <summary>Written before any file is touched; deleted once every folder is either done or rolled back.</summary>
public sealed class RenameJournal
{
    public string RunId { get; set; } = "";
    public JournalKind Kind { get; set; }
    public DateTime StartedAt { get; set; }
    public List<RenameItem> Items { get; set; } = new();
}

public sealed record FolderFailure(string Folder, string File, string Reason, bool RolledBack);

public sealed class RenameResult
{
    public string RunId { get; init; } = "";
    public List<RenameItem> Done { get; } = new();
    public List<FolderFailure> Failures { get; } = new();
    /// <summary>True if some files are in an in-between state; the journal was kept for next launch.</summary>
    public bool Unfinished { get; set; }
    public bool LogLocked { get; set; }
}

/// <summary>
/// Renames files in place, safely (§5): journal first, then two passes per folder
/// (old → temporary, temporary → new). A folder that hits an error is rolled back.
/// </summary>
public sealed class RenameEngine(LogStore log, string? stateDir = null)
{
    public const string TempPrefix = "~ubu_";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string JournalPath => Path.Combine(stateDir ?? AppPaths.StateDir, "unfinished-rename.json");

    /// <summary>Test hook: called before each single file move.</summary>
    public Action<string, string>? BeforeMove { get; set; }

    public static string NewRunId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];

    // ---------- Planning ----------

    public static List<RenameItem> PlanFromStories(IEnumerable<FolderNode> stories, string runId)
    {
        var items = new List<RenameItem>();
        int i = 0;
        foreach (var clip in stories.SelectMany(s => s.AllClips()).Where(c => c.NeedsRename))
        {
            if (clip.Collision is not null)
                throw new InvalidOperationException("Can't rename while there are name clashes. Fix the clips marked in red first.");
            var folder = Path.GetDirectoryName(clip.FullPath)!;
            items.Add(new RenameItem
            {
                Folder = folder,
                OldName = clip.CurrentName,
                NewName = clip.NewName,
                TempName = $"{TempPrefix}{runId}_{i++}{clip.Extension}",
                Fingerprint = clip.Fingerprint,
                Size = clip.Size,
                RecordingDate = clip.RecordingTime,
            });
        }
        return items;
    }

    // ---------- Running ----------

    public RenameResult Rename(List<RenameItem> items, string runId) => Run(items, runId, JournalKind.Rename, resuming: false);

    /// <summary>Restores the original names for one rename run.</summary>
    public RenameResult Undo(string runId)
    {
        var rows = log.Rows.Where(r => r.RunId == runId && r.Status != LogStatus.Undone).ToList();
        int i = 0;
        var items = rows.Select(r => new RenameItem
        {
            Folder = r.FolderPath,
            OldName = r.NewName,
            NewName = r.OldName,
            TempName = $"{TempPrefix}undo{runId}_{i++}{Path.GetExtension(r.NewName)}",
            Fingerprint = r.Fingerprint,
            Size = r.Size,
            RecordingDate = r.RecordingDate,
        }).ToList();
        return Run(items, runId, JournalKind.Undo, resuming: false);
    }

    private RenameResult Run(List<RenameItem> items, string runId, JournalKind kind, bool resuming)
    {
        var result = new RenameResult { RunId = runId };
        if (items.Count == 0) return result;

        var journal = new RenameJournal { RunId = runId, Kind = kind, StartedAt = DateTime.Now, Items = items };
        WriteJournal(journal);

        var unresolved = new List<RenameItem>();
        foreach (var folder in items.GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var list = folder.ToList();
            try
            {
                MoveAll(list, toTemp: true, verifyFingerprint: resuming || kind == JournalKind.Undo, resuming);
                MoveAll(list, toTemp: false, verifyFingerprint: false, resuming);
                result.Done.AddRange(list);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileChangedException)
            {
                var (file, reason) = Describe(ex);
                bool rolledBack = TryRollback(list);
                if (!rolledBack) unresolved.AddRange(list);
                result.Failures.Add(new FolderFailure(folder.Key, file, reason, rolledBack));
            }
        }

        RecordInLog(journal.Kind, runId, result.Done, result);

        if (unresolved.Count > 0)
        {
            journal.Items = unresolved;
            WriteJournal(journal);
            result.Unfinished = true;
        }
        else
        {
            DeleteJournal();
        }
        return result;
    }

    /// <summary>
    /// Pass 1 (toTemp): current name → temporary name.  Pass 2: temporary name → new name.
    /// Each step skips files already in place, so it is also safe to resume after a crash.
    /// </summary>
    private void MoveAll(List<RenameItem> list, bool toTemp, bool verifyFingerprint, bool resuming)
    {
        foreach (var it in list)
        {
            if (toTemp)
            {
                if (File.Exists(it.TempPath)) continue; // already moved
                if (resuming && File.Exists(it.NewPath) && MatchesItem(it.NewPath, it)) continue; // finished before the crash
                if (!File.Exists(it.OldPath))
                    throw new FileNotFoundException($"\"{it.OldName}\" is missing. Was it moved, deleted, or is the drive disconnected?", it.OldPath);
                if (verifyFingerprint && !string.IsNullOrEmpty(it.Fingerprint) && Fingerprint.Compute(it.OldPath) != it.Fingerprint)
                    throw new FileChangedException(it.OldPath);
                Move(it.OldPath, it.TempPath);
            }
            else
            {
                if (!File.Exists(it.TempPath))
                {
                    if (File.Exists(it.NewPath) && MatchesItem(it.NewPath, it)) continue; // already done
                    throw new FileNotFoundException($"\"{it.OldName}\" disappeared during renaming. Is the drive still connected?", it.TempPath);
                }
                if (File.Exists(it.NewPath))
                    throw new IOException($"A file called \"{it.NewName}\" appeared in the folder while renaming. Nothing was overwritten.");
                Move(it.TempPath, it.NewPath);
            }
        }
    }

    private void Move(string from, string to)
    {
        BeforeMove?.Invoke(from, to);
        File.Move(from, to, overwrite: false);
    }

    /// <summary>Puts every file in the folder back under its original name. Never overwrites.</summary>
    private bool TryRollback(List<RenameItem> list)
    {
        try
        {
            // Phase A: anything already at its new name goes back to the temporary name first,
            // so swapped names (A↔B) can't clash.
            foreach (var it in list)
            {
                if (File.Exists(it.TempPath) || string.Equals(it.OldName, it.NewName, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(it.NewPath) && MatchesItem(it.NewPath, it))
                    File.Move(it.NewPath, it.TempPath, overwrite: false);
            }
            // Phase B: temporary → original.
            foreach (var it in list)
            {
                if (!File.Exists(it.TempPath)) continue;
                if (File.Exists(it.OldPath)) return false; // never overwrite; leave for the user
                File.Move(it.TempPath, it.OldPath, overwrite: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool MatchesItem(string path, RenameItem it)
    {
        if (string.IsNullOrEmpty(it.Fingerprint)) return new FileInfo(path).Length == it.Size;
        try { return Fingerprint.Compute(path) == it.Fingerprint; }
        catch (IOException) { return false; }
    }

    private void RecordInLog(JournalKind kind, string runId, List<RenameItem> done, RenameResult result)
    {
        if (done.Count == 0) return;
        var now = DateTime.Now;
        if (kind == JournalKind.Rename)
        {
            foreach (var it in done)
            {
                log.Rows.Add(new LogRow
                {
                    RunId = runId, DateTime = now, FolderPath = it.Folder,
                    OldName = it.OldName, NewName = it.NewName, Size = it.Size,
                    RecordingDate = it.RecordingDate, Fingerprint = it.Fingerprint,
                    Status = LogStatus.Renamed, StatusTime = now,
                });
            }
        }
        else
        {
            var doneKeys = done.Select(d => (d.Folder.ToUpperInvariant(), d.OldName.ToUpperInvariant())).ToHashSet();
            foreach (var row in log.Rows.Where(r => r.RunId == runId && r.Status != LogStatus.Undone))
            {
                if (!doneKeys.Contains((row.FolderPath.ToUpperInvariant(), row.NewName.ToUpperInvariant()))) continue;
                var wasUploaded = row.Status == LogStatus.Uploaded;
                row.Status = LogStatus.Undone;
                row.StatusTime = now;
                if (wasUploaded) row.Note = AppendNote(row.Note, "Was already uploaded before undo.");
            }
        }
        result.LogLocked = !log.TrySave();
    }

    private static string AppendNote(string note, string add) => string.IsNullOrEmpty(note) ? add : note + " " + add;

    // ---------- Crash recovery ----------

    public RenameJournal? ReadUnfinished()
    {
        if (!File.Exists(JournalPath)) return null;
        try { return JsonSerializer.Deserialize<RenameJournal>(File.ReadAllText(JournalPath)); }
        catch { return null; }
    }

    /// <summary>Completes an interrupted run.</summary>
    public RenameResult FinishUnfinished()
    {
        var j = ReadUnfinished() ?? throw new InvalidOperationException("There is no unfinished rename.");
        return Run(j.Items, j.RunId, j.Kind, resuming: true);
    }

    /// <summary>Restores the original names for an interrupted run.</summary>
    public RenameResult RollBackUnfinished()
    {
        var j = ReadUnfinished() ?? throw new InvalidOperationException("There is no unfinished rename.");
        var result = new RenameResult { RunId = j.RunId };
        var unresolved = new List<RenameItem>();
        foreach (var folder in j.Items.GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var list = folder.ToList();
            if (!TryRollback(list))
            {
                unresolved.AddRange(list);
                result.Failures.Add(new FolderFailure(folder.Key, "", "Couldn't put every file back. Is the drive connected?", false));
            }
        }
        if (unresolved.Count > 0)
        {
            j.Items = unresolved;
            WriteJournal(j);
            result.Unfinished = true;
        }
        else DeleteJournal();
        return result;
    }

    private void WriteJournal(RenameJournal j)
    {
        var tmp = JournalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j, JsonOptions));
        File.Move(tmp, JournalPath, overwrite: true);
    }

    private void DeleteJournal()
    {
        if (File.Exists(JournalPath)) File.Delete(JournalPath);
    }

    // ---------- Messages ----------

    public static (string file, string reason) Describe(Exception ex)
    {
        string file = ex switch
        {
            FileNotFoundException f => Path.GetFileName(f.FileName ?? ""),
            FileChangedException c => Path.GetFileName(c.FilePath),
            _ => "",
        };
        string reason = ex switch
        {
            FileNotFoundException f => f.Message,
            DirectoryNotFoundException => "The folder can't be reached. Is the drive still connected?",
            UnauthorizedAccessException => "Windows refused: the file is read-only or you don't have permission to change it. Clear 'Read-only' in the file's Properties and try again.",
            FileChangedException c => c.Message,
            IOException io when IsSharingViolation(io) => "The file is open in another program or still being copied. Close it (or wait for the copy to finish) and try again.",
            _ => ex.Message,
        };
        return (file, reason);
    }

    private static bool IsSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code is 32 or 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
    }
}

public sealed class FileChangedException(string path)
    : IOException($"\"{Path.GetFileName(path)}\" isn't the same file the app renamed earlier, so it was left alone.")
{
    public string FilePath { get; } = path;
}
