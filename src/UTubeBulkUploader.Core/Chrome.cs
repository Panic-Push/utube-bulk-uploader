using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UTubeBulkUploader.Core;

/// <summary>
/// Starts Chrome with the app's own profile (so the user signs in to the channel by hand, once) and opens a
/// control connection to it. The user's everyday Chrome profile is never touched.
/// </summary>
public static class ChromeLauncher
{
    public static string ProfileDir => Path.Combine(AppPaths.StateDir, "chrome-profile");

    public static string? FindChrome(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var candidates = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
            if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
        candidates.Add("/usr/bin/google-chrome");
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The control port of an app-profile Chrome that is already open, if any.</summary>
    public static int? RunningPort(string profileDir)
    {
        try
        {
            var file = Path.Combine(profileDir, "DevToolsActivePort");
            if (!File.Exists(file)) return null;
            var first = File.ReadLines(file).FirstOrDefault();
            if (!int.TryParse(first, out var port)) return null;
            using var tcp = new TcpClient();
            var t = tcp.ConnectAsync(IPAddress.Loopback, port);
            return t.Wait(1000) && tcp.Connected ? port : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True while any Chrome window is using this profile (with or without the control port).</summary>
    public static bool IsProfileInUse(string profileDir)
    {
        if (OperatingSystem.IsWindows())
        {
            var lockFile = Path.Combine(profileDir, "lockfile");
            if (!File.Exists(lockFile)) return false;
            try
            {
                using var _ = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }
        return File.Exists(Path.Combine(profileDir, "SingletonLock")) || new FileInfo(Path.Combine(profileDir, "SingletonLock")).LinkTarget is not null;
    }

    /// <summary>
    /// Opens the app's Chrome profile as a completely ordinary Chrome window, for signing in to Google.
    /// Google refuses sign-in ("This browser or app may not be secure") in a Chrome that has its control port open,
    /// so if night mode's Chrome is running it is closed first. The sign-in is kept in the profile afterwards.
    /// </summary>
    public static async Task OpenForSignInAsync(string chromePath, string profileDir, string url, CancellationToken ct)
    {
        if (RunningPort(profileDir) is int port)
        {
            await CloseAsync(port, ct);
            var sw = Stopwatch.StartNew();
            while (IsProfileInUse(profileDir) && sw.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(300, ct);
            if (IsProfileInUse(profileDir))
                throw new NightModeException("The app's Chrome window didn't close. Close it yourself, then click Sign in again.");
        }
        Directory.CreateDirectory(profileDir);
        try { File.Delete(Path.Combine(profileDir, "DevToolsActivePort")); } catch (IOException) { }
        var psi = new ProcessStartInfo(chromePath) { UseShellExecute = false };
        foreach (var a in new[] { $"--user-data-dir={profileDir}", "--no-first-run", "--no-default-browser-check", url })
            psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>Closes the app's Chrome through its control port.</summary>
    public static async Task CloseAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var info = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct))!;
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri((string)info["webSocketDebuggerUrl"]!), ct);
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}"), WebSocketMessageType.Text, true, ct);
            await Task.Delay(500, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* already closing or gone */ }
    }

    /// <summary>Returns the control port of the app's Chrome, starting it if needed.</summary>
    public static async Task<int> EnsureRunningAsync(string chromePath, string profileDir, string startUrl, CancellationToken ct)
    {
        if (RunningPort(profileDir) is int p) return p;
        if (IsProfileInUse(profileDir))
            throw new NightModeException("The Chrome window opened for signing in is still open. Once you're signed in to the channel, " +
                                         "close that Chrome window, then click Start again.");
        Directory.CreateDirectory(profileDir);
        try { File.Delete(Path.Combine(profileDir, "DevToolsActivePort")); } catch (IOException) { }
        var psi = new ProcessStartInfo(chromePath) { UseShellExecute = false };
        foreach (var a in new[]
                 {
                     "--remote-debugging-port=0", $"--user-data-dir={profileDir}", "--no-first-run", "--no-default-browser-check",
                     "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
                     startUrl,
                 })
            psi.ArgumentList.Add(a);
        foreach (var extra in (Environment.GetEnvironmentVariable("UBU_CHROME_ARGS") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Insert(0, extra);   // used by tests (e.g. --headless=new)
        Process.Start(psi);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            ct.ThrowIfCancellationRequested();
            if (RunningPort(profileDir) is int port) return port;
            await Task.Delay(300, ct);
        }
        throw new NightModeException("Chrome didn't start. If a Chrome window from the app is stuck, close it and try again.");
    }
}

/// <summary>A minimal Chrome DevTools Protocol connection to one tab.</summary>
public sealed class CdpTab : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId;
    private Task? _reader;

    public event Action<string, JsonNode?>? Event;
    public bool Closed { get; private set; }

    public static async Task<CdpTab> ConnectAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct))!.AsArray();
        var page = list.FirstOrDefault(t => (string?)t?["type"] == "page" && ((string?)t?["url"] ?? "").Contains("studio.youtube.com"))
                   ?? list.FirstOrDefault(t => (string?)t?["type"] == "page");
        string wsUrl;
        if (page is null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?about:blank");
            var created = JsonNode.Parse(await (await http.SendAsync(req, ct)).Content.ReadAsStringAsync(ct))!;
            wsUrl = (string)created["webSocketDebuggerUrl"]!;
        }
        else wsUrl = (string)page["webSocketDebuggerUrl"]!;
        var tab = new CdpTab();
        tab._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await tab._ws.ConnectAsync(new Uri(wsUrl), ct);
        tab._reader = Task.Run(tab.ReadLoop);
        return tab;
    }

    public async Task<JsonNode?> SendAsync(string method, object? parameters = null, CancellationToken ct = default, int timeoutSeconds = 30)
    {
        if (Closed) throw new NightModeException("The connection to Chrome was lost. Was the Chrome window closed?");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
        try { return await tcs.Task; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new NightModeException($"Chrome didn't answer ({method}).");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Runs JavaScript in the page and returns its (JSON-serialisable) result.</summary>
    public async Task<JsonNode?> EvalAsync(string expression, CancellationToken ct = default)
    {
        var r = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);
        if (r?["exceptionDetails"] is { } ex)
            throw new NightModeException("Page script error: " + (ex["exception"]?["description"] ?? ex["text"]));
        return r?["result"]?["value"];
    }

    private async Task ReadLoop()
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var res = await _ws.ReceiveAsync(buf, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, res.Count);
                if (!res.EndOfMessage) continue;
                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                ms.SetLength(0);
                if (node?["id"] is { } idNode)
                {
                    if (_pending.TryGetValue((int)idNode, out var tcs))
                    {
                        if (node["error"] is { } err) tcs.TrySetException(new NightModeException("Chrome: " + err["message"]));
                        else tcs.TrySetResult(node["result"]);
                    }
                }
                else if ((string?)node?["method"] is { } m) Event?.Invoke(m, node["params"]);
            }
        }
        catch (Exception) { }
        Closed = true;
        foreach (var t in _pending.Values) t.TrySetException(new NightModeException("The connection to Chrome was lost. Was the Chrome window closed?"));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
        catch (Exception) { }
        _ws.Dispose();
    }
}

public class NightModeException(string message) : Exception(message);
