namespace UTubeBulkUploader.Core;

/// <summary>Why a clip is not being renamed in this run.</summary>
public enum ClipState
{
    /// <summary>Will be renamed (ticked).</summary>
    Pending,
    /// <summary>The user unticked it.</summary>
    Skipped,
    /// <summary>Already renamed by the app before (matched by fingerprint in the log).</summary>
    AlreadyDone,
    /// <summary>File is still being copied or is locked by another program.</summary>
    Busy,
}

/// <summary>One video file on disk.</summary>
public sealed class Clip
{
    public required string FullPath { get; set; }
    public required long Size { get; init; }
    /// <summary>Recording time from metadata, else the file's modified time.</summary>
    public required DateTime RecordingTime { get; init; }
    public required bool RecordingTimeFromMetadata { get; init; }
    public string Fingerprint { get; set; } = "";
    public ClipState State { get; set; } = ClipState.Pending;
    public string? StateNote { get; set; }

    /// <summary>Position in the folder (0-based). Drives the clip number N.</summary>
    public int Order { get; set; }

    /// <summary>Filled in by <see cref="Namer"/>.</summary>
    public string NewName { get; set; } = "";
    /// <summary>The 1-based number in the new name, or 0 if not numbered.</summary>
    public int Number { get; set; }
    /// <summary>Set when the title had to be shortened to fit 100 characters.</summary>
    public bool WasShortened { get; set; }
    /// <summary>Set when the new name clashes with another file. Blocks renaming.</summary>
    public string? Collision { get; set; }

    public FolderNode Folder { get; set; } = null!;

    public string CurrentName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath);
    public bool IsIncluded => State == ClipState.Pending;
    public bool NeedsRename => IsIncluded && !string.Equals(NewName, CurrentName, StringComparison.Ordinal);
}

/// <summary>A folder in the story tree. The root folder is the story.</summary>
public sealed class FolderNode
{
    public required string FullPath { get; init; }
    /// <summary>Name used in titles. Starts as the folder name; the user can edit it.</summary>
    public required string DisplayName { get; set; }
    public FolderNode? Parent { get; init; }
    public List<FolderNode> Children { get; } = new();
    /// <summary>Clips directly inside this folder, kept sorted by <see cref="Clip.Order"/>.</summary>
    public List<Clip> Clips { get; } = new();

    /// <summary>Default date: earliest recording date among this folder's clips.</summary>
    public DateOnly? DefaultDate { get; set; }
    /// <summary>Date typed by the user, if any.</summary>
    public DateOnly? EditedDate { get; set; }
    public DateOnly? Date => EditedDate ?? DefaultDate;

    public bool IsStory => Parent is null;
    public FolderNode Story => Parent is null ? this : Parent.Story;

    /// <summary>True if this folder and everything under it contains no video files.</summary>
    public bool IsEmpty => Clips.Count == 0 && Children.All(c => c.IsEmpty);

    /// <summary>Display names from the story down to this folder.</summary>
    public IReadOnlyList<string> TitleParts()
    {
        var parts = new List<string>();
        for (var f = this; f is not null; f = f.Parent) parts.Add(f.DisplayName);
        parts.Reverse();
        return parts;
    }

    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public IEnumerable<Clip> AllClips() => SelfAndDescendants().SelectMany(f => f.Clips);

    /// <summary>Moves a clip to a new position in this folder and renumbers.</summary>
    public void MoveClip(Clip clip, int newIndex)
    {
        if (!Clips.Remove(clip)) throw new ArgumentException("Clip is not in this folder.", nameof(clip));
        newIndex = Math.Clamp(newIndex, 0, Clips.Count);
        Clips.Insert(newIndex, clip);
        for (int i = 0; i < Clips.Count; i++) Clips[i].Order = i;
    }
}
