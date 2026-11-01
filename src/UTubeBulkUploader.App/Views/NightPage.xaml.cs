using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;

namespace UTubeBulkUploader.Views;

public sealed partial class NightPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;
    private readonly ObservableCollection<string> _activity = new();
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cts;
    private DateTime? _nextAt;
    private string? _screenshot;
    private bool _loading = true;
    private int _startToday;

    public bool Running => _cts is not null;

    public NightPage()
    {
        InitializeComponent();
        Activity.ItemsSource = _activity;
        _loading = false;
        _countdown.Tick += (_, _) => UpdateCountdown();
        _host.LogChanged += () => { if (!Running) RefreshSummary(); };
        RefreshSummary();
        RefreshChrome();
    }

    public void OnActivated()
    {
        if (!Running) RefreshSummary();
        RefreshChrome();
    }

    private void RefreshSummary()
    {
        var batches = BatchPlanner.Plan(_host.LogFile, _host.Settings.BatchSize, out _);
        var clips = batches.Sum(b => b.Rows.Count);
        Summary.Text = clips == 0 ? "Nothing waiting: rename clips first."
            : $"{Format.Count(clips, "clip")} waiting in {Format.Count(batches.Count, "batch", "batches")} · {_host.Settings.NightGapMinutes} min pause between batches";
        StartButton.IsEnabled = !Running && clips > 0;
        StatToday.Text = BatchPlanner.UploadedToday(_host.LogFile).ToString();
        StatLeft.Text = clips.ToString();
    }

    private void RefreshChrome()
    {
        var chrome = ChromeLauncher.FindChrome(_host.Settings.ChromePath);
        var running = ChromeLauncher.RunningPort(ChromeLauncher.ProfileDir) is not null;
        var signingIn = !running && ChromeLauncher.IsProfileInUse(ChromeLauncher.ProfileDir);
        ChromeInfo.Text = chrome is null
            ? "Google Chrome wasn't found. Install Chrome, or set its location in Settings."
            : signingIn ? "Open for signing in. Sign in to the channel's Google account there, then close that Chrome window before starting night mode."
            : running ? "Night mode's Chrome is open. To sign in or switch account, click Sign in: it reopens Chrome in a normal window."
            : "Uses its own Chrome profile, separate from your everyday Chrome. Click Sign in once, sign in to the channel's Google account, then close that window; it stays signed in.";
        OpenChromeButton.IsEnabled = chrome is not null;
    }

    private async void OnOpenChrome(object sender, RoutedEventArgs e)
    {
        var chrome = ChromeLauncher.FindChrome(_host.Settings.ChromePath);
        if (chrome is null) return;
        if (Running)
        {
            await Ui.Inform(XamlRoot, "Night mode is running", "Stop night mode first, then sign in.");
            return;
        }
        try
        {
            // Google blocks sign-in in a Chrome that a program controls, so this window opens as plain Chrome.
            await ChromeLauncher.OpenForSignInAsync(chrome, ChromeLauncher.ProfileDir, StudioTarget.Real.HomeUrl, CancellationToken.None);
            await Ui.Inform(XamlRoot, "Sign in, then close Chrome",
                "A normal Chrome window is opening with the app's own profile.\n\n" +
                "1. Sign in to the Google account that owns the channel.\n" +
                "2. Check that YouTube Studio opens and is in English.\n" +
                "3. Close that Chrome window.\n\n" +
                "You stay signed in. Night mode reopens Chrome itself when it starts.");
        }
        catch (Exception ex)
        {
            await Ui.Inform(XamlRoot, "Couldn't open Chrome", ex.Message);
        }
        RefreshChrome();
    }

    private void Add(string line) => _host.Ui(() =>
    {
        _activity.Insert(0, $"{DateTime.Now:HH:mm}  {line}");
        while (_activity.Count > 300) _activity.RemoveAt(_activity.Count - 1);
        Log.Info("Night mode: " + line);
    });

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var chrome = ChromeLauncher.FindChrome(_host.Settings.ChromePath);
        if (chrome is null)
        {
            await Ui.Inform(XamlRoot, "Chrome not found", "Install Google Chrome, or set its location in Settings.");
            return;
        }
        if (!await Ui.Confirm(XamlRoot, "Start real uploads?",
                "Night mode will upload the waiting batches to the YouTube channel signed in to its Chrome window, " +
                $"with a {_host.Settings.NightGapMinutes}-minute pause between batches, until everything is done or Studio says stop.\n\n" +
                "Don't use that Chrome window while it runs. Keep this PC awake and connected.", "Start")) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _screenshot = null;
        ResultBar.IsOpen = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusTitle.Text = "Starting";
        StatusDetail.Text = "";
        Bar.Value = 0;
        KeepAwake(true);
        StatRun.Text = "0";
        StatFailed.Text = "0";
        _startToday = BatchPlanner.UploadedToday(_host.LogFile);
        Add("Night mode started. Checking before the first batch…");
        Add("✓ This PC is kept awake while night mode runs");

        BatchResult result;
        try
        {
            var target = StudioTarget.Real;
            var port = await ChromeLauncher.EnsureRunningAsync(chrome, ChromeLauncher.ProfileDir, target.HomeUrl, ct);
            await using var tab = await CdpTab.ConnectAsync(port, ct);
            await tab.SendAsync("Page.bringToFront", ct: ct);
            var driver = new StudioDriver(tab, target);
            await driver.InitAsync(ct);
            var gap = TimeSpan.FromMinutes(_host.Settings.NightGapMinutes);
            var runner = new NightRunner { Gap = gap };
            runner.Activity += Add;
            runner.Progress += p => _host.Ui(() => ShowProgress(p));
            result = await Task.Run(() => runner.RunAsync(driver, _host.LogFile, _host.Settings.BatchSize, ct), CancellationToken.None);
        }
        catch (OperationCanceledException) { result = new BatchResult(BatchOutcome.Stopped, "Stopped."); }
        catch (Exception ex)
        {
            Log.Error("Night mode failed", ex);
            result = new BatchResult(BatchOutcome.Error, ex.Message);
        }

        KeepAwake(false);
        _cts.Dispose();
        _cts = null;
        _nextAt = null;
        _countdown.Stop();
        StopButton.IsEnabled = false;
        _host.NotifyLogChanged();
        ShowResult(result);
        RefreshSummary();
        RefreshChrome();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StatusTitle.Text = "Stopping…";
        Add("Stop requested. The batch Studio is already uploading carries on in Chrome; it won't be marked uploaded here.");
        _cts?.Cancel();
    }

    private void ShowProgress(NightProgress p)
    {
        StatusTitle.Text = $"Batch {p.BatchNumber} of {p.BatchCount}";
        StatusDetail.Text = p.Status;
        Bar.Maximum = Math.Max(1, p.ClipsTotal);
        Bar.Value = p.Uploaded + p.Failed;
        StatRun.Text = p.Uploaded.ToString();
        StatFailed.Text = p.Failed.ToString();
        StatToday.Text = (_startToday + p.Uploaded).ToString();
        StatLeft.Text = Math.Max(0, p.ClipsTotal - p.Uploaded - p.Failed).ToString();
        _nextAt = p.NextBatchAt;
        if (_nextAt is not null) { _countdown.Start(); UpdateCountdown(); }
        else _countdown.Stop();
    }

    private void UpdateCountdown()
    {
        if (_nextAt is not { } at) return;
        var left = at - DateTime.Now;
        StatusDetail.Text = left > TimeSpan.Zero ? $"Pausing. Next batch in {(int)left.TotalMinutes}:{left.Seconds:00}" : "Starting the next batch";
    }

    private void ShowResult(BatchResult r)
    {
        _screenshot = r.ScreenshotPath;
        (ResultBar.Severity, ResultBar.Title, StatusTitle.Text) = r.Outcome switch
        {
            BatchOutcome.Done => (InfoBarSeverity.Success, "Finished", "Finished"),
            BatchOutcome.StudioLimit => (InfoBarSeverity.Warning, "Studio said stop", "Stopped: Studio's limit"),
            BatchOutcome.SignedOut => (InfoBarSeverity.Error, "Sign-in needed", "Stopped: signed out"),
            BatchOutcome.TimedOut => (InfoBarSeverity.Warning, "Batch took too long", "Stopped: batch too slow"),
            BatchOutcome.Stopped => (InfoBarSeverity.Informational, "Stopped", "Stopped"),
            _ => (InfoBarSeverity.Error, "Night mode stopped", "Stopped: something unexpected"),
        };
        var extra = r.Outcome is BatchOutcome.StudioLimit or BatchOutcome.TimedOut or BatchOutcome.Error
            ? " Files Studio had already ticked are marked uploaded; the rest of that batch will be tried again next run."
            : "";
        ResultBar.Message = r.Message + extra;
        ScreenshotButton.Visibility = Ui.Show(_screenshot is not null);
        ResultBar.IsOpen = true;
        StatusDetail.Text = "";
        Add(r.Message);
    }

    private void OnOpenScreenshot(object sender, RoutedEventArgs e)
    {
        if (_screenshot is null) return;
        try { Process.Start(new ProcessStartInfo(_screenshot) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Couldn't open screenshot: {ex.Message}"); }
    }

    // Keep the PC from sleeping while night mode runs.
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    private static void KeepAwake(bool on)
    {
        const uint continuous = 0x80000000, system = 0x00000001;
        try { SetThreadExecutionState(on ? continuous | system : continuous); } catch (Exception) { }
    }
}
