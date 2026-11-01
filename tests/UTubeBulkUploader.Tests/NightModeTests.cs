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

/// <summary>Runs night mode for real in Chrome (headless) against the practice page, when Chrome is installed.</summary>
public class NightPracticeTests
{
    private static async Task<(BatchResult, LogStore, TempDir)> Run(int clips, string query, bool practice)
    {
        var chrome = ChromeLauncher.FindChrome(Environment.GetEnvironmentVariable("UBU_CHROME"));
        var t = new TempDir();
        if (chrome is null) return (new BatchResult(BatchOutcome.Done, "no chrome"), null!, t);
        Environment.SetEnvironmentVariable("UBU_CHROME_ARGS", "--headless=new --no-sandbox");
        var log = new LogStore(Path.Combine(t.Root, "log.csv"), t.State);
        for (int i = 1; i <= clips; i++)
        {
            var name = $"Story - {i} (30.10.26).mp4";
            File.WriteAllBytes(Path.Combine(t.Root, name), new byte[500 + i]);
            log.Rows.Add(new LogRow { RunId = "r", FolderPath = t.Root, NewName = name, Size = 500 + i, Fingerprint = "fp" + i, Status = LogStatus.Renamed });
        }
        log.Save();
        var port = await ChromeLauncher.EnsureRunningAsync(chrome, Path.Combine(t.Root, "profile"), "about:blank", CancellationToken.None);
        await using var tab = await CdpTab.ConnectAsync(port, CancellationToken.None);
        var driver = new StudioDriver(tab, StudioTarget.PracticePage(query))
        {
            QuietPeriod = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromSeconds(1), ScreenshotDir = t.Root,
        };
        await driver.InitAsync(CancellationToken.None);
        var r = await new NightRunner { Gap = TimeSpan.FromSeconds(2) }.RunAsync(driver, log, 15, practice, CancellationToken.None);
        try { await tab.SendAsync("Browser.close"); } catch (Exception) { }
        return (r, log, t);
    }

    [Fact]
    public async Task UploadsAllBatchesAndMarksThem()
    {
        var (r, log, t) = await Run(17, "speed=1", practice: false);
        using var _ = t;
        if (r.Message == "no chrome") return;
        Assert.Equal(BatchOutcome.Done, r.Outcome);
        Assert.Equal(17, log.Rows.Count(x => x.Status == LogStatus.Uploaded));
    }

    [Fact]
    public async Task StopsWhenStudioShowsItsLimit()
    {
        var (r, log, t) = await Run(5, "speed=1&limit=2", practice: false);
        using var _ = t;
        if (r.Message == "no chrome") return;
        Assert.Equal(BatchOutcome.StudioLimit, r.Outcome);
        Assert.Contains("limit", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(r.ScreenshotPath);
        Assert.Equal(0, log.Rows.Count(x => x.Status == LogStatus.Uploaded));
    }
}
