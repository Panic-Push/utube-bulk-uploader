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
    private NightRunner? _runner;
    private bool _finishing;
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
            : $"{Format.Count(clips, "clip")} waiting · one video at a time, at least {_host.Settings.SecondsPerVideo} s each";
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
                "Night mode will upload the waiting clips one by one to the YouTube channel signed in to its Chrome window: " +
                "title from the file name, description with the file name and today's date, not made for kids, Private. " +
                "Each video is saved only once its upload is complete; one still not uploaded after 12 minutes is saved anyway and night mode moves on " +
                "(3 like that in a row stop the night).\n\n" +
                "Don't use that Chrome window while it runs. Keep this PC awake and connected.", "Start")) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _finishing = false;
        StopLabel.Text = "Stop";
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
            var driver = new StudioDriver(tab, target) { MinPerVideo = TimeSpan.FromSeconds(_host.Settings.SecondsPerVideo) };
            await driver.InitAsync(ct);
            var runner = new NightRunner();
            _runner = runner;
            runner.Activity += Add;
            runner.Progress += p => _host.Ui(() => ShowProgress(p));
            result = await Task.Run(() => runner.RunAsync(driver, _host.LogFile, ct), CancellationToken.None);
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
        _runner = null;
        _finishing = false;
        StopLabel.Text = "Stop";
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
        if (!_finishing && _runner is not null)
        {
            // First press: finish the video in progress so it isn't left broken.
            _finishing = true;
            _runner.FinishAndStop();
            StopLabel.Text = "Stop now";
            StatusTitle.Text = "Finishing uploads in progress…";
            Add("Stop requested: finishing the video in progress, then stopping. Press Stop now to stop at once.");
            return;
        }
        StopButton.IsEnabled = false;
        StatusTitle.Text = "Stopping…";
        Add("Stopping now. The video in progress will be uploaded again next run.");
        _cts?.Cancel();
    }

    private void ShowProgress(NightProgress p)
    {
        if (!_finishing) StatusTitle.Text = p.Uploading == 0 ? "Working" : "Uploading";
        StatusDetail.Text = p.Status;
        var total = Math.Max(1, p.Uploaded + p.Failed + p.Uploading + p.Waiting);
        Bar.Maximum = total;
        Bar.Value = p.Uploaded + p.Failed;
        StatRun.Text = p.Uploaded.ToString();
        StatFailed.Text = p.Failed.ToString();
        StatToday.Text = (_startToday + p.Uploaded).ToString();
        StatLeft.Text = p.Waiting.ToString();
        _nextAt = p.NextVideoAt;
        if (_nextAt is not null) { _countdown.Start(); UpdateCountdown(); }
        else _countdown.Stop();
    }

    private void UpdateCountdown()
    {
        if (_nextAt is not { } at) return;
        var left = at - DateTime.Now;
        StatusDetail.Text = left > TimeSpan.Zero ? $"Next video in {(int)left.TotalSeconds} s" : "Starting the next video";
    }

    private void ShowResult(BatchResult r)
    {
        _screenshot = r.ScreenshotPath;
        (ResultBar.Severity, ResultBar.Title, StatusTitle.Text) = r.Outcome switch
        {
            BatchOutcome.Done => (InfoBarSeverity.Success, "Finished", "Finished"),
            BatchOutcome.StudioLimit => (InfoBarSeverity.Warning, "Studio said stop", "Stopped: Studio's limit"),
            BatchOutcome.SignedOut => (InfoBarSeverity.Error, "Sign-in needed", "Stopped: signed out"),
            BatchOutcome.TimedOut => (InfoBarSeverity.Warning, "Uploads too slow", "Stopped: 3 uploads in a row too slow"),
            BatchOutcome.Stopped => (InfoBarSeverity.Informational, "Stopped", "Stopped"),
            _ => (InfoBarSeverity.Error, "Night mode stopped", "Stopped: something unexpected"),
        };
        var extra = r.Outcome is BatchOutcome.StudioLimit or BatchOutcome.TimedOut or BatchOutcome.Error
            ? " Videos that finished are marked uploaded in the log; the rest are tried again next run."
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
