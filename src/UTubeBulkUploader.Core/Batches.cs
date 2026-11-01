namespace UTubeBulkUploader.Core;

/// <summary>One group of up to 15 renamed clips, uploaded to YouTube Studio in one go.</summary>
public sealed class Batch
{
    public int Number { get; init; }
    public List<LogRow> Rows { get; } = new();
    public IEnumerable<string> Paths => Rows.Select(r => r.CurrentPath);
    public long TotalSize => Rows.Sum(r => r.Size);
    public string FirstName => Rows.Count == 0 ? "" : Rows[0].NewName;
    public string LastName => Rows.Count == 0 ? "" : Rows[^1].NewName;
}

public static class BatchPlanner
{
    public const int MaxBatchSize = 15;

    /// <summary>
    /// Renamed clips that haven't been uploaded yet, in the order they were renamed (the order the folders were
    /// added and the clips arranged), packed into full batches: fewer batches means fewer uploads to start.
    /// Clips whose file is no longer on disk are left out and listed in <paramref name="missing"/>.
    /// </summary>
    public static List<Batch> Plan(LogStore log, int batchSize, out List<LogRow> missing, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        batchSize = Math.Clamp(batchSize, 1, MaxBatchSize);
        missing = new List<LogRow>();
        var latest = log.LatestByFingerprint();
        var waiting = new List<LogRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in log.Rows)          // log order = rename order
        {
            if (string.IsNullOrEmpty(row.Fingerprint) || !seen.Add(row.Fingerprint)) continue;
            var current = latest[row.Fingerprint];
            if (current.Status != LogStatus.Renamed) continue;
            if (!exists(current.CurrentPath)) { missing.Add(current); continue; }
            waiting.Add(current);
        }

        var batches = new List<Batch>();
        foreach (var row in waiting)
        {
            if (batches.Count == 0 || batches[^1].Rows.Count >= batchSize) batches.Add(new Batch { Number = batches.Count + 1 });
            batches[^1].Rows.Add(row);
        }
        return batches;
    }

    /// <summary>Marks every clip in the batch as uploaded and saves the log (returns false if the log is locked).</summary>
    public static bool MarkUploaded(LogStore log, Batch batch, string note)
    {
        var now = DateTime.Now;
        foreach (var r in batch.Rows)
        {
            r.Status = LogStatus.Uploaded;
            r.StatusTime = now;
            r.Note = string.IsNullOrEmpty(r.Note) ? note : $"{r.Note} {note}";
        }
        return log.TrySave();
    }
}
