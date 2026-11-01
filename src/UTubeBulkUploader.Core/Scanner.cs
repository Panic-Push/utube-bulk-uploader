namespace UTubeBulkUploader.Core;

public sealed record ScanProgress(int FilesDone, int FilesTotal, string CurrentFile);

/// <summary>Builds the story tree for one added folder.</summary>
public sealed class Scanner(IRecordingDateReader dateReader, ISet<string> videoExtensions)
{
    /// <param name="doneFingerprints">Fingerprints the log says were already renamed by the app.</param>
    public FolderNode Scan(string storyPath, ISet<string> doneFingerprints,
        IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        storyPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storyPath));
        if (!Directory.Exists(storyPath))
            throw new DirectoryNotFoundException($"The folder \"{storyPath}\" can't be found. Check the drive is connected.");

        var root = new FolderNode { FullPath = storyPath, DisplayName = FolderName(storyPath) };
        var files = new List<(FolderNode folder, FileInfo file)>();
        Walk(root, files, ct);

        int done = 0;
        foreach (var (folder, file) in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(done, files.Count, file.FullName));
            folder.Clips.Add(MakeClip(folder, file, doneFingerprints));
            done++;
        }
        progress?.Report(new ScanProgress(done, files.Count, ""));

        foreach (var f in root.SelfAndDescendants())
        {
            var ordered = f.Clips
                .OrderBy(c => c.RecordingTime)
                .ThenBy(c => c.CurrentName, NaturalStringComparer.Instance)
                .ToList();
            f.Clips.Clear();
            f.Clips.AddRange(ordered);
            for (int i = 0; i < f.Clips.Count; i++) f.Clips[i].Order = i;
            f.DefaultDate = f.Clips.Count == 0 ? null : DateOnly.FromDateTime(f.Clips.Min(c => c.RecordingTime));
        }
        return root;
    }

    private void Walk(FolderNode folder, List<(FolderNode, FileInfo)> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(folder.FullPath);

        foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, NaturalStringComparer.Instance))
        {
            if (!videoExtensions.Contains(file.Extension)) continue;
            if (file.Name.StartsWith("._", StringComparison.Ordinal)) continue; // macOS resource-fork junk
            if (file.Name.StartsWith(RenameEngine.TempPrefix, StringComparison.Ordinal)) continue;
            if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            files.Add((folder, file));
        }

        foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, NaturalStringComparer.Instance))
        {
            if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
            var child = new FolderNode { FullPath = sub.FullName, DisplayName = sub.Name, Parent = folder };
            folder.Children.Add(child);
            Walk(child, files, ct);
        }
    }

    private Clip MakeClip(FolderNode folder, FileInfo file, ISet<string> doneFingerprints)
    {
        DateTime? meta = null;
        try { meta = dateReader.Read(file.FullName); } catch { /* fall back to modified time */ }

        var clip = new Clip
        {
            FullPath = file.FullName,
            Size = file.Length,
            RecordingTime = meta ?? file.LastWriteTime,
            RecordingTimeFromMetadata = meta is not null,
            Folder = folder,
        };

        if (IsBusy(file))
        {
            clip.State = ClipState.Busy;
            clip.StateNote = "Still being copied or open in another program. It will be picked up next time.";
            return clip;
        }

        try
        {
            clip.Fingerprint = Fingerprint.Compute(file.FullName);
        }
        catch (IOException ex)
        {
            clip.State = ClipState.Busy;
            clip.StateNote = "Couldn't read this file: " + ex.Message;
            return clip;
        }

        if (doneFingerprints.Contains(clip.Fingerprint))
        {
            clip.State = ClipState.AlreadyDone;
            clip.StateNote = "Already renamed by the app.";
        }
        return clip;
    }

    /// <summary>
    /// A file that another program is still writing (copying) can't be opened
    /// while denying others write access.
    /// </summary>
    public static bool IsBusy(FileInfo file)
    {
        try
        {
            using var _ = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path.TrimEnd('\\', '/', ':') : name; // a drive root like E:\
    }
}

/// <summary>Sorts "Clip 2" before "Clip 10".</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
