using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using UTubeBulkUploader.ViewModels;
using Windows.System;

namespace UTubeBulkUploader.Views;

public sealed partial class PreviewPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;
    public PreviewViewModel Vm { get; } = new();
    private List<FolderNode> _shown = new();
    private string? _lastRunId;
    private bool _busy;

    public PreviewPage()
    {
        InitializeComponent();
        _host.StoriesChanged += Reload;
        Reload();
    }

    public void OnActivated() => Reload();

    private void Reload()
    {
        // Rebuild only when the set of stories changed; keep edits otherwise.
        if (!_shown.SequenceEqual(_host.Stories))
        {
            _shown = _host.Stories.ToList();
            Vm.Load();
        }
        else Vm.Refresh();
    }

    // Enter in a name/date box applies it at once (otherwise it applies when you leave the box).
    private void OnCommitKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox box) return;
        switch (box.DataContext)
        {
            case StoryViewModel s:
                s.Name = box.Text;
                break;
            case FolderViewModel f when box.PlaceholderText == "dd.mm.yy":
                f.DateText = box.Text;
                break;
            case FolderViewModel f:
                f.Name = box.Text;
                break;
        }
        e.Handled = true;
    }

    private void OnSortFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderViewModel f) f.SortByTime();
    }

    private void OnRemoveStory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoryViewModel s)
        {
            Vm.Remove(s);
            _shown = _host.Stories.ToList();
        }
    }

    private void OnAddMore(object sender, RoutedEventArgs e) => App.Window?.GoTo("home");

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        // make sure an edit still in a text box is applied first
        RenameButton.Focus(FocusState.Programmatic);
        Vm.Refresh();

        if (Vm.Clashes > 0)
        {
            var list = _host.Stories.SelectMany(s => s.AllClips()).Where(c => c.Collision is not null).Take(10)
                .Select(c => $"• {c.CurrentName} → {c.NewName}\n   {c.Collision}");
            await Ui.Inform(XamlRoot, "Some names clash",
                "Nothing was renamed. Fix these first (they're marked with a red icon):\n\n" + string.Join("\n\n", list));
            return;
        }
        if (Vm.ToRename == 0)
        {
            await Ui.Inform(XamlRoot, "Nothing to rename", "Every ticked clip already has its new name.");
            return;
        }
        if (_host.Settings.ConfirmBeforeRename)
        {
            var folders = _host.Stories.SelectMany(s => s.AllClips()).Where(c => c.NeedsRename)
                .Select(c => Path.GetDirectoryName(c.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var ok = await Ui.Confirm(XamlRoot, $"Rename {Format.Count(Vm.ToRename, "file")}?",
                $"{Format.Count(Vm.ToRename, "video file")} in {Format.Count(folders, "folder")} will get their new names, in place. " +
                "Folders aren't renamed and nothing is overwritten.\n\nYou can undo this afterwards from here or from the Log page.\n\n" +
                "Close any program that has these videos open, and wait for any copying to finish.", "Rename files");
            if (!ok) return;
        }

        SetBusy(true);
        try
        {
            var result = await _host.RenameAsync();
            ShowResult(result);
        }
        catch (Exception ex)
        {
            Log.Error("Rename failed", ex);
            ShowError("Couldn't rename", ex.Message);
        }
        finally
        {
            SetBusy(false);
            _shown = _host.Stories.ToList();
            Vm.Load();
        }
    }

    private void ShowResult(RenameResult r)
    {
        _lastRunId = r.Done.Count > 0 ? r.RunId : null;
        UndoButton.Visibility = Ui.Show(_lastRunId is not null);
        var lines = new List<string>();
        if (r.Done.Count > 0) lines.Add($"{Format.Count(r.Done.Count, "file")} renamed and recorded in the log.");
        if (r.Failures.Count > 0) lines.Add("Some folders weren't renamed:\n" + Ui.Describe(r.Failures));
        if (r.LogLocked) lines.Add($"The log file is open in another program, probably Excel. Close it and the app will write to it next time; nothing is lost.");
        Message.Severity = r.Failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        Message.Title = r.Failures.Count > 0 ? "Renamed, with problems" : "Renamed";
        Message.Message = string.Join("\n\n", lines);
        Message.IsOpen = true;
    }

    private void ShowError(string title, string text)
    {
        UndoButton.Visibility = Visibility.Collapsed;
        Message.Severity = InfoBarSeverity.Error;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }

    private async void OnUndoLast(object sender, RoutedEventArgs e)
    {
        if (_lastRunId is null || _busy) return;
        if (!await Ui.Confirm(XamlRoot, "Undo this rename?", "The files renamed just now get their previous names back.", "Undo")) return;
        SetBusy(true);
        try
        {
            var r = await _host.UndoAsync(_lastRunId);
            _lastRunId = null;
            UndoButton.Visibility = Visibility.Collapsed;
            Message.Severity = r.Failures.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            Message.Title = r.Failures.Count > 0 ? "Partly undone" : "Undone";
            Message.Message = r.Failures.Count > 0
                ? $"{Format.Count(r.Done.Count, "file")} got their old names back. Problems:\n" + Ui.Describe(r.Failures)
                : $"{Format.Count(r.Done.Count, "file")} got their old names back.";
            Message.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Error("Undo failed", ex);
            ShowError("Couldn't undo", ex.Message);
        }
        finally
        {
            SetBusy(false);
            _shown = _host.Stories.ToList();
            Vm.Load();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = Ui.Show(busy);
        RenameButton.IsEnabled = !busy && Vm.CanRename;
    }
}
