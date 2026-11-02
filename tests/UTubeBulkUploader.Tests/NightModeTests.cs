using System.Text.Json;
using UTubeBulkUploader.Core;
using Xunit;

namespace UTubeBulkUploader.Tests;

public class BatchTests
{
    private static LogStore LogWith(TempDir t, int count, Func<int, string>? status = null)
    {
        var log = new LogStore(Path.Combine(t.Root, "log.csv"), t.State);
        for (int i = 1; i <= count; i++)
        {
            var name = $"Story - {i} (30.10.26).mp4";
            File.WriteAllBytes(Path.Combine(t.Root, name), new byte[100 + i]);
            log.Rows.Add(new LogRow
            {
                RunId = "r", DateTime = DateTime.Now, FolderPath = t.Root, OldName = $"v{i}.mp4", NewName = name, Size = 100 + i,
                Fingerprint = "fp" + i, Status = status?.Invoke(i) ?? LogStatus.Renamed, StatusTime = DateTime.Now,
            });
        }
        return log;
    }

    [Fact]
    public void FullBatchesOfFifteenInRenameOrder()
    {
        using var t = new TempDir();
        var log = LogWith(t, 33);
        var b = BatchPlanner.Plan(log, 15, out var missing);
        Assert.Equal(new[] { 15, 15, 3 }, b.Select(x => x.Rows.Count));
        Assert.Equal("Story - 1 (30.10.26).mp4", b[0].FirstName);
        Assert.Equal("Story - 16 (30.10.26).mp4", b[1].FirstName);
        Assert.Empty(missing);
    }

    [Fact]
    public void TodaysUploadsAreCounted()
    {
        using var t = new TempDir();
        var log = LogWith(t, 5, i => i <= 3 ? LogStatus.Uploaded : LogStatus.Renamed);
        log.Rows[0].StatusTime = DateTime.Today.AddDays(-1);
        Assert.Equal(2, BatchPlanner.UploadedToday(log));
    }

    [Fact]
    public void UploadedUndoneAndMissingAreLeftOut()
    {
        using var t = new TempDir();
        var log = LogWith(t, 6, i => i == 2 ? LogStatus.Uploaded : i == 3 ? LogStatus.Undone : LogStatus.Renamed);
        File.Delete(Path.Combine(t.Root, "Story - 4 (30.10.26).mp4"));
        var b = BatchPlanner.Plan(log, 15, out var missing);
        Assert.Equal(new[] { "fp1", "fp5", "fp6" }, b.Single().Rows.Select(r => r.Fingerprint));
        Assert.Equal("fp4", missing.Single().Fingerprint);
    }

    [Fact]
    public void MarkUploadedIsSavedAndNotOfferedAgain()
    {
        using var t = new TempDir();
        var log = LogWith(t, 20);
        var b = BatchPlanner.Plan(log, 15, out _);
        Assert.True(BatchPlanner.MarkUploaded(log, b[0], "Test."));
        var reread = new LogStore(log.LogPath, t.State);
        reread.Load();
        Assert.Equal(15, reread.Rows.Count(r => r.Status == LogStatus.Uploaded));
        Assert.Equal(5, BatchPlanner.Plan(reread, 15, out _).Single().Rows.Count);
    }

    [Fact]
    public void UploadUrlsAreRecognised()
    {
        Assert.True(StudioDriver.IsUploadUrl("https://upload.youtube.com/upload/studio?authuser=0"));
        Assert.False(StudioDriver.IsUploadUrl("https://studio.youtube.com/youtubei/v1/creator/list_creator_videos"));
    }
}

public class VideoDetailsTests
{
    [Fact]
    public void DescriptionHasFileNameAndUploadDate()
    {
        Assert.Equal("File Name: Collector Visit - Phone 1 - 1 (26.10.26), Upload Date: 01.11.26",
            VideoDetails.Description("Collector Visit - Phone 1 - 1 (26.10.26)", new DateTime(2026, 11, 1)));
        Assert.Equal("May 2025 - Muth - 3 (30.10.26)", VideoDetails.Title(Path.Combine("x", "May 2025 - Muth - 3 (30.10.26).mp4")));
    }
}

/// <summary>Runs night mode for real in Chrome (headless) against the test copy of Studio, when Chrome is installed.</summary>
public class NightPracticeTests
{
    private sealed record Saved(int n, string file, string title, string description, bool notForKids, bool @private, string link, bool completed);

    private sealed record Outcome(BatchResult Result, LogStore Log, TempDir Dir, List<Saved> Saved, int Loads) : IDisposable
    {
        public void Dispose() => Dir.Dispose();
    }

    private static async Task<Outcome?> Run(int clips, string query, int refreshEvery = 20, TimeSpan? cutoff = null, Action<LogStore>? prepare = null)
    {
        var chrome = ChromeLauncher.FindChrome(Environment.GetEnvironmentVariable("UBU_CHROME"));
        if (chrome is null) return null;
        var t = new TempDir();
        Environment.SetEnvironmentVariable("UBU_CHROME_ARGS", "--headless=new --no-sandbox");
        var log = new LogStore(Path.Combine(t.Root, "log.csv"), t.State);
        for (int i = 1; i <= clips; i++)
        {
            var name = $"Story - Cam 1 - {i} (30.10.26).mp4";
            File.WriteAllBytes(Path.Combine(t.Root, name), new byte[500 + i]);
            log.Rows.Add(new LogRow { RunId = "r", FolderPath = t.Root, NewName = name, Size = 500 + i, Fingerprint = "fp" + i, Status = LogStatus.Renamed });
        }
        prepare?.Invoke(log);
        log.Save();
        // A test that hangs fails after 4 minutes with what it was doing, instead of stalling the build.
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var port = await ChromeLauncher.EnsureRunningAsync(chrome, Path.Combine(t.Root, "profile"), "about:blank", limit.Token);
        await using var tab = await CdpTab.ConnectAsync(port, limit.Token);
        var driver = new StudioDriver(tab, StudioTarget.PracticePage(query))
        {
            ClickPause = TimeSpan.FromMilliseconds(150), StepTimeout = TimeSpan.FromSeconds(10), ScreenshotDir = t.Root,
            MinPerVideo = TimeSpan.Zero, UploadCutoff = cutoff ?? TimeSpan.FromSeconds(30),
        };
        var lastStatus = "";
        driver.Status += st => lastStatus = st;
        await driver.InitAsync(limit.Token);
        var runner = new NightRunner { RefreshEvery = refreshEvery, UploadDate = new DateTime(2026, 11, 1) };
        var result = await runner.RunAsync(driver, log, limit.Token);
        if (limit.IsCancellationRequested) result = result with { Message = $"Test ran over 4 minutes. Last status: {lastStatus}" };
        var saved = JsonSerializer.Deserialize<List<Saved>>((string?)await tab.EvalAsync("localStorage.getItem('ubu-saved') || '[]'") ?? "[]")!;
        var loads = (int)double.Parse((string?)await tab.EvalAsync("localStorage.getItem('ubu-loads') || '0'") ?? "0");
        try { await tab.SendAsync("Browser.close"); } catch (Exception) { }
        return new Outcome(result, log, t, saved, loads);
    }

    [Fact]
    public async Task EachVideoUploadsFullyThenIsSavedPrivateWithTitleAndDescription()
    {
        using var o = await Run(4, "speed=1", refreshEvery: 2);
        if (o is null) return;
        Assert.True(o.Result.Outcome == BatchOutcome.Done, o.Result.Message);
        Assert.Equal(4, o.Log.Rows.Count(x => x.Status == LogStatus.Uploaded));
        Assert.All(o.Log.Rows, r => Assert.Contains("https://youtu.be/TEST", r.Note));
        Assert.Equal(4, o.Saved.Count);
        Assert.All(o.Saved, s => Assert.True(s.completed, "saved before its upload was complete"));
        var first = o.Saved.Single(s => s.file == "Story - Cam 1 - 1 (30.10.26)");
        Assert.Equal("Story - Cam 1 - 1 (30.10.26)", first.title);
        Assert.Equal("File Name: Story - Cam 1 - 1 (30.10.26), Upload Date: 01.11.26", first.description);
        Assert.All(o.Saved, s => { Assert.True(s.notForKids); Assert.True(s.@private); });
        Assert.True(o.Loads >= 2, "Studio should have been refreshed after 2 videos");
    }

    [Fact]
    public async Task AFailedUploadIsRecordedAndTheRestCarryOn()
    {
        using var o = await Run(3, "speed=1&fail=2");
        if (o is null) return;
        Assert.True(o.Result.Outcome == BatchOutcome.Done, o.Result.Message);
        Assert.Equal(2, o.Log.Rows.Count(x => x.Status == LogStatus.Uploaded));
        var failed = Assert.Single(o.Log.Rows, x => x.Status == LogStatus.Failed);
        Assert.Contains("Network error", failed.Note);
        Assert.Equal("fp2", BatchPlanner.Plan(o.Log, 15, out var _m).Single().Rows.Single().Fingerprint);
    }

    [Fact]
    public async Task ACutOffVideoIsSavedAnywayAndTheNextOneStarts()
    {
        using var o = await Run(3, "speed=1&stuck=2", cutoff: TimeSpan.FromSeconds(15));
        if (o is null) return;
        Assert.True(o.Result.Outcome == BatchOutcome.Done, o.Result.Message);
        Assert.Equal(2, o.Log.Rows.Count(x => x.Status == LogStatus.Uploaded));
        var cut = Assert.Single(o.Log.Rows, x => x.Status == LogStatus.Failed);
        Assert.Equal("fp2", cut.Fingerprint);
        Assert.Contains("cut-off", cut.Note);
        Assert.Equal(3, o.Saved.Count);   // saved anyway
    }

    [Fact]
    public async Task ThreeCutOffsInARowStopTheNight()
    {
        using var o = await Run(5, "speed=1&stuck=all", cutoff: TimeSpan.FromSeconds(4));
        if (o is null) return;
        Assert.Equal(BatchOutcome.TimedOut, o.Result.Outcome);
        Assert.Equal(3, o.Log.Rows.Count(x => x.Status == LogStatus.Failed));
        Assert.Equal(2, o.Log.Rows.Count(x => x.Status == LogStatus.Renamed));
    }

    [Fact]
    public async Task StopsWhenStudioShowsItsLimitAndLeavesTheRestWaiting()
    {
        using var o = await Run(4, "speed=1&limit=2");
        if (o is null) return;
        Assert.Equal(BatchOutcome.StudioLimit, o.Result.Outcome);
        Assert.NotNull(o.Result.ScreenshotPath);
        Assert.Equal(2, o.Log.Rows.Count(x => x.Status == LogStatus.Uploaded));
        Assert.Equal(2, o.Log.Rows.Count(x => x.Status == LogStatus.Renamed));
    }

    [Fact]
    public async Task ClipsLeftUploadingByAnEarlierRunAreTriedAgain()
    {
        using var o = await Run(1, "speed=1", prepare: log => log.Rows[0].Status = LogStatus.Uploading);
        if (o is null) return;
        Assert.True(o.Result.Outcome == BatchOutcome.Done, o.Result.Message);
        Assert.Equal(LogStatus.Uploaded, Assert.Single(o.Log.Rows).Status);
    }
}
