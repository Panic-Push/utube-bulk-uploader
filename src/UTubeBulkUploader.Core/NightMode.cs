using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UTubeBulkUploader.Core;

public enum BatchOutcome { Done, StudioLimit, SignedOut, TimedOut, Stopped, Error }

public sealed record BatchResult(BatchOutcome Outcome, string Message, string? ScreenshotPath = null);

/// <summary>Where night mode sends files: real YouTube Studio, or the test copy used by the automated tests.</summary>
public sealed class StudioTarget
{
    public required bool Practice { get; init; }
    public required string HomeUrl { get; init; }

    public static StudioTarget Real => new() { Practice = false, HomeUrl = "https://studio.youtube.com/" };

    /// <summary>The test page (automated tests only), written next to the app's state so Chrome can open it as a file.</summary>
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

/// <summary>What happened when night mode tried to put one video on YouTube.</summary>
/// <param name="Saved">Studio saved the video (title, description, audience, Private).</param>
/// <param name="Upload">Whether the file finished uploading before Save: Uploaded, Failed (Studio said so), or Unknown (cut-off).</param>
public sealed record VideoStart(bool Saved, string Message, string? Link = null, bool FileHandedOver = false,
    BatchOutcome Stop = BatchOutcome.Error, string? ScreenshotPath = null, UploadState Upload = UploadState.Unknown, bool CutOff = false);

/// <summary>Where a saved video's upload stands, read from Studio's upload box.</summary>
public enum UploadState { Unknown, Uploading, Uploaded, Failed }

/// <summary>The details night mode fills in for every video.</summary>
public static class VideoDetails
{
    /// <summary>"File Name: Collector Visit - Phone 1 - 1 (26.10.26), Upload Date: 01.11.26"</summary>
    public static string Description(string title, DateTime uploadDate) =>
        $"File Name: {title}, Upload Date: {uploadDate.ToString("dd.MM.yy", CultureInfo.InvariantCulture)}";

    public static string Title(string path) => Path.GetFileNameWithoutExtension(path);
}

/// <summary>
/// Drives one YouTube Studio tab, one video at a time, the way a person would: Create → Upload videos, pick the
/// file, type the title and description, answer "not made for kids", Next to the visibility step, Private, Save.
/// The file carries on uploading in the background while the next video is started; progress is read from
/// Studio's upload box ("100% uploaded"). The page is never reloaded while anything is uploading.
/// Anything unexpected stops with a message, a screenshot and the page's text rather than guessing.
/// </summary>
public sealed class StudioDriver
{
    private readonly CdpTab _tab;
    private readonly StudioTarget _target;
    private readonly HashSet<string> _uploadRequests = new();
    private DateTime _lastNetworkActivity = DateTime.MinValue;
    private int _serverConfirmed;
    private readonly object _lock = new();

    /// <summary>How long one screen of Studio may take to appear.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>A file not finished uploading after this long is saved anyway and night mode moves on.</summary>
    public TimeSpan UploadCutoff { get; init; } = TimeSpan.FromMinutes(12);
    /// <summary>An upload whose percentage hasn't moved for this long is treated as stuck: saved anyway, night mode moves on.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>How long to watch for Studio's after-save boxes ("Video processing" and the like) to close them.</summary>
    public TimeSpan AfterSaveWatch { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Each video takes at least this long, however fast Studio is.</summary>
    public TimeSpan MinPerVideo { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Short pause after each click so Studio can react, as a person would.</summary>
    public TimeSpan ClickPause { get; init; } = TimeSpan.FromMilliseconds(1200);
    public string ScreenshotDir { get; init; } = Path.Combine(AppPaths.StateDir, "night-screens");

    public event Action<string>? Status;

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
            case "Network.responseReceived":
                // YouTube's upload server answers "final" once it has received a whole file.
                if (IsUploadUrl((string?)p?["response"]?["url"] ?? "") && p?["response"]?["headers"] is JsonObject headers &&
                    headers.Any(h => h.Key.Equals("x-goog-upload-status", StringComparison.OrdinalIgnoreCase) &&
                                     string.Equals((string?)h.Value, "final", StringComparison.OrdinalIgnoreCase)))
                    lock (_lock) { _serverConfirmed++; _lastNetworkActivity = DateTime.UtcNow; }
                break;
            case "Network.loadingFinished":
            case "Network.loadingFailed":
                lock (_lock) { if (_uploadRequests.Remove((string?)p?["requestId"] ?? "")) _lastNetworkActivity = DateTime.UtcNow; }
                break;
            case "Page.javascriptDialogOpening":
                // "Leave site?" only appears when night mode refreshes with nothing uploading.
                _ = _tab.SendAsync("Page.handleJavaScriptDialog", new { accept = true });
                break;
        }
    }

    public static bool IsUploadUrl(string url) =>
        url.Contains("upload.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/upload/", StringComparison.OrdinalIgnoreCase) && url.Contains("youtube", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files YouTube's upload server has confirmed receiving in full (all videos so far).</summary>
    public int ServerConfirmed { get { lock (_lock) return _serverConfirmed; } }
    public bool UploadTrafficActive { get { lock (_lock) return _uploadRequests.Count > 0; } }

    // ---------- Page helpers (they also look inside Studio's nested components) ----------

    private const string Helpers = """
        const __roots = () => { const out = []; const walk = r => { out.push(r); for (const e of r.querySelectorAll('*')) if (e.shadowRoot) walk(e.shadowRoot); }; walk(document); return out; };
        const __all = () => { const out = []; for (const r of __roots()) for (const e of r.querySelectorAll('*')) out.push(e); return out; };
        const __up = n => n.parentNode instanceof ShadowRoot ? n.parentNode.host : n.parentNode;
        const __vis = e => { if (!e || !e.getClientRects || !e.getClientRects().length) return false; const s = getComputedStyle(e); return s.visibility !== 'hidden' && s.display !== 'none'; };
        const __txt = e => (e.innerText || e.textContent || '').replace(/\s+/g, ' ').trim();
        const __visibleText = () => { const parts = []; for (const r of __roots()) { const tw = document.createTreeWalker(r, NodeFilter.SHOW_TEXT); let n; while ((n = tw.nextNode())) { const el = n.parentElement; if (el && __vis(el)) parts.push(n.nodeValue); } } return parts.join(' ').replace(/\s+/g, ' '); };
        const __ctext = n => { if (n.nodeType === 3) return n.nodeValue; if (n.nodeName === 'STYLE' || n.nodeName === 'SCRIPT') return ''; let s = ''; if (n.shadowRoot) for (const k of n.shadowRoot.childNodes) s += __ctext(k) + ' '; for (const k of n.childNodes) s += __ctext(k) + ' '; return s; };
        const __inside = (e, id) => { for (let x = e, i = 0; x && i < 12; x = __up(x), i++) if (x.id === id) return true; return false; };
        const __clickable = e => { for (let x = e, i = 0; x && i < 4; x = __up(x), i++) if (['BUTTON','YTCP-BUTTON','TP-YT-PAPER-ITEM','YTCP-ICON-BUTTON','TP-YT-PAPER-ICON-BUTTON','A'].includes(x.tagName) || x.getAttribute && x.getAttribute('role') === 'button' || x.getAttribute && x.getAttribute('role') === 'menuitem') return x; return e; };
        const __button = (ids, words) => { const all = __all().filter(__vis);
            for (const id of ids) { const b = all.find(e => e.id === id); if (b) return b; }
            for (const w of words) { const b = all.find(e => ['BUTTON','YTCP-BUTTON','TP-YT-PAPER-ITEM'].includes(e.tagName) && __txt(e).toLowerCase() === w); if (b) return b; }
            for (const w of words) { const b = all.find(e => e.children.length === 0 && __txt(e).toLowerCase() === w); if (b) return __clickable(b); }
            return null; };
        const __fileInput = () => { const inputs = __all().filter(e => e.tagName === 'INPUT' && e.type === 'file');
            return inputs.find(i => { let x = __up(i); while (x && x.nodeType === 1 && getComputedStyle(x).display === 'contents') x = __up(x); return x && x.nodeType === 1 && __vis(x); }) || null; };
        const __titleBox = () => __all().find(e => e.isContentEditable && __vis(e) && e.id === 'textbox' && __inside(e, 'title-textarea'))
            || __all().find(e => e.isContentEditable && __vis(e) && /title/i.test(e.getAttribute('aria-label') || ''));
        const __descBox = () => __all().find(e => e.isContentEditable && __vis(e) && e.id === 'textbox' && __inside(e, 'description-textarea'))
            || __all().find(e => e.isContentEditable && __vis(e) && /tell viewers|description/i.test(e.getAttribute('aria-label') || ''));
        const __radio = (name, words) => { const all = __all().filter(__vis);
            return all.find(e => e.getAttribute('name') === name && (e.getAttribute('role') === 'radio' || /RADIO/.test(e.tagName)))
                || all.find(e => (e.getAttribute('role') === 'radio' || /RADIO-BUTTON/.test(e.tagName)) && words.some(w => __txt(e).toLowerCase().startsWith(w))); };
        const __dialog = () => __all().find(e => /UPLOADS-DIALOG/.test(e.tagName) && __vis(e)) || (() => {
            const b = __all().find(e => (e.id === 'done-button' || e.id === 'next-button') && __vis(e)); if (!b) return null;
            let d = b; for (let i = 0; i < 40 && d; i++) { d = __up(d); if (d && /DIALOG/.test(d.tagName || '')) return d; } return null; })();
        const __checked = e => !!e && (e.getAttribute('aria-checked') === 'true' || e.hasAttribute('checked') || e.checked === true);
        """;

    private Task<JsonNode?> Js(string body, CancellationToken ct) => _tab.EvalAsync("(() => {" + Helpers + body + "})()", ct);
    private async Task<bool> JsBool(string body, CancellationToken ct) => (bool?)await Js(body, ct) == true;
    private async Task<string> UrlAsync(CancellationToken ct) => (string?)await Js("return location.href;", ct) ?? "";
    private async Task<string> VisibleTextAsync(CancellationToken ct) => (string?)await Js("return __visibleText();", ct) ?? "";

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

    /// <summary>Waits until a page check returns true. Returns false on timeout.</summary>
    private async Task<bool> WaitFor(string check, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try { if (await JsBool(check, ct)) return true; }
            catch (NightModeException) when (!_tab.Closed) { /* page busy */ }
            await Task.Delay(500, ct);
        }
        return false;
    }

    private static bool IsSignInPage(string url) =>
        url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) || url.Contains("ServiceLogin", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> CaptureAsync(string name, CancellationToken ct = default, string? text = null)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotDir);
            var stem = Path.Combine(ScreenshotDir, $"{DateTime.Now:yyyy-MM-dd HHmmss} {name}");
            text ??= await VisibleTextAsync(ct);
            await File.WriteAllTextAsync(stem + ".txt", text, ct);
            var r = await _tab.SendAsync("Page.captureScreenshot", new { format = "png" }, ct);
            if ((string?)r?["data"] is not { } data) return null;
            await File.WriteAllBytesAsync(stem + ".png", Convert.FromBase64String(data), ct);
            return stem + ".png";
        }
        catch (Exception) { return null; }
    }

    // ---------- Pre-flight and page state ----------

    /// <summary>Opens Studio's home page, checks it's signed in and in English. Null when all is well.</summary>
    public async Task<BatchResult?> PreflightAsync(Action<string> report, CancellationToken ct)
    {
        Status?.Invoke("Checking YouTube Studio");
        if (await GoHomeAsync(ct) is { } stop) return stop;
        report("✓ Signed in to YouTube Studio");

        var lang = (string?)await Js("return (document.documentElement.lang || '').toLowerCase();", ct) ?? "";
        var english = lang.StartsWith("en") || (lang.Length == 0 && Regex.IsMatch(await VisibleTextAsync(ct), @"\b(Dashboard|Content|Upload)\b"));
        if (!english)
            return new BatchResult(BatchOutcome.Error,
                $"YouTube Studio isn't in English (it's set to \"{lang}\"). Night mode reads Studio's English words. " +
                "In Studio click your channel picture > Language > English, then start again.", await CaptureAsync("not english", ct));
        report("✓ Studio is in English");
        return null;
    }

    /// <summary>Loads Studio's home page. Only call when nothing is uploading: it reloads the page.</summary>
    public async Task<BatchResult?> GoHomeAsync(CancellationToken ct)
    {
        await NavigateAsync(_target.HomeUrl, ct);
        if (_target.Practice) return null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            var url = await UrlAsync(ct);
            if (IsSignInPage(url))
                return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Click Sign in on this screen, sign in to the channel, close that Chrome window, then start again.");
            if (url.Contains("studio.youtube.com/channel/", StringComparison.OrdinalIgnoreCase)) return null;
            await Task.Delay(1000, ct);
        }
        return new BatchResult(BatchOutcome.Error, "YouTube Studio didn't open. Check the internet connection, then start again.", await CaptureAsync("studio did not open", ct));
    }

    /// <summary>Signed-out page or Studio's limit message, if showing.</summary>
    public async Task<BatchResult?> CheckStopSignsAsync(CancellationToken ct)
    {
        if (IsSignInPage(await UrlAsync(ct)))
            return new BatchResult(BatchOutcome.SignedOut, "YouTube signed Chrome out. Click Sign in on this screen, then start again.", await CaptureAsync("signed out", ct));
        var text = await VisibleTextAsync(ct);
        if (Limit.Match(text) is { Success: true } lm)
            return new BatchResult(BatchOutcome.StudioLimit, $"Studio says stop: \"{Snippet(text, lm.Index)}\"", await CaptureAsync("studio limit", ct, text));
        return null;
    }

    // ---------- One video ----------

    /// <summary>Uploads one file and saves its details. Returns once Studio has saved it (the file may still be uploading).</summary>
    public async Task<VideoStart> StartVideoAsync(string path, DateTime uploadDate, CancellationToken ct)
    {
        var title = VideoDetails.Title(path);
        var description = VideoDetails.Description(title, uploadDate);
        var handedOver = false;
        try
        {
            if (!File.Exists(path)) return new VideoStart(false, $"The file is missing: {path}");
            if (await CheckStopSignsAsync(ct) is { } sign) return new VideoStart(false, sign.Message, Stop: sign.Outcome, ScreenshotPath: sign.ScreenshotPath);

            // 1. Create → Upload videos
            Status?.Invoke($"Opening the upload box for {title}");
            await CloseNoticesAsync(TimeSpan.Zero, ct);
            if (!await OpenUploadBoxAsync(ct))
                return await Fail("Couldn't open Studio's upload box (Create → Upload videos).", "upload box not found", handedOver, ct);

            // 2. The file
            Status?.Invoke($"Giving Studio {title}");
            var input = await _tab.SendAsync("Runtime.evaluate", new { expression = "(() => {" + Helpers + "return __fileInput();})()" }, ct);
            var objectId = (string?)input?["result"]?["objectId"];
            if (objectId is null) return await Fail("Studio's file picker disappeared.", "no file picker", handedOver, ct);
            await _tab.SendAsync("DOM.setFileInputFiles", new { files = new[] { path }, objectId }, ct);
            handedOver = true;
            var started = Stopwatch.StartNew();

            // 3. Details screen: title and description. (Chrome tells Studio about the file itself; only if
            // nothing happens is the "file chosen" signal sent again, so a file is never handed over twice.)
            var details = await WaitFor("return !!__titleBox();", TimeSpan.FromSeconds(8), ct);
            if (!details)
            {
                await Js("const i = __fileInput(); if (i && !__titleBox()) i.dispatchEvent(new Event('change', { bubbles: true })); return true;", ct);
                details = await WaitFor("return !!__titleBox();", StepTimeout, ct);
            }
            if (!details)
            {
                if (await CheckStopSignsAsync(ct) is { } s2) return new VideoStart(false, s2.Message, Stop: s2.Outcome, ScreenshotPath: s2.ScreenshotPath);
                return await Fail("Studio's details screen (title box) didn't appear.", "no details screen", handedOver, ct);
            }
            await Pause(ct);
            if (!await TypeIntoAsync("__titleBox()", title, ct))
                return await Fail("Couldn't type the title.", "title not set", handedOver, ct);
            if (!await WaitFor("return !!__descBox();", TimeSpan.FromSeconds(10), ct) || !await TypeIntoAsync("__descBox()", description, ct))
                return await Fail("Couldn't type the description.", "description not set", handedOver, ct);

            // 4. Not made for kids
            Status?.Invoke($"{title}: audience");
            if (!await ClickRadioAsync("VIDEO_MADE_FOR_KIDS_NOT_MFK", "[\"no, it's not made for kids\", \"no, it’s not made for kids\", \"no\"]", ct))
                return await Fail("Couldn't choose \"No, it's not made for kids\".", "kids answer not set", handedOver, ct);

            // 5. Next until the visibility step
            Status?.Invoke($"{title}: going to Visibility");
            var reached = false;
            for (var i = 0; i < 8 && !reached; i++)
            {
                if (await JsBool("return !!__radio('PRIVATE', ['private']);", ct)) { reached = true; break; }
                if (!await JsBool("const b = __button(['next-button'], ['next']); if (!b) return false; b.click(); return true;", ct))
                {
                    // Next may be briefly hidden while a step loads.
                    if (!await WaitFor("return !!__button(['next-button'], ['next']) || !!__radio('PRIVATE', ['private']);", TimeSpan.FromSeconds(10), ct))
                        return await Fail("Couldn't find the Next button.", "no next button", handedOver, ct);
                    continue;
                }
                await Pause(ct);
                reached = await WaitFor("return !!__radio('PRIVATE', ['private']);", TimeSpan.FromSeconds(2), ct);
            }
            if (!reached) return await Fail("Studio's Visibility step didn't appear.", "no visibility step", handedOver, ct);

            // 6. Private
            Status?.Invoke($"{title}: Private");
            if (!await ClickRadioAsync("PRIVATE", "[\"private\"]", ct))
                return await Fail("Couldn't choose Private.", "private not set", handedOver, ct);
            var link = (string?)await Js("""
                const a = __all().find(e => e.tagName === 'A' && /youtu\.be\/|youtube\.com\/(watch|shorts)/.test(e.href || ''));
                if (a) return a.href;
                const m = __visibleText().match(/https:\/\/(youtu\.be\/[\w-]+|www\.youtube\.com\/watch\?v=[\w-]+)/);
                return m ? m[0] : null;
                """, ct);

            // 7. Stay on the details screen until its progress line says the upload is complete.
            var (upload, uploadNote) = await WaitForUploadAsync(title, started, ct);
            if (upload == UploadState.Unknown && _stopSign is { } stopSign)
                return new VideoStart(false, stopSign.Message, link, handedOver, stopSign.Outcome, stopSign.ScreenshotPath);
            var cutOff = upload == UploadState.Unknown;
            string? cutShot = cutOff ? await CaptureAsync(uploadNote.StartsWith("stuck") ? "upload stuck" : "upload cut-off", ct) : null;
            var left = MinPerVideo - started.Elapsed;
            if (left > TimeSpan.Zero) { Status?.Invoke($"{title}: uploaded, saving shortly"); await Task.Delay(left, ct); }

            // 8. Save, then close any "still uploading" / "video saved" notice
            Status?.Invoke($"{title}: saving");
            if (!await JsBool("const b = __button(['done-button'], ['save', 'publish']); if (!b) return false; b.click(); return true;", ct))
                return await Fail("Couldn't find the Save button.", "no save button", handedOver, ct);
            await Pause(ct);
            var closed = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < StepTimeout)
            {
                if (!await JsBool("return !!__dialog();", ct)) { closed = true; break; }
                await Task.Delay(700, ct);
            }
            if (!closed) return await Fail("Studio didn't confirm the save (the details screen stayed open).", "not saved", handedOver, ct);
            await CloseNoticesAsync(AfterSaveWatch, ct);

            return upload switch
            {
                UploadState.Uploaded => new VideoStart(true, "Uploaded and saved as Private.", link, handedOver, Upload: UploadState.Uploaded),
                UploadState.Failed => new VideoStart(true, $"Studio: {uploadNote}", link, handedOver, Upload: UploadState.Failed),
                _ => new VideoStart(true, uploadNote.StartsWith("stuck")
                        ? $"Upload {uploadNote} for {Clock(StuckAfter)}; saved anyway and moved on."
                        : $"Not uploaded after {Clock(UploadCutoff)}; saved anyway and moved on.", link, handedOver,
                    ScreenshotPath: cutShot, Upload: UploadState.Unknown, CutOff: true),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new VideoStart(false, "Stopped.", FileHandedOver: handedOver, Stop: BatchOutcome.Stopped);
        }
        catch (NightModeException ex)
        {
            return new VideoStart(false, ex.Message, FileHandedOver: handedOver, ScreenshotPath: _tab.Closed ? null : await CaptureAsync("error"));
        }
    }

    private BatchResult? _stopSign;

    // The details screen's own progress line, e.g. "Uploading 45% ... 2 minutes left" then
    // "Upload complete ... Processing will begin shortly" (or "Checks complete", "Processing up to HD").
    private const string ProgressScript = """
        const d = __dialog(); if (!d) return '';
        const text = __ctext(d).replace(/\s+/g, ' ');
        const m = text.match(/(upload(ing)? complete|upload failed[^.]*|uploading \d{1,3}\s?%[^.]*|\d{1,3}\s?% uploaded|processing (will begin|up to|\d)[^.]*|checks? complete|processing abandoned[^.]*|upload (was )?cancell?ed)/i);
        return m ? m[0] : '';
        """;

    private static readonly Regex DoneLine = new(@"upload(ing)? complete|100\s?% uploaded|processing (will begin|up to|\d)|checks? complete", RegexOptions.IgnoreCase);
    private static readonly Regex FailLine = new(@"fail|abandon|cancell?ed|reject|error", RegexOptions.IgnoreCase);

    private async Task<(UploadState, string)> WaitForUploadAsync(string title, Stopwatch started, CancellationToken ct)
    {
        _stopSign = null;
        var lastCheck = TimeSpan.Zero;
        string? lastPercent = null;
        var lastMove = started.Elapsed;
        for (var first = true; first || started.Elapsed < UploadCutoff; first = false)
        {
            var line = (string?)await Js(ProgressScript, ct) ?? "";
            if (FailLine.IsMatch(line)) return (UploadState.Failed, line.Trim());
            if (DoneLine.IsMatch(line)) return (UploadState.Uploaded, "");

            // Stuck: the percentage hasn't moved for StuckAfter.
            var pm = Regex.Match(line, @"(\d{1,3})\s?%");
            var percent = pm.Success ? pm.Groups[1].Value : null;
            if (percent != lastPercent) { lastPercent = percent; lastMove = started.Elapsed; }
            else if (started.Elapsed - lastMove >= StuckAfter)
                return (UploadState.Unknown, percent is null ? "stuck" : $"stuck at {percent}%");

            Status?.Invoke($"{title}: {(line.Length > 0 ? line.Trim() : "uploading")} · {(int)started.Elapsed.TotalMinutes} min");
            if (started.Elapsed - lastCheck > TimeSpan.FromSeconds(20))
            {
                lastCheck = started.Elapsed;
                if (await CheckStopSignsAsync(ct) is { } sign) { _stopSign = sign; return (UploadState.Unknown, ""); }
            }
            await Task.Delay(2000, ct);
        }
        return (UploadState.Unknown, "");
    }

    /// <summary>
    /// Closes Studio's boxes that pop up after Save ("Video processing", "Video saved", "Still uploading"):
    /// watches for <paramref name="watch"/>, or checks once when zero. Never touches the upload window itself.
    /// </summary>
    private async Task CloseNoticesAsync(TimeSpan watch, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            var closed = await JsBool("""
                if (__dialog()) return false;
                const all = __all().filter(__vis);
                const b = all.find(e => e.id === 'close-button' || e.id === 'close-icon-button')
                    || all.find(e => ['BUTTON','YTCP-BUTTON','TP-YT-PAPER-BUTTON','YTCP-BUTTON-SHAPE'].includes(e.tagName) && /^(close|got it)$/i.test(__txt(e)));
                if (!b) return false;
                __clickable(b).click(); return true;
                """, ct);
            if (closed) { Status?.Invoke("Closed a Studio notice"); await Task.Delay(800, ct); continue; }
            if (watch == TimeSpan.Zero) return;
            await Task.Delay(1000, ct);
        } while (sw.Elapsed < watch);
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private async Task<VideoStart> Fail(string message, string shot, bool handedOver, CancellationToken ct) =>
        new(false, message + " Studio may have changed its page; a screenshot and the page's text were saved.", FileHandedOver: handedOver,
            ScreenshotPath: await CaptureAsync(shot, ct));

    private Task Pause(CancellationToken ct) => Task.Delay(ClickPause, ct);

    private async Task<bool> OpenUploadBoxAsync(CancellationToken ct)
    {
        if (await JsBool("return !!__fileInput() && !__titleBox();", ct)) return true;
        // The upload arrow (dashboard, content page) or Create → Upload videos (every page).
        var clicked = await JsBool("""
            const up = __all().find(e => __vis(e) && (e.id === 'upload-icon' || e.id === 'upload-button'));
            if (up) { up.click(); return true; }
            const c = __button(['create-icon'], ['create']);
            if (!c) return false;
            c.click(); return true;
            """, ct);
        if (!clicked) return false;
        await Pause(ct);
        if (!await JsBool("return !!__fileInput();", ct))
            await JsBool("const m = __button([], ['upload videos']); if (!m) return false; m.click(); return true;", ct);
        return await WaitFor("return !!__fileInput();", TimeSpan.FromSeconds(20), ct);
    }

    /// <summary>Replaces a text box's content by selecting it and typing, as a person would; checks the result.</summary>
    private async Task<bool> TypeIntoAsync(string finder, string text, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!await JsBool($"const e = {finder}; if (!e) return false; e.focus(); const r = document.createRange(); r.selectNodeContents(e); const s = getSelection(); s.removeAllRanges(); s.addRange(r); return true;", ct))
                return false;
            await Task.Delay(300, ct);
            await _tab.SendAsync("Input.insertText", new { text }, ct);
            await Task.Delay(600, ct);
            var now = (string?)await Js($"const e = {finder}; return e ? __txt(e) : null;", ct);
            if (string.Equals(now?.Trim(), text, StringComparison.Ordinal)) return true;
        }
        // Last resort: set the text directly and tell Studio it changed.
        await Js($"const e = {finder}; if (!e) return false; e.textContent = {JsonSerializer.Serialize(text)}; e.dispatchEvent(new InputEvent('input', {{ bubbles: true, composed: true }})); return true;", ct);
        await Task.Delay(600, ct);
        var final = (string?)await Js($"const e = {finder}; return e ? __txt(e) : null;", ct);
        return string.Equals(final?.Trim(), text, StringComparison.Ordinal);
    }

    private async Task<bool> ClickRadioAsync(string name, string wordsJson, CancellationToken ct)
    {
        if (!await WaitFor($"return !!__radio('{name}', {wordsJson});", TimeSpan.FromSeconds(15), ct)) return false;
        await Js($"const r = __radio('{name}', {wordsJson}); if (r) r.click(); return true;", ct);
        await Pause(ct);
        return await JsBool($"return __checked(__radio('{name}', {wordsJson}));", ct);
    }

    // ---------- Background uploads ----------

    private const string ReadRowsScript = """
        const titles = __TITLES__;
        const status = /upload|%|waiting|processing|fail|error|cancel|reject/i;
        const textNodes = [];
        for (const r of __roots()) { const tw = document.createTreeWalker(r, NodeFilter.SHOW_TEXT); let n; while ((n = tw.nextNode())) { const v = n.nodeValue.replace(/\s+/g, ' ').trim(); if (v) textNodes.push([n, v]); } }
        return titles.map(t => {
          const found = [];
          for (const [n, v] of textNodes) {
            if (v !== t && !(v.endsWith('…') && t.startsWith(v.slice(0, -1).trim()))) continue;
            let el = __up(n);
            for (let i = 0; i < 6 && el && el !== document; i++, el = __up(el)) {
              const txt = __ctext(el).replace(/\s+/g, ' ').trim();
              if (txt.replace(t, '').match(status)) { found.push(txt); break; }
            }
          }
          return found;
        });
        """;

    private static readonly Regex UploadedWords = new(@"100% uploaded|upload complete", RegexOptions.IgnoreCase);
    private static readonly Regex FailedWords = new(@"fail|error|reject|could ?n[o']t|too (long|large|big)|invalid|abandon|cancell?ed", RegexOptions.IgnoreCase);

    /// <summary>Reads Studio's upload box for each title: uploading, 100% uploaded, or failed (with Studio's words).</summary>
    public async Task<List<(UploadState State, string Note)>> ReadUploadsAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var result = new List<(UploadState, string)>();
        if (titles.Count == 0) return result;
        var rows = (await Js(ReadRowsScript.Replace("__TITLES__", JsonSerializer.Serialize(titles)), ct))?.AsArray();
        for (var i = 0; i < titles.Count; i++)
        {
            var texts = rows?[i]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? new List<string>();
            if (texts.Count == 0) { result.Add((UploadState.Unknown, "")); continue; }
            if (texts.Any(t => UploadedWords.IsMatch(t))) { result.Add((UploadState.Uploaded, "")); continue; }
            var failed = texts.FirstOrDefault(t => FailedWords.IsMatch(t.Replace(titles[i], "")));
            if (failed is not null) { result.Add((UploadState.Failed, Shorten(failed.Replace(titles[i], "").Trim()))); continue; }
            var pm = texts.Select(t => Regex.Match(t, @"(\d{1,3})\s?%")).FirstOrDefault(m => m.Success);
            result.Add((UploadState.Uploading, pm is null ? "" : pm.Groups[1].Value + "%"));
        }
        return result;
    }

    private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "…";

    private static string Snippet(string text, int index)
    {
        var start = Math.Max(0, index - 60);
        var space = text.IndexOf(' ', start);
        if (start > 0 && space >= 0 && space < index) start = space + 1;
        var len = Math.Min(text.Length - start, 160);
        return text.Substring(start, len).Trim();
    }
}

public sealed record NightProgress(string Status, int Uploaded, int Failed, int Uploading, int Waiting, DateTime? NextVideoAt = null);

/// <summary>
/// Uploads the waiting clips one at a time. Each video stays on Studio's details screen until its upload is complete,
/// then is saved (Private) and marked Uploaded. A file not finished after the cut-off is saved anyway, marked Failed
/// and tried again next run; <see cref="MaxCutoffsInARow"/> cut-offs in a row stop the night.
/// </summary>
public sealed class NightRunner
{
    public int MaxCutoffsInARow { get; init; } = 3;
    public int RefreshEvery { get; init; } = 20;
    public DateTime UploadDate { get; init; } = DateTime.Today;

    public event Action<NightProgress>? Progress;
    public event Action<string>? Activity;
    public int Uploaded { get; private set; }
    public int Failed { get; private set; }

    private volatile bool _finishing;

    /// <summary>Finish the video in progress, then stop.</summary>
    public void FinishAndStop() => _finishing = true;

    public async Task<BatchResult> RunAsync(StudioDriver driver, LogStore log, CancellationToken ct)
    {
        RecoverInterrupted(log);
        var queue = new Queue<LogRow>(BatchPlanner.Plan(log, BatchPlanner.MaxBatchSize, out _).SelectMany(b => b.Rows));
        if (queue.Count == 0) return new BatchResult(BatchOutcome.Done, "Nothing to upload: every renamed clip is already uploaded.");

        if (await driver.PreflightAsync(s => Activity?.Invoke(s), ct) is { } pre)
        {
            Activity?.Invoke(pre.Message);
            return pre;
        }

        var cutoffsInARow = 0;
        var sinceRefresh = 0;
        LogRow? current = null;
        driver.Status += s => Report(s, current is null ? 0 : 1, queue.Count);
        try
        {
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (_finishing) return new BatchResult(BatchOutcome.Stopped, "Stopped.");
                if (sinceRefresh >= RefreshEvery)
                {
                    Report("Refreshing YouTube Studio", 0, queue.Count);
                    if (await driver.GoHomeAsync(ct) is { } h) return h;
                    sinceRefresh = 0;
                }

                var row = current = queue.Dequeue();
                Activity?.Invoke($"Uploading {row.NewName}");
                Mark(log, row, LogStatus.Uploading, "Night mode is uploading this.");
                var v = await driver.StartVideoAsync(row.CurrentPath, UploadDate, ct);
                current = null;
                var link = v.Link is null ? "" : $" {v.Link}";

                if (v.Saved && v.Upload == UploadState.Uploaded)
                {
                    Mark(log, row, LogStatus.Uploaded, $"Uploaded as Private.{link}", v.Link);
                    Uploaded++;
                    cutoffsInARow = 0;
                    sinceRefresh++;
                    Activity?.Invoke($"Uploaded: {row.NewName}");
                }
                else if (v.Saved)
                {
                    Mark(log, row, LogStatus.Failed, (v.CutOff
                        ? $"{v.Message} Check it on YouTube: if it finished, delete the copy uploaded next run."
                        : $"{v.Message}. The video was saved, so a broken entry is left on YouTube.") + link, v.Link);
                    Failed++;
                    sinceRefresh++;
                    Activity?.Invoke($"Failed: {row.NewName}. {v.Message}");
                    if (v.CutOff && ++cutoffsInARow >= MaxCutoffsInARow)
                        return new BatchResult(BatchOutcome.TimedOut,
                            $"{MaxCutoffsInARow} videos in a row got stuck or didn't finish uploading in time, so night mode stopped for the night. Check the internet connection.",
                            v.ScreenshotPath);
                    if (!v.CutOff) cutoffsInARow = 0;
                }
                else if (v.Stop is BatchOutcome.StudioLimit or BatchOutcome.SignedOut or BatchOutcome.Stopped)
                {
                    Mark(log, row, LogStatus.Renamed, v.FileHandedOver
                        ? "Night mode stopped while this was being uploaded; an unfinished draft may be on YouTube. It will be uploaded next run."
                        : "");
                    Activity?.Invoke(v.Message);
                    return new BatchResult(v.Stop, v.Message, v.ScreenshotPath);
                }
                else
                {
                    Mark(log, row, LogStatus.Failed, v.FileHandedOver
                        ? $"Night mode: {v.Message} An unfinished draft may be left on YouTube."
                        : $"Night mode: {v.Message}");
                    Failed++;
                    Activity?.Invoke($"Failed: {row.NewName}. {v.Message}");
                    return new BatchResult(BatchOutcome.Error, v.Message, v.ScreenshotPath);
                }
                Report($"{row.NewName}: {(v.Upload == UploadState.Uploaded ? "done" : "failed")}", 0, queue.Count);
            }
        }
        catch (OperationCanceledException)
        {
            if (current is not null)
                Mark(log, current, LogStatus.Renamed, "Stopped while this was being uploaded; an unfinished draft may be on YouTube. It will be uploaded next run.");
            return new BatchResult(BatchOutcome.Stopped, "Stopped.");
        }

        Report("All done", 0, 0);
        return new BatchResult(BatchOutcome.Done, $"All done: {Uploaded} uploaded" + (Failed > 0 ? $", {Failed} failed (tried again next run)." : "."));
    }

    /// <summary>Clips left as "Uploading" by a run that ended unexpectedly are tried again; the log says why.</summary>
    private static void RecoverInterrupted(LogStore log)
    {
        var changed = false;
        foreach (var r in log.LatestByFingerprint().Values.Where(r => r.Status == LogStatus.Uploading))
        {
            r.Status = LogStatus.Failed;
            r.StatusTime = DateTime.Now;
            r.Note = $"{r.Note} Night mode stopped before this finished; an unfinished draft may be on YouTube. Uploading it again.".Trim();
            changed = true;
        }
        if (changed) log.TrySave();
    }

    private void Mark(LogStore log, LogRow row, string status, string note, string? link = null)
    {
        if (link is not null) row.YouTubeLink = link;
        row.Status = status;
        row.StatusTime = DateTime.Now;
        row.Note = note;
        if (!log.TrySave()) Activity?.Invoke("The log is open in Excel; it will be written when you close it.");
    }

    private int _waiting;
    private void Report(string status, int uploading, int waiting)
    {
        if (waiting >= 0) _waiting = waiting;
        if (status.Length > 0) Progress?.Invoke(new NightProgress(status, Uploaded, Failed, uploading, _waiting));
    }
}
