using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using UTubeBulkUploader.Core;

namespace UTubeBulkUploader.Services;

/// <summary>
/// The one place that owns settings, the log and the added story folders.
/// Pages talk to this; everything heavy lives in UTubeBulkUploader.Core.
/// </summary>
public sealed class AppHost : ObservableObject
{
    public static AppHost Current { get; } = new();

    public AppSettings Settings { get; private set; } = new();
    public LogStore LogFile { get; private set; } = null!;
    public RenameEngine Engine { get; private set; } = null!;
    public DispatcherQueue Dispatcher { get; private set; } = null!;
    public IRecordingDateReader DateReader { get; } = new ChainedDateReader(new ShellDateReader(), new Mp4DateReader());

    /// <summary>Story folders added on Home, scanned and waiting in Preview.</summary>
    public List<FolderNode> Stories { get; } = new();
    public event Action? StoriesChanged;
    public event Action? LogChanged;

    public void Init(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Log.Init(Path.Combine(AppPaths.StateDir, "logs"));
        Log.Info("UTube Bulk Uploader starting");
        Settings = AppSettings.Load();
        OpenLog();
    }

    private void OpenLog()
    {
        LogFile = new LogStore(Settings.EffectiveLogPath);
        try { LogFile.Load(); }
        catch (Exception ex) { Log.Error("Could not read the log file", ex); }
        Engine = new RenameEngine(LogFile);
    }

    public void Ui(Action a)
    {
        if (Dispatcher.HasThreadAccess) a();
        else Dispatcher.TryEnqueue(() => a());
    }

    public void SaveSettings(Action<AppSettings> change)
    {
        var oldLog = Settings.EffectiveLogPath;
        change(Settings);
        try { Settings.Save(); }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
        if (!string.Equals(oldLog, Settings.EffectiveLogPath, StringComparison.OrdinalIgnoreCase))
        {
            // Carry the history over to the new location so nothing is forgotten.
            var rows = LogFile.Rows;
            OpenLog();
            if (LogFile.Rows.Count == 0 && rows.Count > 0)
            {
                LogFile.Rows.AddRange(rows);
                LogFile.TrySave();
            }
            NotifyLogChanged();
            RefreshAlreadyDone();
        }
    }

    public void NotifyStoriesChanged() => Ui(() => StoriesChanged?.Invoke());
    public void NotifyLogChanged() => Ui(() => LogChanged?.Invoke());

    // ---------- Stories ----------

    /// <summary>Checks a folder can be added. Returns a reason if not.</summary>
    public string? CheckFolder(string path)
    {
        if (!Directory.Exists(path)) return "This folder can't be found. Is the drive connected?";
        return Namer.CheckOverlap(path, Stories.Select(s => s.FullPath));
    }

    public FolderNode Scan(string path, IProgress<ScanProgress>? progress, CancellationToken ct = default)
    {
        var scanner = new Scanner(DateReader, Settings.VideoExtensionSet);
        var story = scanner.Scan(path, LogFile.DoneFingerprints(), progress, ct);
        Namer.Apply(story);
        return story;
    }

    public void AddStory(FolderNode story)
    {
        Stories.Add(story);
        Recheck();
        NotifyStoriesChanged();
    }

    public void RemoveStory(FolderNode story)
    {
        Stories.Remove(story);
        Recheck();
        NotifyStoriesChanged();
    }

    /// <summary>Recomputes titles and clashes for everything shown in Preview.</summary>
    public int Recheck()
    {
        foreach (var s in Stories) Namer.Apply(s);
        return Namer.DetectCollisions(Stories, ListFolder);
    }

    /// <summary>After undo or a log move, clips may no longer (or newly) count as done.</summary>
    private void RefreshAlreadyDone()
    {
        var done = LogFile.DoneFingerprints();
        foreach (var c in Stories.SelectMany(s => s.AllClips()))
        {
            if (c.State == ClipState.AlreadyDone && !done.Contains(c.Fingerprint)) { c.State = ClipState.Pending; c.StateNote = null; }
            else if (c.State is ClipState.Pending or ClipState.Skipped && done.Contains(c.Fingerprint))
            {
                c.State = ClipState.AlreadyDone;
                c.StateNote = "Already renamed by the app.";
            }
        }
        Recheck();
        NotifyStoriesChanged();
    }

    private static IEnumerable<string> ListFolder(string folder)
    {
        try { return Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---------- Renaming ----------

    public async Task<RenameResult> RenameAsync()
    {
        var run = RenameEngine.NewRunId();
        var items = RenameEngine.PlanFromStories(Stories, run);
        Log.Info($"Rename run {run}: {items.Count} files");
        var result = await Task.Run(() => Engine.Rename(items, run));
        Log.Info($"Rename run {run}: {result.Done.Count} renamed, {result.Failures.Count} folder failures");
        await RescanAllAsync();
        NotifyLogChanged();
        return result;
    }

    public async Task<RenameResult> UndoAsync(string runId)
    {
        var result = await Task.Run(() => Engine.Undo(runId));
        Log.Info($"Undo {runId}: {result.Done.Count} restored, {result.Failures.Count} folder failures");
        await RescanAllAsync();
        NotifyLogChanged();
        return result;
    }

    /// <summary>Reads the story folders again so Preview shows the names now on disk.</summary>
    public async Task RescanAllAsync()
    {
        var paths = Stories.Select(s => (s.FullPath, s.DisplayName)).ToList();
        var fresh = await Task.Run(() =>
        {
            var list = new List<FolderNode>();
            foreach (var (path, name) in paths)
            {
                try
                {
                    var s = Scan(path, null);
                    s.DisplayName = name;
                    list.Add(s);
                }
                catch (Exception ex) { Log.Warn($"Rescan of {path} failed: {ex.Message}"); }
            }
            return list;
        });
        Stories.Clear();
        Stories.AddRange(fresh);
        Recheck();
        NotifyStoriesChanged();
    }
}
