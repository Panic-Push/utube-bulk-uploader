using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using UTubeBulkUploader.Views;
using Windows.Graphics;

namespace UTubeBulkUploader;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "UTubeBulkUploader.ico")); } catch (Exception) { }
        SizeToScreen();
        ApplyTheme(AppHost.Current.Settings.Theme);

        // Fluent entrance: each page slides up gently when shown
        ContentFrame.ContentTransitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 28 } };
        AppHost.Current.StoriesChanged += UpdateBadges;
        Nav.SelectedItem = Nav.MenuItems[0];
        Root.Loaded += async (_, _) => await OfferRecoveryAsync();
    }

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public void GoTo(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
        else if (tag == "settings")
        {
            // the Settings entry only exists once the navigation pane has loaded
            if (Nav.SettingsItem is not null) Nav.SelectedItem = Nav.SettingsItem;
            else Nav.Loaded += (_, _) => Nav.SelectedItem = Nav.SettingsItem;
        }
    }

    public void StartScan(IReadOnlyList<string> folders, bool openPreview)
    {
        GoTo("home");
        if (_pages.TryGetValue("home", out var p) && p is HomePage home) _ = home.AddFoldersAsync(folders.ToList(), openPreview);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "preview" => new PreviewPage(),
                "batches" => new ComingSoonPage("Batches", "",
                    "Next update: renamed clips grouped into batches of up to 15, ready to drag into YouTube Studio."),
                "night" => new ComingSoonPage("Night mode", "",
                    "A later update: uploads batches to YouTube Studio overnight, up to your nightly limit."),
                "log" => new LogPage(),
                "settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[tag] = page;
        }
        ContentFrame.Content = page;
        if (page is IActivatable a) a.OnActivated();
    }

    private void UpdateBadges()
    {
        var n = AppHost.Current.Stories.Count;
        PreviewBadge.Value = n;
        PreviewBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>If a rename was cut short (app closed, PC off, drive pulled out), offer to finish or undo it.</summary>
    private async Task OfferRecoveryAsync()
    {
        var host = AppHost.Current;
        var journal = host.Engine.ReadUnfinished();
        if (journal is null) return;

        var what = journal.Kind == JournalKind.Undo ? "An undo" : "A rename";
        var folders = journal.Items.Select(i => i.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var d = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = $"{what} didn't finish",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{what} started {journal.StartedAt:dd MMM, HH:mm} was interrupted, so {journal.Items.Count} file" +
                       $"{(journal.Items.Count == 1 ? "" : "s")} in {folders} folder{(folders == 1 ? "" : "s")} may have temporary names.\n\n" +
                       "Finish: give them their new names.\nRoll back: put back the names they had before.\n\n" +
                       "Make sure the drive with the footage is connected first.",
            },
            PrimaryButtonText = "Finish",
            SecondaryButtonText = "Roll back",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await d.ShowAsync();
        if (choice == ContentDialogResult.None) return;

        RenameResult result;
        try
        {
            result = await Task.Run(() => choice == ContentDialogResult.Primary ? host.Engine.FinishUnfinished() : host.Engine.RollBackUnfinished());
        }
        catch (Exception ex)
        {
            Log.Error("Recovery failed", ex);
            await Ui.Inform(Root.XamlRoot, "Couldn't fix the interrupted rename", ex.Message);
            return;
        }
        host.NotifyLogChanged();
        var text = result.Unfinished
            ? "Some files couldn't be fixed yet:\n\n" + Ui.Describe(result.Failures) + "\n\nThe app will offer this again next time it opens."
            : choice == ContentDialogResult.Primary ? "All files now have their new names." : "All files have their previous names back.";
        await Ui.Inform(Root.XamlRoot, result.Unfinished ? "Not finished yet" : "Done", text);
    }

    private void SizeToScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = Math.Min((int)(1240 * scale), (int)(area.Width * 0.92));
        var h = Math.Min((int)(820 * scale), (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>Pages that refresh when shown.</summary>
public interface IActivatable
{
    void OnActivated();
}
