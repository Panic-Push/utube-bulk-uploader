using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using Windows.System;

namespace UTubeBulkUploader.Views;

public sealed partial class SettingsPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Load();
    }

    public void OnActivated() => Load();

    private void Load()
    {
        _loading = true;
        var s = _host.Settings;
        Confirm.IsOn = s.ConfirmBeforeRename;
        Types.Text = string.Join(", ", s.VideoExtensions.Select(e => e.TrimStart('.').ToUpperInvariant()));
        LogInfo.Text = s.EffectiveLogPath + (string.IsNullOrWhiteSpace(s.LogFilePath) ? " (Desktop)" : "");
        LogDefault.Visibility = Ui.Show(!string.IsNullOrWhiteSpace(s.LogFilePath));
        Theme.SelectedItem = Theme.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.Theme) ?? Theme.Items[0];
        var v = typeof(App).Assembly.GetName().Version;
        VersionInfo.Text = $"Version {v?.ToString(3)} · Renames and batches news footage for YouTube Studio";
        _loading = false;
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.SaveSettings(s => s.ConfirmBeforeRename = Confirm.IsOn);
    }

    private void OnTheme(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Theme.SelectedItem is not ComboBoxItem item) return;
        var theme = (string)item.Tag;
        _host.SaveSettings(s => s.Theme = theme);
        App.Window?.ApplyTheme(theme);
    }

    private void OnTypesKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { CommitTypes(); e.Handled = true; }
    }

    private void OnTypesCommitted(object sender, RoutedEventArgs e) => CommitTypes();

    private void CommitTypes()
    {
        if (_loading) return;
        var list = Types.Text.Split(',', ';', ' ')
            .Select(t => t.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(t => t.Length > 0 && t.All(char.IsLetterOrDigit))
            .Distinct().Select(t => "." + t).ToList();
        if (list.Count == 0) list = AppSettings.DefaultVideoExtensions.ToList();
        _host.SaveSettings(s => s.VideoExtensions = list);
        Load();
    }

    private void OnTypesDefault(object sender, RoutedEventArgs e)
    {
        _host.SaveSettings(s => s.VideoExtensions = AppSettings.DefaultVideoExtensions.ToList());
        Load();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = _host.Settings.EffectiveLogPath;
        if (File.Exists(path)) Shell.Open("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Shell.Open(dir);
    }

    private async void OnChangeLog(object sender, RoutedEventArgs e)
    {
        List<string> picked;
        try { picked = MultiFolderPicker.Pick(App.Hwnd, "Choose the folder for the log file"); }
        catch (Exception ex)
        {
            await Ui.Inform(XamlRoot, "Couldn't open the folder picker", ex.Message);
            return;
        }
        if (picked.Count == 0) return;
        var path = Path.Combine(picked[0], AppPaths.LogFileName);
        _host.SaveSettings(s => s.LogFilePath = path);
        Load();
        await Ui.Inform(XamlRoot, "Log moved",
            $"The log is now kept at:\n{path}\n\nIts history was copied over if the new location had no log yet. The old file was left where it was.");
    }

    private void OnLogDefault(object sender, RoutedEventArgs e)
    {
        _host.SaveSettings(s => s.LogFilePath = null);
        Load();
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Shell.Open(AppPaths.StateDir);
}
