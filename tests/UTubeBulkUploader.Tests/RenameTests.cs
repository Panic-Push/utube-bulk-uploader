using UTubeBulkUploader.Core;
using Xunit;
using static UTubeBulkUploader.Tests.Fixture;

namespace UTubeBulkUploader.Tests;

public class RenameTests
{
    private static readonly DateTime Day1 = new(2026, 10, 26, 9, 0, 0);

    private static void MakeNested(TempDir t)
    {
        t.Clip("Collector Visit/a.mp4", Day1);
        t.Clip("Collector Visit/b.mp4", Day1.AddMinutes(1));
        t.Clip("Collector Visit/Phone 1/c.mp4", Day1);
        t.Clip("Collector Visit/Phone 1/Day 2/d.mov", Day1.AddDays(1));
        t.Clip("Collector Visit/Phone 1/Day 2/e.mov", Day1.AddDays(1).AddMinutes(1));
        File.WriteAllText(t.Path_("Collector Visit/Phone 1/Day 2/d.XML"), "sidecar");
    }

    [Fact]
    public void Acceptance_rename_undo_and_second_run()
    {
        using var t = new TempDir();
        MakeNested(t);
        var (log, engine) = Engine(t);

        var result = RenameAll(engine, Scan(t.Path_("Collector Visit"), log));
        Assert.Empty(result.Failures);
        Assert.Equal(5, result.Done.Count);

        Assert.Equal(new[] { "Collector Visit 1 (26.10.26).mp4", "Collector Visit 2 (26.10.26).mp4" }, t.Names("Collector Visit"));
        Assert.Equal(new[] { "Collector Visit - Phone 1 - 1 (26.10.26).mp4" }, t.Names("Collector Visit/Phone 1"));
        Assert.Equal(new[] { "Collector Visit - Phone 1 - Day 2 - 1 (27.10.26).mov", "Collector Visit - Phone 1 - Day 2 - 2 (27.10.26).mov", "d.XML" },
            t.Names("Collector Visit/Phone 1/Day 2"));

        // Second run finds nothing new.
        var again = Scan(t.Path_("Collector Visit"), log);
        Assert.All(again.AllClips(), c => Assert.Equal(ClipState.AlreadyDone, c.State));
        Assert.DoesNotContain(again.AllClips(), c => c.NeedsRename);

        // Undo restores every original name.
        var undo = engine.Undo(result.RunId);
        Assert.Empty(undo.Failures);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, t.Names("Collector Visit"));
        Assert.Equal(new[] { "c.mp4" }, t.Names("Collector Visit/Phone 1"));
        Assert.Equal(new[] { "d.XML", "d.mov", "e.mov" }, t.Names("Collector Visit/Phone 1/Day 2"));
        Assert.All(log.Rows, r => Assert.Equal(LogStatus.Undone, r.Status));

        // After undo the clips count as new again.
        Assert.All(Scan(t.Path_("Collector Visit"), log).AllClips(), c => Assert.Equal(ClipState.Pending, c.State));
    }

    [Fact]
    public void Log_is_written_with_bom_and_reloads()
    {
        using var t = new TempDir();
        MakeNested(t);
        var (log, engine) = Engine(t);
        RenameAll(engine, Scan(t.Path_("Collector Visit"), log));

        var bytes = File.ReadAllBytes(log.LogPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        var reloaded = new LogStore(log.LogPath, t.State);
        reloaded.Load();
        Assert.Equal(5, reloaded.Rows.Count);
        Assert.All(reloaded.Rows, r => Assert.Equal(LogStatus.Renamed, r.Status));
        Assert.Equal(log.Rows[0].Fingerprint, reloaded.Rows[0].Fingerprint);
    }

    [Fact]
    public void Swapped_names_rename_without_overwriting()
    {
        using var t = new TempDir();
        var first = File.ReadAllBytes(t.Clip("S/S 2 (26.10.26).mp4", Day1));
        var second = File.ReadAllBytes(t.Clip("S/S 1 (26.10.26).mp4", Day1.AddMinutes(5)));
        var (log, engine) = Engine(t);

        var r = RenameAll(engine, Scan(t.Path_("S"), log));

        Assert.Empty(r.Failures);
        Assert.Equal(first, File.ReadAllBytes(t.Path_("S/S 1 (26.10.26).mp4")));
        Assert.Equal(second, File.ReadAllBytes(t.Path_("S/S 2 (26.10.26).mp4")));
    }

    [Fact]
    public void Crash_halfway_can_be_finished_on_next_launch()
    {
        using var t = new TempDir();
        MakeNested(t);
        var (log, engine) = Engine(t);
        var story = Scan(t.Path_("Collector Visit"), log);

        int moves = 0;
        engine.BeforeMove = (_, _) => { if (++moves == 4) throw new SimulatedCrash(); };
        Assert.Throws<SimulatedCrash>(() => RenameAll(engine, story));

        // "Next launch": a fresh engine finds the journal.
        var (log2, engine2) = Engine(t);
        Assert.NotNull(engine2.ReadUnfinished());
        var r = engine2.FinishUnfinished();

        Assert.Empty(r.Failures);
        Assert.Null(engine2.ReadUnfinished());
        Assert.Equal(new[] { "Collector Visit 1 (26.10.26).mp4", "Collector Visit 2 (26.10.26).mp4" }, t.Names("Collector Visit"));
        Assert.Equal(5, log2.Rows.Count);
        Assert.DoesNotContain(Directory.GetFiles(t.Root, "*", SearchOption.AllDirectories), f => Path.GetFileName(f).StartsWith(RenameEngine.TempPrefix));
    }

    [Fact]
    public void Crash_in_second_pass_can_be_rolled_back()
    {
        using var t = new TempDir();
        t.Clip("S/S 2 (26.10.26).mp4", Day1);
        t.Clip("S/S 1 (26.10.26).mp4", Day1.AddMinutes(5));
        t.Clip("S/x.mp4", Day1.AddMinutes(9));
        var (log, engine) = Engine(t);
        var story = Scan(t.Path_("S"), log);

        int moves = 0; // 3 moves to temp, then crash after the first final move
        engine.BeforeMove = (_, _) => { if (++moves == 5) throw new SimulatedCrash(); };
        Assert.Throws<SimulatedCrash>(() => RenameAll(engine, story));

        var (_, engine2) = Engine(t);
        var r = engine2.RollBackUnfinished();

        Assert.Empty(r.Failures);
        Assert.Equal(new[] { "S 1 (26.10.26).mp4", "S 2 (26.10.26).mp4", "x.mp4" }, t.Names("S"));
        Assert.Null(engine2.ReadUnfinished());
        // Contents went back to the right names.
        var fresh = Scan(t.Path_("S"));
        Assert.Equal(Day1, ClipNamed(fresh, "S 2 (26.10.26).mp4").RecordingTime);
    }

    [Fact]
    public void Windows_refusing_a_file_rolls_back_that_folder_only()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        t.Clip("S/Cam/b.mp4", Day1);
        t.Clip("S/Cam/c.mp4", Day1.AddMinutes(1));
        var (log, engine) = Engine(t);
        var story = Scan(t.Path_("S"), log);

        engine.BeforeMove = (from, _) =>
        {
            if (Path.GetFileName(from) == "c.mp4") throw new IOException("The process cannot access the file because it is being used by another process.");
        };
        var r = RenameAll(engine, story);

        var failure = Assert.Single(r.Failures);
        Assert.True(failure.RolledBack);
        Assert.Equal(new[] { "b.mp4", "c.mp4" }, t.Names("S/Cam"));   // rolled back
        Assert.Equal(new[] { "S 1 (26.10.26).mp4" }, t.Names("S")); // other folder done
        Assert.Single(log.Rows);
        Assert.Null(engine.ReadUnfinished());
    }

    [Fact]
    public void Plan_refuses_to_run_with_collisions()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        var story = Scan(t.Path_("S"));
        story.Clips[0].Collision = "clash";
        Assert.Throws<InvalidOperationException>(() => RenameEngine.PlanFromStories([story], "r"));
    }

    [Fact]
    public void Same_footage_on_another_drive_letter_is_recognised()
    {
        using var t = new TempDir();
        t.Clip("D/Story/a.mp4", Day1);
        var (log, engine) = Engine(t);
        RenameAll(engine, Scan(t.Path_("D/Story"), log));

        // Simulate the drive showing up under a different letter: copy the folder elsewhere.
        Directory.CreateDirectory(t.Path_("E/Story"));
        foreach (var f in Directory.GetFiles(t.Path_("D/Story")))
            File.Copy(f, Path.Combine(t.Path_("E/Story"), Path.GetFileName(f)));

        var other = Scan(t.Path_("E/Story"), log);
        Assert.All(other.AllClips(), c => Assert.Equal(ClipState.AlreadyDone, c.State));
    }

    public sealed class SimulatedCrash : Exception;
}
