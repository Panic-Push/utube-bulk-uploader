using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UTubeBulkUploader.Core;

public enum BatchOutcome { Done, StudioLimit, SignedOut, TimedOut, Stopped, Error }

public sealed record BatchResult(BatchOutcome Outcome, string Message, string? ScreenshotPath = null);

/// <summary>Where night mode sends files: real YouTube Studio, or the practice copy shipped with the app.</summary>
public sealed class StudioTarget
{
    public required bool Practice { get; init; }
    public required string HomeUrl { get; init; }

    public static StudioTarget Real => new() { Practice = false, HomeUrl = "https://studio.youtube.com/" };

    /// <summary>The practice page, written next to the app's state so Chrome can open it as a file.</summary>
    public static StudioTarget PracticePage(string? query = null)
    {
        var dir = Path.Combine(AppPaths.StateDir, "practice");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "practice-studio.html");
        using (var s = typeof(StudioTarget).Assembly.GetManifestResourceStream("UTubeBulkUploader.Core.practice-studio.html")!)
        using (var f = File.Create(path)) s.CopyTo(f);
        return new StudioTarget { Practice = true, HomeUrl = new Uri(path).AbsoluteUri + (query is null ? "" : "?" + query) };
    }
}

/// <summary>
/// Drives one Studio tab: opens the upload box, hands it a batch of files (Studio's own "Select files" route,
/// which is the same as dropping them), and watches until Studio has finished uploading all of them.
/// Anything unexpected stops the run with a message and a screenshot rather than guessing.
/// </summary>
public sealed class StudioDriver
{
    private readonly CdpTab _tab;
    private readonly StudioTarget _target;
    private readonly HashSet<string> _uploadRequests = new();
    private DateTime _lastNetworkActivity = DateTime.MinValue;
    private readonly object _lock = new();

    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromMinutes(25);
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public string ScreenshotDir { get; init; } = Path.Combine(AppPaths.StateDir, "night-screens");

    /// <summary>Live status for the screen, e.g. "Studio is uploading (3 connections)".</summary>
    public event Action<string>? Status;

    // Busy and stop wording Studio uses. Matching is done on visible text only.
    private static readonly Regex Busy = new(@"\b(Uploading|Waiting to upload|Preparing upload|Starting upload|Upload starting)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Limit = new(
        @"daily upload limit|upload limit|reached (the|your) (daily )?limit|limit reached|uploading too many|try again (later|tomorrow)|exceeded the number of videos",
        RegexOptions.IgnoreCase);

    public StudioDriver(CdpTab tab, StudioTarget target)
    {
        _tab = tab;
        _target = target;
        _tab.Event += OnEvent;
    }

    public async Task InitAsync(CancellationToken ct)
    {
        await _tab.SendAsync("Page.enable", ct: ct);
        await _tab.SendAsync("Network.enable", ct: ct);
        await _tab.SendAsync("Runtime.enable", ct: ct);
    }

    private void OnEvent(string method, JsonNode? p)
    {
        switch (method)
        {
            case "Network.requestWillBeSent":
                var url = (string?)p?["request"]?["url"] ?? "";
                var m = (string?)p?["request"]?["method"] ?? "";
                if (IsUploadUrl(url) && m is "POST" or "PUT")
                    lock (_lock) { _uploadRequests.Add((string)p!["requestId"]!); _lastNetworkActivity = DateTime.UtcNow; }
                break;
            case "Network.loadingFinished":
            case "Network.loadingFailed":
                lock (_lock) { if (_uploadRequests.Remove((string?)p?["requestId"] ?? "")) _lastNetworkActivity = DateTime.UtcNow; }
                break;
            case "Network.dataReceived":
                break;
            case "Page.javascriptDialogOpening":
                // "Leave site?" prompts only appear when we move on after a finished batch.
                _ = _tab.SendAsync("Page.handleJavaScriptDialog", new { accept = true });
                break;
        }
    }

    public static bool IsUploadUrl(string url) =>
        url.Contains("upload.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/upload/", StringComparison.OrdinalIgnoreCase) && url.Contains("youtube", StringComparison.OrdinalIgnoreCase);

    private int ActiveUploads { get { lock (_lock) return _uploadRequests.Count; } }
    private DateTime LastActivity { get { lock (_lock) return _lastNetworkActivity; } }

    // A small script added before each call: finds elements and text inside Studio's nested components too.
    private const string Helpers = """
        const __all = () => { const out = []; const walk = r => { for (const e of r.querySelectorAll('*')) { out.push(e); if (e.shadowRoot) walk(e.shadowRoot); } }; walk(document); return out; };
        const __visibleText = () => { const parts = []; const walk = r => { const tw = document.createTreeWalker(r, NodeFilter.SHOW_TEXT); let n; while ((n = tw.nextNode())) { const el = n.parentElement; if (el && el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden') parts.push(n.nodeValue); } for (const e of r.querySelectorAll('*')) if (e.shadowRoot) walk(e.shadowRoot); }; walk(document.body || document); return parts.join(' ').replace(/\s+/g, ' '); };
        const __fileInput = () => __all().find(e => e.tagName === 'INPUT' && e.type === 'file');
        """;

    private Task<JsonNode?> Js(string body, CancellationToken ct) => _tab.EvalAsync("(() => {" + Helpers + body + "})()", ct);

    private async Task<string> UrlAsync(CancellationToken ct) => (string?)await Js("return location.href;", ct) ?? "";

    private async Task NavigateAsync(string url, CancellationToken ct)
    {
        await _tab.SendAsync("Page.navigate", new { url }, ct);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(500, ct);
            try { if ((string?)await Js("return document.readyState;", ct) == "complete") return; }
            catch (NightModeException) { /* page still switching */ }
        }
    }

    private static bool IsSignInPage(string url) =>
        url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) || url.Contains("ServiceLogin", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> CaptureAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var r = await _tab.SendAsync("Page.captureScreenshot", new { format = "png" }, ct);
            if ((string?)r?["data"] is not { } data) return null;
            Directory.CreateDirectory(ScreenshotDir);
            var path = Path.Combine(ScreenshotDir, $"{DateTime.Now:yyyy-MM-dd HHmmss} {name}.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(data), ct);
            return path;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Opens Studio's upload box. Returns null when ready, else a stop result.</summary>
    private async Task<BatchResult?> OpenUploadBoxAsync(CancellationToken ct)
    {
        Status?.Invoke("Opening YouTube Studio");
        await NavigateAsync(_target.HomeUrl, ct);
        string url = await UrlAsync(ct);
        string? uploadUrl = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            url = await UrlAsync(ct);
            if (IsSignInPage(url)) return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Sign in in the app's Chrome window, then start again.");
            if (_target.Practice) { uploadUrl = _target.HomeUrl.Contains('?') ? _target.HomeUrl + "&d=ud" : _target.HomeUrl + "?d=ud"; break; }
            var m = Regex.Match(url, @"^(https://studio\.youtube\.com/channel/[A-Za-z0-9_-]+)");
            if (m.Success) { uploadUrl = m.Groups[1].Value + "/videos/upload?d=ud"; break; }
            await Task.Delay(1000, ct);
        }
        if (uploadUrl is not null)
        {
            Status?.Invoke("Opening the upload box");
            await NavigateAsync(uploadUrl, ct);
        }

        // Wait for the box's file picker; if the direct link didn't open it, press Studio's upload button.
        sw.Restart();
        var clicked = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (IsSignInPage(await UrlAsync(ct))) return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Sign in in the app's Chrome window, then start again.");
            if ((bool?)await Js("return !!__fileInput();", ct) == true) return null;
            if (!clicked && sw.Elapsed > TimeSpan.FromSeconds(8))
            {
                clicked = (bool?)await Js("""
                    const b = __all().find(e => e.id === 'upload-icon' || e.id === 'upload-button' ||
                        ((e.getAttribute('aria-label') || '').toLowerCase().startsWith('upload video')));
                    if (b) { b.click(); return true; } return false;
                    """, ct) == true;
            }
            await Task.Delay(1000, ct);
        }
        return new BatchResult(BatchOutcome.Error, "Couldn't find Studio's upload box. Studio may have changed its page.",
            await CaptureAsync("upload box not found", ct));
    }

    /// <summary>Uploads one batch and waits for Studio to finish it.</summary>
    public async Task<BatchResult> UploadBatchAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        try
        {
            foreach (var f in files)
                if (!File.Exists(f)) return new BatchResult(BatchOutcome.Error, $"A file in this batch is missing: {f}");

            if (await OpenUploadBoxAsync(ct) is { } stop) return stop;

            var input = await _tab.SendAsync("Runtime.evaluate", new { expression = "(() => {" + Helpers + "return __fileInput();})()" }, ct);
            var objectId = (string?)input?["result"]?["objectId"];
            if (objectId is null) return new BatchResult(BatchOutcome.Error, "Studio's file picker disappeared.", await CaptureAsync("no file picker", ct));
            lock (_lock) { _uploadRequests.Clear(); _lastNetworkActivity = DateTime.MinValue; }
            Status?.Invoke($"Handing {files.Count} files to Studio");
            await _tab.SendAsync("DOM.setFileInputFiles", new { files, objectId }, ct);
            await Js("const i = __fileInput(); if (i) { i.dispatchEvent(new Event('change', { bubbles: true })); } return true;", ct).ContinueWith(_ => { }, ct);

            return await WatchAsync(files.Count, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BatchResult(BatchOutcome.Stopped, "Stopped.");
        }
        catch (NightModeException ex)
        {
            return new BatchResult(BatchOutcome.Error, ex.Message, _tab.Closed ? null : await CaptureAsync("error"));
        }
    }

    private async Task<BatchResult> WatchAsync(int count, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var started = false;
        DateTime? quietSince = null;
        while (true)
        {
            await Task.Delay(PollInterval, ct);
            var url = await UrlAsync(ct);
            if (IsSignInPage(url))
                return new BatchResult(BatchOutcome.SignedOut, "YouTube signed Chrome out during the batch. Sign in again in the app's Chrome window.", await CaptureAsync("signed out", ct));

            var text = (string?)await Js("return __visibleText();", ct) ?? "";
            if (Limit.Match(text) is { Success: true } lm)
            {
                var around = Snippet(text, lm.Index);
                return new BatchResult(BatchOutcome.StudioLimit, $"Studio says stop: \"{around}\"", await CaptureAsync("studio limit", ct));
            }

            var busyText = Busy.IsMatch(text);
            var active = ActiveUploads;
            if (busyText || active > 0) started = true;
            var networkQuiet = active == 0 && (LastActivity == DateTime.MinValue || DateTime.UtcNow - LastActivity > QuietPeriod);

            var mins = (int)sw.Elapsed.TotalMinutes;
            Status?.Invoke(busyText || active > 0
                ? $"Studio is uploading {count} file{(count == 1 ? "" : "s")} · {mins} min so far"
                : started ? "Uploads look finished, checking…" : "Waiting for Studio to start uploading…");

            if (started && !busyText && networkQuiet)
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince.Value >= QuietPeriod)
                    return new BatchResult(BatchOutcome.Done, $"Studio finished {count} file{(count == 1 ? "" : "s")} in {Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMinutes))} min.");
            }
            else quietSince = null;

            if (!started && sw.Elapsed > StartTimeout)
                return new BatchResult(BatchOutcome.Error, "Studio didn't start uploading the files.", await CaptureAsync("not started", ct));
            if (sw.Elapsed > BatchTimeout)
                return new BatchResult(BatchOutcome.TimedOut, $"This batch took more than {BatchTimeout.TotalMinutes:0} minutes, so night mode stopped to be safe.",
                    await CaptureAsync("too slow", ct));
        }
    }

    private static string Snippet(string text, int index)
    {
        var start = Math.Max(0, index - 60);
        var space = text.IndexOf(' ', start);
        if (start > 0 && space >= 0 && space < index) start = space + 1;   // begin at a whole word
        var len = Math.Min(text.Length - start, 160);
        return text.Substring(start, len).Trim();
    }
}

public sealed record NightProgress(string Status, int BatchNumber, int BatchCount, int ClipsDone, int ClipsTotal, DateTime? NextBatchAt = null);

/// <summary>Runs batches one after another with a pause in between, until done, stopped, or Studio says stop.</summary>
public sealed class NightRunner
{
    public TimeSpan Gap { get; init; } = TimeSpan.FromMinutes(2);
    public event Action<NightProgress>? Progress;
    public event Action<string>? Activity;

    public async Task<BatchResult> RunAsync(StudioDriver driver, LogStore log, int batchSize, bool practice, CancellationToken ct)
    {
        var batches = BatchPlanner.Plan(log, batchSize, out _);
        var total = batches.Sum(b => b.Rows.Count);
        if (batches.Count == 0) return new BatchResult(BatchOutcome.Done, "Nothing to upload: every renamed clip is already uploaded.");
        var done = 0;
        driver.Status += s => Progress?.Invoke(new NightProgress(s, CurrentBatch, batches.Count, done, total));
        for (var i = 0; i < batches.Count; i++)
        {
            CurrentBatch = i + 1;
            var b = batches[i];
            Activity?.Invoke($"Batch {b.Number} of {batches.Count}: {b.Rows.Count} clips ({b.FirstName} … {b.LastName})");
            var r = await driver.UploadBatchAsync(b.Paths.ToList(), ct);
            if (r.Outcome != BatchOutcome.Done)
            {
                Activity?.Invoke(r.Message);
                return r;
            }
            done += b.Rows.Count;
            if (practice) Activity?.Invoke($"Batch {b.Number} finished on the practice page (log not changed).");
            else
            {
                var saved = BatchPlanner.MarkUploaded(log, b, $"Uploaded by night mode, batch {b.Number}.");
                Activity?.Invoke($"Batch {b.Number} done and marked uploaded in the log." + (saved ? "" : " (The log is open in Excel; it will be written when you close it.)"));
            }
            if (i < batches.Count - 1)
            {
                var next = DateTime.Now + Gap;
                Progress?.Invoke(new NightProgress($"Pausing before the next batch", CurrentBatch, batches.Count, done, total, next));
                try { await Task.Delay(Gap, ct); }
                catch (OperationCanceledException) { return new BatchResult(BatchOutcome.Stopped, "Stopped."); }
            }
        }
        Progress?.Invoke(new NightProgress("All batches finished", batches.Count, batches.Count, done, total));
        return new BatchResult(BatchOutcome.Done, $"All {batches.Count} batch{(batches.Count == 1 ? "" : "es")} ({total} clips) finished.");
    }

    private int CurrentBatch { get; set; }
}
