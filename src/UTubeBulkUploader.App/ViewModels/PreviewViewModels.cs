using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;

namespace UTubeBulkUploader.ViewModels;

/// <summary>One added folder on Home.</summary>
public sealed class StoryRow(FolderNode story)
{
    public FolderNode Story { get; } = story;
    public string Name => Story.DisplayName;
    public string Path => Story.FullPath;
    public string Counts
    {
        get
        {
            var clips = Story.AllClips().ToList();
            if (clips.Count == 0) return "No videos";
            var subs = Story.SelfAndDescendants().Count() - 1;
            var done = clips.Count(c => c.State == ClipState.AlreadyDone);
            return Format.Count(clips.Count, "clip") +
                   (subs > 0 ? $" · {Format.Count(subs, "sub-folder")}" : "") +
                   (done > 0 ? $" · {done} already done" : "");
        }
    }
}

/// <summary>Preview: story → sub-folders → clips, all editable before renaming.</summary>
public sealed class PreviewViewModel : ObservableObject
{
    private readonly AppHost _host = AppHost.Current;
    public ObservableCollection<StoryViewModel> Stories { get; } = new();

    public string Summary { get; private set; } = "";
    public int ToRename { get; private set; }
    public int Clashes { get; private set; }
    public bool HasStories => Stories.Count > 0;
    public bool IsEmpty => Stories.Count == 0;
    public bool CanRename => ToRename > 0;

    public void Load()
    {
        Stories.Clear();
        foreach (var s in _host.Stories) Stories.Add(new StoryViewModel(this, s));
        Refresh();
    }

    /// <summary>Recalculate numbers, titles and clashes after any edit, and refresh what's shown.</summary>
    public void Refresh()
    {
        Clashes = _host.Recheck();
        foreach (var s in Stories) s.Refresh();
        var clips = _host.Stories.SelectMany(s => s.AllClips()).ToList();
        ToRename = clips.Count(c => c.NeedsRename);
        var done = clips.Count(c => c.State == ClipState.AlreadyDone);
        var busy = clips.Count(c => c.State == ClipState.Busy);
        Summary = clips.Count == 0 && Stories.Count == 0 ? "" :
            $"{Format.Count(Stories.Count, "story", "stories")} · {ToRename} of {Format.Count(clips.Count, "clip")} will be renamed" +
            (done > 0 ? $" · {done} already done" : "") +
            (busy > 0 ? $" · {busy} still copying" : "") +
            (Clashes > 0 ? $" · {Format.Count(Clashes, "name clash", "name clashes")}" : "");
        OnPropertyChanged(string.Empty);
    }

    public void Remove(StoryViewModel s)
    {
        Stories.Remove(s);
        _host.RemoveStory(s.Story);
        Refresh();
    }
}

public sealed class StoryViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    private readonly string _originalName;
    public FolderNode Story { get; }
    public ObservableCollection<FolderViewModel> Folders { get; } = new();

    public StoryViewModel(PreviewViewModel owner, FolderNode story)
    {
        _owner = owner;
        Story = story;
        _originalName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(story.FullPath));
        foreach (var f in story.SelfAndDescendants())
        {
            // The story's own clips show first; skip the story row if it holds only sub-folders.
            if (f.IsStory && f.Clips.Count == 0 && f.Children.Count > 0) continue;
            Folders.Add(new FolderViewModel(owner, f));
        }
    }

    /// <summary>Story name used in titles. Changing it never renames the folder on disk.</summary>
    public string Name
    {
        get => Story.DisplayName;
        set
        {
            var clean = Namer.SanitizePart(value);
            if (string.IsNullOrWhiteSpace(value) || clean == Story.DisplayName)
            {
                OnPropertyChanged();
                return;
            }
            Story.DisplayName = clean;
            _owner.Refresh();
        }
    }

    public string Path => Story.FullPath;
    public string RenameNote => Story.DisplayName != _originalName ? $"Folder on disk: \"{_originalName}\"" : "";
    public string Counts => new StoryRow(Story).Counts;
    public double Dim => Story.IsEmpty ? 0.5 : 1.0;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var f in Folders) f.Refresh();
    }
}

public sealed class FolderViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    private readonly string _originalName;
    private bool _syncing;
    public FolderNode Folder { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public FolderViewModel(PreviewViewModel owner, FolderNode folder)
    {
        _owner = owner;
        Folder = folder;
        _originalName = folder.DisplayName;
        foreach (var c in folder.Clips) Clips.Add(new ClipViewModel(owner, c));
        Clips.CollectionChanged += OnReordered;
    }

    public int Depth
    {
        get
        {
            int d = 0;
            for (var f = Folder.Parent; f is not null; f = f.Parent) d++;
            return d;
        }
    }

    public Thickness Indent => new(Math.Max(0, Depth - 1) * 28, 0, 0, 0);
    public bool IsStory => Folder.IsStory;
    public bool IsSub => !Folder.IsStory;
    public bool HasClips => Folder.Clips.Count > 0;
    public bool IsEmpty => Folder.IsEmpty;
    public double Dim => Folder.IsEmpty ? 0.5 : 1.0;
    public string EmptyNote => Folder.IsEmpty ? "No videos" : Folder.Clips.Count == 0 ? "Only sub-folders" : "";
    public string Heading => "Videos directly in this folder";

    /// <summary>Sub-folder name used in titles (the folder on disk keeps its name).</summary>
    public string Name
    {
        get => Folder.DisplayName;
        set
        {
            var clean = Namer.SanitizePart(value);
            if (string.IsNullOrWhiteSpace(value) || clean == Folder.DisplayName)
            {
                OnPropertyChanged();
                return;
            }
            Folder.DisplayName = clean;
            _owner.Refresh();
        }
    }

    public string RenameNote => IsSub && Folder.DisplayName != _originalName ? $"Was \"{_originalName}\"" : "";

    /// <summary>The folder's date as dd.mm.yy. Typing a new one changes every clip in this folder.</summary>
    public string DateText
    {
        get => Namer.FormatDate(Folder.Date);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Folder.EditedDate = null; // back to the date from the clips
                DateError = "";
                _owner.Refresh();
                return;
            }
            if (Namer.TryParseDate(value, out var d))
            {
                Folder.EditedDate = d == Folder.DefaultDate ? null : d;
                DateError = "";
                _owner.Refresh();
            }
            else
            {
                DateError = "Type the date like 26.10.26";
                OnPropertyChanged();
            }
        }
    }

    public bool DateEdited => Folder.EditedDate is not null;
    public string DateNote => DateEdited ? $"From clips: {Namer.FormatDate(Folder.DefaultDate)}" : "";

    private string _dateError = "";
    public string DateError
    {
        get => _dateError;
        private set
        {
            if (SetProperty(ref _dateError, value)) OnPropertyChanged(nameof(HasDateError));
        }
    }

    public bool HasDateError => _dateError.Length > 0;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var c in Clips) c.Refresh();
    }

    public void SortByTime()
    {
        var ordered = Folder.Clips.OrderBy(c => c.RecordingTime).ThenBy(c => c.CurrentName, NaturalStringComparer.Instance).ToList();
        Folder.Clips.Clear();
        Folder.Clips.AddRange(ordered);
        for (int i = 0; i < Folder.Clips.Count; i++) Folder.Clips[i].Order = i;
        _syncing = true;
        Clips.Clear();
        foreach (var c in Folder.Clips) Clips.Add(new ClipViewModel(_owner, c));
        _syncing = false;
        _owner.Refresh();
    }

    // ListView drag-to-reorder removes and re-inserts the item; mirror the new order into the folder.
    private void OnReordered(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncing || e.Action != NotifyCollectionChangedAction.Add) return;
        Folder.Clips.Clear();
        Folder.Clips.AddRange(Clips.Select(c => c.Clip));
        for (int i = 0; i < Folder.Clips.Count; i++) Folder.Clips[i].Order = i;
        AppHost.Current.Dispatcher.TryEnqueue(() => _owner.Refresh());
    }
}

public sealed class ClipViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    public Clip Clip { get; }

    public ClipViewModel(PreviewViewModel owner, Clip clip)
    {
        _owner = owner;
        Clip = clip;
    }

    /// <summary>Ticked = will be renamed. Already-done and still-copying clips can't be ticked.</summary>
    public bool Included
    {
        get => Clip.State == ClipState.Pending;
        set
        {
            if (!CanToggle || value == Included) return;
            Clip.State = value ? ClipState.Pending : ClipState.Skipped;
            _owner.Refresh();
        }
    }

    public bool CanToggle => Clip.State is ClipState.Pending or ClipState.Skipped;
    public string CurrentName => Clip.CurrentName;
    public string FullPath => Clip.FullPath;

    public string NewName => Clip.State switch
    {
        ClipState.AlreadyDone => "Already done",
        ClipState.Busy => "Skipped for now",
        ClipState.Skipped => "Not renaming",
        _ when !Clip.NeedsRename => "Already has this name",
        _ => Clip.NewName,
    };

    public double NewNameOpacity => Clip.State == ClipState.Pending && Clip.NeedsRename ? 1.0 : 0.5;
    public string Size => Format.Size(Clip.Size);
    public string DateText => Clip.RecordingTime.ToString("dd.MM.yy HH:mm") + (Clip.RecordingTimeFromMetadata ? "" : "*");
    public string DateTip => Clip.RecordingTimeFromMetadata
        ? "Recording time from the video's own information"
        : "No recording time inside the file, so this is when the file was last modified";

    public string Note => Clip.Collision ?? Clip.StateNote ??
                          (Clip.WasShortened && Clip.IsIncluded ? "Shortened to fit YouTube's 100-character title limit" : "");

    public bool HasNote => Note.Length > 0;
    public bool IsError => Clip.Collision is not null;
    public bool IsWarning => !IsError && (Clip.State == ClipState.Busy || (Clip.WasShortened && Clip.IsIncluded));
    public bool IsInfo => HasNote && !IsError && !IsWarning;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
