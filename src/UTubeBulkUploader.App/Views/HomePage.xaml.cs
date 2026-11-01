using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using UTubeBulkUploader.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace UTubeBulkUploader.Views;

public sealed partial class HomePage : Page, IActivatable, INotifyPropertyChanged
{
    private const string DropHint = "Each folder you add is a story. Its sub-folders, at any depth, become parts of the titles, e.g. Collector Visit - Phone 1 - 1 (26.10.26).";
    private readonly AppHost _host = AppHost.Current;
    private CancellationTokenSource? _scanCts;

    public ObservableCollection<StoryRow> Rows { get; } = new();
    public bool HasStories => Rows.Count > 0;
    public event PropertyChangedEventHandler? PropertyChanged;

    public HomePage()
    {
        InitializeComponent();
        _host.StoriesChanged += Refresh;
        DropSub.Text = DropHint;
        Refresh();
    }

    public void OnActivated() => Refresh();

    private void Refresh()
    {
        var h = DateTime.Now.Hour;
        Greeting.Text = h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
        Rows.Clear();
        foreach (var s in _host.Stories) Rows.Add(new StoryRow(s));
        var clips = _host.Stories.SelectMany(s => s.AllClips()).ToList();
        Totals.Text = $"{Format.Count(_host.Stories.Count, "folder")} · {Format.Count(clips.Count, "clip")} · {Format.Size(clips.Sum(c => (double)c.Size))}";
        Changed(nameof(HasStories));
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ------------------------------------------------------------------ adding folders
    private void OnAddFolders(object sender, RoutedEventArgs e) => PickFolders();

    private void OnDropZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && (IsInside(d, AddButton) || IsInside(d, CancelScanButton))) return;
        PickFolders();
    }

    private static bool IsInside(DependencyObject? child, DependencyObject parent)
    {
        while (child is not null)
        {
            if (child == parent) return true;
            child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private void PickFolders()
    {
        if (_scanCts is not null) return;
        List<string> folders;
        try { folders = MultiFolderPicker.Pick(App.Hwnd, "Choose one or more story folders (Ctrl+click to pick several)"); }
        catch (Exception ex)
        {
            Log.Error("Folder picker failed", ex);
            _ = Ui.Inform(XamlRoot, "Couldn't open the folder picker", ex.Message);
            return;
        }
        if (folders.Count > 0) _ = AddFoldersAsync(folders, openPreview: false);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_scanCts is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Add folders";
        DropBorder.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropBorder.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];

    private async void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (_scanCts is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var folders = items.OfType<StorageFolder>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (folders.Count == 0)
        {
            await Ui.Inform(XamlRoot, "Drop folders, not files", "Drag the story folders themselves onto the app.");
            return;
        }
        await AddFoldersAsync(folders, openPreview: false);
    }

    public async Task AddFoldersAsync(List<string> folders, bool openPreview)
    {
        if (_scanCts is not null) return;
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var problems = new List<string>();
        var network = new List<string>();
        int added = 0;
        try
        {
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                var why = _host.CheckFolder(folder);
                if (why is not null)
                {
                    problems.Add($"• {name}: {why}");
                    continue;
                }
                SetBusy(true, $"Reading {name}…");
                var progress = new Progress<ScanProgress>(p =>
                    SetBusy(true, p.FilesTotal == 0 ? $"Reading {name}…" : $"Reading {name}: {p.FilesDone} of {p.FilesTotal} videos"));
                try
                {
                    var story = await Task.Run(() => _host.Scan(folder, progress, ct), ct);
                    _host.AddStory(story);
                    added++;
                    if (folder.StartsWith(@"\\", StringComparison.Ordinal)) network.Add(name);
                    if (story.IsEmpty) problems.Add($"• {name}: no video files found (it's still listed, greyed out in Preview).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error($"Scan of {folder} failed", ex);
                    problems.Add($"• {name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            problems.Add("Stopped. Folders read before you stopped are kept.");
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            SetBusy(false, "");
        }

        ShowMessage(problems, network);
        if (openPreview && added > 0) App.Window?.GoTo("preview");
    }

    private void ShowMessage(List<string> problems, List<string> network)
    {
        if (problems.Count == 0 && network.Count == 0)
        {
            Message.IsOpen = false;
            return;
        }
        var parts = new List<string>(problems);
        if (network.Count > 0)
            parts.Add($"• {string.Join(", ", network)} is on a network drive. It works, but every file crosses the network twice " +
                      "(once to this PC, once to YouTube), so it's slower. Run the app on the PC that holds the footage if you can.");
        Message.Severity = problems.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Message.Title = problems.Count > 0 ? "Some folders need a look" : "Network folder";
        Message.Message = string.Join("\n", parts);
        Message.IsOpen = true;
    }

    private void OnCancelScan(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

    private void SetBusy(bool busy, string text)
    {
        ScanRing.IsActive = busy;
        ScanRing.Visibility = Ui.Show(busy);
        DropIcon.Visibility = Ui.Hide(busy);
        AddButton.Visibility = Ui.Hide(busy);
        CancelScanButton.Visibility = Ui.Show(busy);
        DropTitle.Text = busy ? "Reading folders" : "Drop story folders here";
        DropSub.Text = busy ? text : DropHint;
    }

    // ------------------------------------------------------------------ list
    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoryRow row) _host.RemoveStory(row.Story);
    }

    private async void OnClear(object sender, RoutedEventArgs e)
    {
        if (!await Ui.Confirm(XamlRoot, "Clear the list?", "The folders are only removed from this list. Nothing on disk changes.", "Clear")) return;
        foreach (var s in _host.Stories.ToList()) _host.RemoveStory(s);
    }

    private void OnNext(object sender, RoutedEventArgs e) => App.Window?.GoTo("preview");
}
