using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UTubeBulkUploader.Core;

public enum BatchOutcome { Done, StudioLimit, SignedOut, TimedOut, Stopped, Error }

/// <summary>What Studio's upload box said about one file of a batch.</summary>
public sealed record FileOutcome(string Path, bool Uploaded, string Note);

/// <param name="Files">Files Studio confirmed (uploaded or failed). Present even when the batch stopped early,
/// so files that did finish are still recorded.</param>
public sealed record BatchResult(BatchOutcome Outcome, string Message, string? ScreenshotPath = null, IReadOnlyList<FileOutcome>? Files = null);

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

/// <summary>
/// Drives one Studio tab: opens the upload box from Studio's home page, hands it a batch of files (Studio's own
/// "Select files" route, the same as dropping them), and reads Studio's upload box until it says
/// "Uploads complete" with a "100% uploaded" tick for every file. Processing is not waited for.
/// Anything unexpected stops the run with a message, a screenshot and the box's text rather than guessing.
/// </summary>
public sealed class StudioDriver
{
    private readonly CdpTab _tab;
    private readonly StudioTarget _target;
    private readonly HashSet<string> _uploadRequests = new();
    private DateTime _lastNetworkActivity = DateTime.MinValue;
    private int _serverConfirmed;
    private readonly object _lock = new();

    /// <summary>A batch that hasn't finished after this long stops the run.</summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>Studio must start showing the files within this time.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Uploads look finished but the box doesn't say so: stop after this long and save what it shows.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
    public string ScreenshotDir { get; init; } = Path.Combine(AppPaths.StateDir, "night-screens");

    /// <summary>Live status for the screen.</summary>
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
                // YouTube's upload server answers "final" once it has received the whole file.
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
    private int ServerConfirmed { get { lock (_lock) return _serverConfirmed; } }

    // A small script added before each call: finds elements and text inside Studio's nested components too.
    private const string Helpers = """
        const __roots = () => { const out = []; const walk = r => { out.push(r); for (const e of r.querySelectorAll('*')) if (e.shadowRoot) walk(e.shadowRoot); }; walk(document); return out; };
        const __all = () => { const out = []; for (const r of __roots()) for (const e of r.querySelectorAll('*')) out.push(e); return out; };
        const __visibleText = () => { const parts = []; for (const r of __roots()) { const tw = document.createTreeWalker(r, NodeFilter.SHOW_TEXT); let n; while ((n = tw.nextNode())) { const el = n.parentElement; if (el && el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden') parts.push(n.nodeValue); } } return parts.join(' ').replace(/\s+/g, ' '); };
        const __fileInput = () => __all().find(e => e.tagName === 'INPUT' && e.type === 'file');
        const __up = n => n.parentNode instanceof ShadowRoot ? n.parentNode.host : n.parentNode;
        const __ctext = n => { if (n.nodeType === 3) return n.nodeValue; if (n.nodeName === 'STYLE' || n.nodeName === 'SCRIPT') return ''; let s = ''; if (n.shadowRoot) for (const k of n.shadowRoot.childNodes) s += __ctext(k) + ' '; for (const k of n.childNodes) s += __ctext(k) + ' '; return s; };
        """;

    // Reads Studio's upload box: whether it says "Uploads complete", and each batch file's row text.
    private const string ReadBoxScript = """
        const titles = __TITLES__;
        const status = /upload|%|waiting|processing|fail|error|cancel|reject/i;
        const textNodes = [];
        for (const r of __roots()) { const tw = document.createTreeWalker(r, NodeFilter.SHOW_TEXT); let n; while ((n = tw.nextNode())) { const v = n.nodeValue.replace(/\s+/g, ' ').trim(); if (v) textNodes.push([n, v]); } }
        const complete = textNodes.some(([n, v]) => /^uploads? complete$/i.test(v) && n.parentElement && n.parentElement.getClientRects().length);
        const ticks = textNodes.filter(([n, v]) => /100% uploaded/i.test(v)).length;
        const rows = titles.map(t => {
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
        return { complete, ticks, rows, text: __visibleText().slice(0, 4000) };
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

    public async Task<string?> CaptureAsync(string name, CancellationToken ct = default, string? text = null)
    {
        try
        {
            Directory.CreateDirectory(ScreenshotDir);
            var stem = Path.Combine(ScreenshotDir, $"{DateTime.Now:yyyy-MM-dd HHmmss} {name}");
            if (text is not null) await File.WriteAllTextAsync(stem + ".txt", text, ct);
            var r = await _tab.SendAsync("Page.captureScreenshot", new { format = "png" }, ct);
            if ((string?)r?["data"] is not { } data) return null;
            await File.WriteAllBytesAsync(stem + ".png", Convert.FromBase64String(data), ct);
            return stem + ".png";
        }
        catch (Exception) { return null; }
    }

    // ---------- Pre-flight ----------

    /// <summary>
    /// Short check before a run: Studio opens signed in, and it is in English (the app reads Studio's English words).
    /// Returns null when all is well, else the reason to stop.
    /// </summary>
    public async Task<BatchResult?> PreflightAsync(Action<string> report, CancellationToken ct)
    {
        Status?.Invoke("Checking YouTube Studio");
        await NavigateAsync(_target.HomeUrl, ct);
        var sw = Stopwatch.StartNew();
        var signedIn = _target.Practice;
        while (!signedIn && sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            var url = await UrlAsync(ct);
            if (IsSignInPage(url))
                return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Click Sign in on this screen, sign in to the channel, close that Chrome window, then start again.");
            if (url.Contains("studio.youtube.com/channel/", StringComparison.OrdinalIgnoreCase)) signedIn = true;
            else await Task.Delay(1000, ct);
        }
        if (!signedIn)
            return new BatchResult(BatchOutcome.Error, "YouTube Studio didn't open. Check the internet connection, then start again.", await CaptureAsync("studio did not open", ct));
        report("✓ Signed in to YouTube Studio");

        var lang = (string?)await Js("return (document.documentElement.lang || '').toLowerCase();", ct) ?? "";
        var english = lang.StartsWith("en") || (lang.Length == 0 && Regex.IsMatch((string?)await Js("return __visibleText();", ct) ?? "", @"\b(Dashboard|Content|Upload)\b"));
        if (!english)
            return new BatchResult(BatchOutcome.Error,
                $"YouTube Studio isn't in English (it's set to \"{lang}\"). Night mode reads Studio's English words, so it can't tell when uploads finish. " +
                "In Studio click your channel picture > Language > English, then start again.", await CaptureAsync("not english", ct));
        report("✓ Studio is in English");
        return null;
    }

    // ---------- One batch ----------

    /// <summary>Opens Studio's home page and its upload box. Returns null when ready, else a stop result.</summary>
    private async Task<BatchResult?> OpenUploadBoxAsync(CancellationToken ct)
    {
        // Always start from Studio's home page: it has the upload button, and reloading clears the previous
        // batch's ticks from the upload box.
        Status?.Invoke("Opening YouTube Studio");
        await NavigateAsync(_target.HomeUrl, ct);
        string? uploadUrl = null;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            var url = await UrlAsync(ct);
            if (IsSignInPage(url)) return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Click Sign in on this screen, then start again.");
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

        // Wait for the box's file picker; if the direct link didn't open it, go home and press Studio's upload button.
        sw.Restart();
        var clicked = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(45))
        {
            if (IsSignInPage(await UrlAsync(ct))) return new BatchResult(BatchOutcome.SignedOut, "Chrome isn't signed in to YouTube. Click Sign in on this screen, then start again.");
            if ((bool?)await Js("return !!__fileInput();", ct) == true) return null;
            if (!clicked && sw.Elapsed > TimeSpan.FromSeconds(8))
            {
                if (!_target.Practice) await NavigateAsync(_target.HomeUrl, ct);
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

    /// <summary>Uploads one batch and waits until Studio's upload box shows it complete.</summary>
    public async Task<BatchResult> UploadBatchAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        var confirmed = new Dictionary<string, FileOutcome>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in files)
                if (!File.Exists(f)) return new BatchResult(BatchOutcome.Error, $"A file in this batch is missing: {f}");

            if (await OpenUploadBoxAsync(ct) is { } stop) return stop;

            var input = await _tab.SendAsync("Runtime.evaluate", new { expression = "(() => {" + Helpers + "return __fileInput();})()" }, ct);
            var objectId = (string?)input?["result"]?["objectId"];
            if (objectId is null) return new BatchResult(BatchOutcome.Error, "Studio's file picker disappeared.", await CaptureAsync("no file picker", ct));
            lock (_lock) { _uploadRequests.Clear(); _lastNetworkActivity = DateTime.MinValue; _serverConfirmed = 0; }
            Status?.Invoke($"Handing {files.Count} files to Studio");
            await _tab.SendAsync("DOM.setFileInputFiles", new { files, objectId }, ct);
            await Js("const i = __fileInput(); if (i) { i.dispatchEvent(new Event('change', { bubbles: true })); } return true;", ct).ContinueWith(_ => { }, ct);

            return await WatchAsync(files, confirmed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BatchResult(BatchOutcome.Stopped, "Stopped.", Files: confirmed.Values.ToList());
        }
        catch (NightModeException ex)
        {
            return new BatchResult(BatchOutcome.Error, ex.Message, _tab.Closed ? null : await CaptureAsync("error"), confirmed.Values.ToList());
        }
    }

    private static readonly Regex Uploaded = new(@"100% uploaded|upload complete", RegexOptions.IgnoreCase);
    private static readonly Regex Failed = new(@"fail|error|reject|could ?n[o']t|too (long|large|big)|invalid|abandon|cancell?ed", RegexOptions.IgnoreCase);
    private static readonly Regex Percent = new(@"(\d{1,3})\s?%", RegexOptions.IgnoreCase);

    private async Task<BatchResult> WatchAsync(IReadOnlyList<string> files, Dictionary<string, FileOutcome> confirmed, CancellationToken ct)
    {
        var titles = files.Select(Path.GetFileNameWithoutExtension).Select(t => t!).ToList();
        var script = ReadBoxScript.Replace("__TITLES__", JsonSerializer.Serialize(titles));
        var sw = Stopwatch.StartNew();
        var started = false;
        DateTime? stuckSince = null;
        string lastText = "";
        while (true)
        {
            await Task.Delay(PollInterval, ct);
            var url = await UrlAsync(ct);
            if (IsSignInPage(url))
                return new BatchResult(BatchOutcome.SignedOut, "YouTube signed Chrome out during the batch. Click Sign in on this screen, then start again.",
                    await CaptureAsync("signed out", ct), confirmed.Values.ToList());

            var box = await Js(script, ct);
            var text = (string?)box?["text"] ?? "";
            lastText = text;
            if (Limit.Match(text) is { Success: true } lm)
                return new BatchResult(BatchOutcome.StudioLimit, $"Studio says stop: \"{Snippet(text, lm.Index)}\"",
                    await CaptureAsync("studio limit", ct, text), confirmed.Values.ToList());

            // Read each file's row.
            var complete = (bool?)box?["complete"] == true;
            var ticks = (int?)box?["ticks"] ?? 0;
            var rows = box?["rows"]?.AsArray();
            int seen = 0, percentSum = 0;
            for (var i = 0; i < files.Count; i++)
            {
                var texts = rows?[i]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? new List<string>();
                if (texts.Count == 0) continue;
                seen++;
                if (texts.Any(t => Uploaded.IsMatch(t)))
                {
                    confirmed[files[i]] = new FileOutcome(files[i], true, "");
                    percentSum += 100;
                    continue;
                }
                var failed = texts.FirstOrDefault(t => Failed.IsMatch(t.Replace(titles[i], "")));
                if (failed is not null)
                {
                    confirmed[files[i]] = new FileOutcome(files[i], false, "Studio: " + Shorten(failed.Replace(titles[i], "").Trim()));
                    continue;
                }
                var pm = texts.Select(t => Percent.Match(t)).FirstOrDefault(x => x.Success);
                percentSum += pm is null ? 0 : Math.Min(100, int.Parse(pm.Groups[1].Value));
            }
            if (seen > 0 || ActiveUploads > 0) started = true;

            var done = confirmed.Values.Count(c => c.Uploaded);
            var mins = (int)sw.Elapsed.TotalMinutes;
            Status?.Invoke(!started ? "Waiting for Studio to start uploading…"
                : $"Studio: {done} of {files.Count} uploaded · about {percentSum / Math.Max(1, files.Count)}% · {mins} min so far");

            // Finished: the box says "Uploads complete" and every file has its tick (or an error on its row).
            if (complete && confirmed.Count >= files.Count)
                return Finish(files, confirmed, sw);
            // Titles Studio shortened beyond recognition: fall back to counting ticks.
            if (complete && ticks >= files.Count && confirmed.Values.All(c => c.Uploaded))
            {
                foreach (var f in files) confirmed.TryAdd(f, new FileOutcome(f, true, ""));
                return Finish(files, confirmed, sw);
            }

            // Looks finished from the outside (server confirmed every file, or no upload traffic for a while)
            // but the box doesn't say so: stop and save what it shows, rather than guess.
            var quiet = ActiveUploads == 0 && LastActivity != DateTime.MinValue && DateTime.UtcNow - LastActivity > StuckAfter;
            var serverDone = ServerConfirmed >= files.Count;
            var practiceIdle = _target.Practice && started && confirmed.Count >= files.Count;   // test page has no network
            if (started && (serverDone || quiet || practiceIdle))
            {
                stuckSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stuckSince.Value >= StuckAfter)
                    return new BatchResult(BatchOutcome.Error,
                        $"The uploads look finished ({ServerConfirmed} of {files.Count} confirmed by YouTube's upload server, {done} ticked in Studio), " +
                        "but Studio's upload box doesn't say \"Uploads complete\". Night mode stopped so nothing is marked wrongly. " +
                        "A screenshot and the box's text were saved for checking.",
                        await CaptureAsync("not recognised as finished", ct, Describe(titles, rows, text)), confirmed.Values.ToList());
            }
            else stuckSince = null;

            if (!started && sw.Elapsed > StartTimeout)
                return new BatchResult(BatchOutcome.Error, "Studio didn't start uploading the files.", await CaptureAsync("not started", ct, text));
            if (sw.Elapsed > BatchTimeout)
                return new BatchResult(BatchOutcome.TimedOut,
                    $"This batch took more than {BatchTimeout.TotalMinutes:0} minutes ({done} of {files.Count} finished), so night mode stopped to be safe.",
                    await CaptureAsync("too slow", ct, Describe(titles, rows, lastText)), confirmed.Values.ToList());
        }
    }

    private BatchResult Finish(IReadOnlyList<string> files, Dictionary<string, FileOutcome> confirmed, Stopwatch sw)
    {
        var ok = confirmed.Values.Count(c => c.Uploaded);
        var failed = files.Count - ok;
        var mins = Math.Max(1, (int)Math.Round(sw.Elapsed.TotalMinutes));
        return new BatchResult(BatchOutcome.Done,
            failed == 0 ? $"Studio finished all {files.Count} files in {mins} min." : $"Studio finished {ok} of {files.Count} files in {mins} min; {failed} failed.",
            Files: files.Select(f => confirmed.TryGetValue(f, out var c) ? c : new FileOutcome(f, false, "Not shown in Studio's upload box.")).ToList());
    }

    private static string Describe(List<string> titles, JsonArray? rows, string text)
    {
        var lines = new List<string> { "What night mode read from YouTube Studio", "" };
        for (var i = 0; i < titles.Count; i++)
        {
            var texts = rows?[i]?.AsArray().Select(x => (string?)x ?? "").ToList() ?? new List<string>();
            lines.Add($"{titles[i]}: {(texts.Count == 0 ? "(not found in the upload box)" : string.Join(" | ", texts))}");
        }
        lines.Add("");
        lines.Add("Visible page text:");
        lines.Add(text);
        return string.Join(Environment.NewLine, lines);
    }

    private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "…";

    private static string Snippet(string text, int index)
    {
        var start = Math.Max(0, index - 60);
        var space = text.IndexOf(' ', start);
        if (start > 0 && space >= 0 && space < index) start = space + 1;   // begin at a whole word
        var len = Math.Min(text.Length - start, 160);
        return text.Substring(start, len).Trim();
    }
}

/// <param name="Uploaded">Files uploaded in this run.</param>
public sealed record NightProgress(string Status, int BatchNumber, int BatchCount, int Uploaded, int Failed, int ClipsTotal, DateTime? NextBatchAt = null);

/// <summary>Runs batches one after another with a pause in between, until done, stopped, or Studio says stop.</summary>
public sealed class NightRunner
{
    public TimeSpan Gap { get; init; } = TimeSpan.FromMinutes(2);
    public event Action<NightProgress>? Progress;
    public event Action<string>? Activity;
    public int Uploaded { get; private set; }
    public int Failed { get; private set; }

    public async Task<BatchResult> RunAsync(StudioDriver driver, LogStore log, int batchSize, CancellationToken ct)
    {
        var batches = BatchPlanner.Plan(log, batchSize, out _);
        var total = batches.Sum(b => b.Rows.Count);
        if (batches.Count == 0) return new BatchResult(BatchOutcome.Done, "Nothing to upload: every renamed clip is already uploaded.");
        driver.Status += s => Progress?.Invoke(new NightProgress(s, CurrentBatch, batches.Count, Uploaded, Failed, total));

        if (await driver.PreflightAsync(s => Activity?.Invoke(s), ct) is { } stop)
        {
            Activity?.Invoke(stop.Message);
            return stop;
        }

        for (var i = 0; i < batches.Count; i++)
        {
            CurrentBatch = i + 1;
            var b = batches[i];
            Activity?.Invoke($"Batch {b.Number} of {batches.Count}: {b.Rows.Count} clips ({b.FirstName} … {b.LastName})");
            var r = await driver.UploadBatchAsync(b.Paths.ToList(), ct);
            Record(log, b, r);
            if (r.Outcome != BatchOutcome.Done)
            {
                Activity?.Invoke(r.Message);
                return r;
            }
            Activity?.Invoke(r.Message);
            if (i < batches.Count - 1)
            {
                var next = DateTime.Now + Gap;
                Progress?.Invoke(new NightProgress("Pausing before the next batch", CurrentBatch, batches.Count, Uploaded, Failed, total, next));
                try { await Task.Delay(Gap, ct); }
                catch (OperationCanceledException) { return new BatchResult(BatchOutcome.Stopped, "Stopped."); }
            }
        }
        Progress?.Invoke(new NightProgress("All batches finished", batches.Count, batches.Count, Uploaded, Failed, total));
        return new BatchResult(BatchOutcome.Done,
            $"All {batches.Count} batch{(batches.Count == 1 ? "" : "es")} finished: {Uploaded} uploaded" + (Failed > 0 ? $", {Failed} failed (they'll be tried again next run)." : "."));
    }

    /// <summary>Writes each file Studio confirmed to the log on its own: Uploaded, or Failed with Studio's words.</summary>
    private void Record(LogStore log, Batch b, BatchResult r)
    {
        if (r.Files is not { Count: > 0 } files) return;
        var byPath = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<(LogRow, FileOutcome)>();
        foreach (var row in b.Rows)
            if (byPath.TryGetValue(row.CurrentPath, out var o)) outcomes.Add((row, o));
        var saved = BatchPlanner.MarkFiles(log, outcomes, $"Night mode, batch {b.Number}.");
        var up = outcomes.Count(o => o.Item2.Uploaded);
        Uploaded += up;
        Failed += outcomes.Count - up;
        foreach (var (row, o) in outcomes.Where(o => !o.Item2.Uploaded))
            Activity?.Invoke($"Failed: {row.NewName}. {o.Note}");
        if (!saved) Activity?.Invoke("The log is open in Excel; it will be written when you close it.");
    }

    private int CurrentBatch { get; set; }
}
