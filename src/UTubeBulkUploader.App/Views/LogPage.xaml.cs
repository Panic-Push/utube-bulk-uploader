using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using UTubeBulkUploader.ViewModels;

namespace UTubeBulkUploader.Views;

public sealed partial class LogPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;
    public LogViewModel Vm { get; } = new();
    private bool _busy;

    public LogPage()
    {
        InitializeComponent();
        _host.LogChanged += () => { Vm.Load(); ShowLocked(); };
        Vm.Load();
    }

    public void OnActivated()
    {
        Vm.Load();
        ShowLocked();
    }

    private void ShowLocked()
    {
        if (!Vm.Locked) return;
        Message.Severity = InfoBarSeverity.Warning;
        Message.Title = "The log file is open in another program";
        Message.Message = "Probably Excel. Everything is kept safely inside the app and written to the file once it's closed. Click Refresh after closing it.";
        Message.IsOpen = true;
    }

    private async void OnUndoRun(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (List.SelectedItem is not LogItem item)
        {
            await Ui.Inform(XamlRoot, "Pick a row first", "Select any row from the rename run you want to undo.");
            return;
        }
        var runId = item.Row.RunId;
        var rows = _host.LogFile.Rows.Where(r => r.RunId == runId && r.Status != LogStatus.Undone).ToList();
        if (rows.Count == 0)
        {
            await Ui.Inform(XamlRoot, "Already undone", "Every file in that run already has its old name back.");
            return;
        }
        var uploaded = rows.Count(r => r.Status == LogStatus.Uploaded);
        var when = rows[0].DateTime.ToString("dd MMM yyyy, HH:mm");
        var body = $"{Format.Count(rows.Count, "file")} renamed on {when} will get their old names back." +
                   (uploaded > 0 ? $"\n\n{uploaded} of them are already on YouTube. Undo only changes the names on disk, not on YouTube." : "");
        if (!await Ui.Confirm(XamlRoot, "Undo this rename run?", body, "Undo")) return;

        SetBusy(true);
        try
        {
            var r = await _host.UndoAsync(runId);
            Message.Severity = r.Failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            Message.Title = r.Failures.Count > 0 ? "Partly undone" : "Undone";
            Message.Message = $"{Format.Count(r.Done.Count, "file")} got their old names back." +
                              (r.Failures.Count > 0 ? "\n\n" + Ui.Describe(r.Failures) : "");
            Message.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error("Undo failed", ex);
            await Ui.Inform(XamlRoot, "Couldn't undo", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnOpenExcel(object sender, RoutedEventArgs e)
    {
        var path = _host.LogFile.LogPath;
        if (!File.Exists(path))
        {
            await Ui.Inform(XamlRoot, "No log file yet", $"The log is created the first time you rename files. It will be at:\n\n{path}");
            return;
        }
        Shell.Open(path);
    }

    private void OnShowFile(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not LogItem item) return;
        var file = Path.Combine(item.Row.FolderPath, item.Title);
        if (File.Exists(file)) Shell.Open("explorer.exe", $"/select,\"{file}\"");
        else if (Directory.Exists(item.Row.FolderPath)) Shell.Open(item.Row.FolderPath);
        else _ = Ui.Inform(XamlRoot, "Folder not found", $"{item.Row.FolderPath}\n\nIs the drive connected?");
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { _host.LogFile.Load(); }
        catch (Exception ex) { Log.Warn($"Log reload failed: {ex.Message}"); }
        if (!_host.LogFile.HasUnwrittenChanges) Message.IsOpen = false;
        Vm.Load();
        ShowLocked();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = Ui.Show(busy);
    }
}
