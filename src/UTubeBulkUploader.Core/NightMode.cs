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
public sealed record VideoStart(bool Saved, string Message, string? Link = null, bool FileHandedOver = false,
    BatchOutcome Stop = BatchOutcome.Error, string? ScreenshotPath = null);

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
            if (!await OpenUploadBoxAsync(ct))
                return await Fail("Couldn't open Studio's upload box (Create → Upload videos).", "upload box not found", handedOver, ct);

            // 2. The file
            Status?.Invoke($"Giving Studio {title}");
            var input = await _tab.SendAsync("Runtime.evaluate", new { expression = "(() => {" + Helpers + "return __fileInput();})()" }, ct);
            var objectId = (string?)input?["result"]?["objectId"];
            if (objectId is null) return await Fail("Studio's file picker disappeared.", "no file picker", handedOver, ct);
            await _tab.SendAsync("DOM.setFileInputFiles", new { files = new[] { path }, objectId }, ct);
            handedOver = true;

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

            // 7. Save, then close any "still uploading" / "video saved" notice
            Status?.Invoke($"{title}: saving");
            if (!await JsBool("const b = __button(['done-button'], ['save', 'publish']); if (!b) return false; b.click(); return true;", ct))
                return await Fail("Couldn't find the Save button.", "no save button", handedOver, ct);
            await Pause(ct);
            var closed = false;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < StepTimeout)
            {
                await Js("const b = __button(['close-button', 'close-icon-button'], ['close', 'got it', 'ok']); if (b && !__titleBox()) { b.click(); return true; } return false;", ct);
                if (!await JsBool("return !!__titleBox();", ct)) { closed = true; break; }
                await Task.Delay(700, ct);
            }
            if (!closed) return await Fail("Studio didn't confirm the save (the details screen stayed open).", "not saved", handedOver, ct);
            await Task.Delay(1500, ct);
            await Js("const b = __button(['close-button', 'close-icon-button'], ['close', 'got it']); if (b) b.click(); return true;", ct);

            return new VideoStart(true, "Saved as Private.", link, handedOver);
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
/// Uploads the waiting clips one by one: a new video at most every <see cref="PerVideo"/>, never more than
/// <see cref="MaxUploading"/> still uploading, and a page refresh every <see cref="RefreshEvery"/> videos once
/// nothing is uploading. Each clip is logged on its own: Uploading (saved, file still going up), Uploaded, or Failed.
/// </summary>
public sealed class NightRunner
{
    public TimeSpan PerVideo { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxUploading { get; init; } = 3;
    public int RefreshEvery { get; init; } = 20;
    /// <summary>A file still uploading after this long is given up on.</summary>
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>A saved video Studio's box doesn't show for this long, with no upload traffic, stops the run.</summary>
    public TimeSpan NotSeenAfter { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public DateTime UploadDate { get; init; } = DateTime.Today;

    public event Action<NightProgress>? Progress;
    public event Action<string>? Activity;
    public int Uploaded { get; private set; }
    public int Failed { get; private set; }

    private volatile bool _finishing;

    /// <summary>Stop starting new videos; finish the ones uploading, then stop.</summary>
    public void FinishAndStop() => _finishing = true;

    private sealed class InFlight
    {
        public required LogRow Row { get; init; }
        public required string Title { get; init; }
        public string? Link { get; init; }
        public DateTime SavedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

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

        var inFlight = new List<InFlight>();
        var lastStart = DateTime.MinValue;
        var sinceRefresh = 0;
        var refreshPending = false;
        BatchResult? stop = null;
        driver.Status += s => Report(s, inFlight.Count, queue.Count);

        try
        {
            while (queue.Count > 0 || inFlight.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (_finishing && stop is null) stop = new BatchResult(BatchOutcome.Stopped, "Stopped after finishing the uploads in progress.");

                // 1. Background uploads: mark the finished ones.
                if (inFlight.Count > 0) await CheckUploadsAsync(driver, log, inFlight, ct, s => stop ??= s);

                // 2. Signed out or Studio's limit: stop starting new videos.
                if (stop is null && await driver.CheckStopSignsAsync(ct) is { } sign) stop = sign;

                // 3. Refresh every 20 videos, only when nothing is uploading.
                if (refreshPending && inFlight.Count == 0 && stop is null)
                {
                    Report("Refreshing YouTube Studio", 0, queue.Count);
                    if (await driver.GoHomeAsync(ct) is { } h) stop = h;
                    refreshPending = false;
                    sinceRefresh = 0;
                }

                // 4. Start the next video when it's time and there's room.
                var due = lastStart + PerVideo;
                if (stop is null && !refreshPending && queue.Count > 0 && inFlight.Count < MaxUploading && DateTime.UtcNow >= due)
                {
                    var row = queue.Dequeue();
                    lastStart = DateTime.UtcNow;
                    var title = VideoDetails.Title(row.CurrentPath);
                    Activity?.Invoke($"Uploading {row.NewName}");
                    var started = await driver.StartVideoAsync(row.CurrentPath, UploadDate, ct);
                    if (started.Saved)
                    {
                        Mark(log, row, LogStatus.Uploading, $"Saved on YouTube as Private{(started.Link is null ? "" : $": {started.Link}")}. Uploading.");
                        inFlight.Add(new InFlight { Row = row, Title = title, Link = started.Link });
                        sinceRefresh++;
                        if (sinceRefresh >= RefreshEvery) refreshPending = true;
                    }
                    else if (started.Stop is BatchOutcome.StudioLimit or BatchOutcome.SignedOut or BatchOutcome.Stopped)
                    {
                        stop = new BatchResult(started.Stop, started.Message, started.ScreenshotPath);   // clip stays waiting for next run
                    }
                    else
                    {
                        Mark(log, row, LogStatus.Failed, started.FileHandedOver
                            ? $"Night mode: {started.Message} The file was given to Studio, so an unfinished draft may be left on YouTube."
                            : $"Night mode: {started.Message}");
                        Failed++;
                        Activity?.Invoke($"Failed: {row.NewName}. {started.Message}");
                        stop = new BatchResult(started.Stop == BatchOutcome.Stopped ? BatchOutcome.Stopped : BatchOutcome.Error, started.Message, started.ScreenshotPath);
                    }
                    Report(started.Saved ? $"{row.NewName} saved, uploading in the background" : started.Message, inFlight.Count, queue.Count);
                    continue;
                }

                if (stop is not null && inFlight.Count == 0) break;
                if (stop is not null && queue.Count > 0) Report("Finishing the uploads in progress before stopping", inFlight.Count, queue.Count);
                else if (queue.Count == 0) Report("Waiting for the last uploads to finish", inFlight.Count, 0);
                else if (inFlight.Count >= MaxUploading) Report($"{MaxUploading} videos uploading; waiting before the next", inFlight.Count, queue.Count);
                else if (refreshPending) Report("Waiting for uploads to finish before refreshing Studio", inFlight.Count, queue.Count);
                else Report("Next video soon", inFlight.Count, queue.Count, due.ToLocalTime());
                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in inFlight)
                Mark(log, f.Row, LogStatus.Uploading, f.Row.Note);   // left as Uploading: checked next run
            return new BatchResult(BatchOutcome.Stopped, inFlight.Count == 0 ? "Stopped." :
                $"Stopped. {inFlight.Count} video(s) were still uploading; if Chrome stays open they may finish. The app checks them next run.");
        }

        if (stop is not null) return stop;
        Report("All done", 0, 0);
        return new BatchResult(BatchOutcome.Done, $"All done: {Uploaded} uploaded" + (Failed > 0 ? $", {Failed} failed (tried again next run)." : "."));
    }

    private async Task CheckUploadsAsync(StudioDriver driver, LogStore log, List<InFlight> inFlight, CancellationToken ct, Action<BatchResult> stop)
    {
        var states = await driver.ReadUploadsAsync(inFlight.Select(f => f.Title).ToList(), ct);
        for (var i = inFlight.Count - 1; i >= 0; i--)
        {
            var f = inFlight[i];
            var (state, note) = states[i];
            var link = f.Link is null ? "" : $" {f.Link}";
            if (state == UploadState.Uploaded)
            {
                Mark(log, f.Row, LogStatus.Uploaded, $"Uploaded as Private.{link}");
                Uploaded++;
                inFlight.RemoveAt(i);
                Activity?.Invoke($"Uploaded: {f.Row.NewName}");
                continue;
            }
            if (state == UploadState.Failed)
            {
                Mark(log, f.Row, LogStatus.Failed, $"Studio: {note}. The video was already saved, so a broken entry is left on YouTube.{link}");
                Failed++;
                inFlight.RemoveAt(i);
                Activity?.Invoke($"Failed: {f.Row.NewName}. {note}");
                continue;
            }
            if (state != UploadState.Unknown) f.LastSeen = DateTime.UtcNow;
            if (DateTime.UtcNow - f.SavedAt > UploadTimeout)
            {
                Mark(log, f.Row, LogStatus.Failed, $"Still not uploaded after {UploadTimeout.TotalMinutes:0} minutes; a broken entry may be left on YouTube.{link}");
                Failed++;
                inFlight.RemoveAt(i);
                Activity?.Invoke($"Failed: {f.Row.NewName} took over {UploadTimeout.TotalMinutes:0} minutes.");
                stop(new BatchResult(BatchOutcome.TimedOut, $"{f.Row.NewName} took over {UploadTimeout.TotalMinutes:0} minutes to upload, so night mode stopped to be safe.",
                    await driver.CaptureAsync("too slow", ct)));
                continue;
            }
            if (state == UploadState.Unknown && DateTime.UtcNow - f.LastSeen > NotSeenAfter && !driver.UploadTrafficActive)
            {
                // Not in Studio's box and nothing uploading: rather than guess, record it and stop.
                Mark(log, f.Row, LogStatus.Failed, $"Saved, but Studio's upload box never showed it finishing. Check it on YouTube.{link}");
                Failed++;
                inFlight.RemoveAt(i);
                stop(new BatchResult(BatchOutcome.Error,
                    $"Studio's upload box doesn't show {f.Row.NewName}, and nothing is uploading. Night mode stopped so nothing is marked wrongly; a screenshot and the page's text were saved.",
                    await driver.CaptureAsync("upload not shown", ct)));
            }
        }
        Report(inFlight.Count == 0 ? "" : $"{inFlight.Count} uploading: " +
            string.Join(", ", inFlight.Select((f, k) => $"{f.Row.NewName} {(states.Count > k ? states[k].Note : "")}".Trim())), inFlight.Count, -1);
    }

    /// <summary>Clips left as "Uploading" by a run that stopped unexpectedly are tried again; the log says why.</summary>
    private static void RecoverInterrupted(LogStore log)
    {
        var changed = false;
        foreach (var r in log.LatestByFingerprint().Values.Where(r => r.Status == LogStatus.Uploading))
        {
            r.Status = LogStatus.Failed;
            r.StatusTime = DateTime.Now;
            r.Note = $"{r.Note} Night mode stopped before this finished uploading; a broken entry may be left on YouTube. Uploading it again.".Trim();
            changed = true;
        }
        if (changed) log.TrySave();
    }

    private void Mark(LogStore log, LogRow row, string status, string note)
    {
        row.Status = status;
        row.StatusTime = DateTime.Now;
        row.Note = note;
        if (!log.TrySave()) Activity?.Invoke("The log is open in Excel; it will be written when you close it.");
    }

    private int _waiting;
    private void Report(string status, int uploading, int waiting, DateTime? next = null)
    {
        if (waiting >= 0) _waiting = waiting;
        if (status.Length > 0 || next is not null)
            Progress?.Invoke(new NightProgress(status, Uploaded, Failed, uploading, _waiting, next));
    }
}
