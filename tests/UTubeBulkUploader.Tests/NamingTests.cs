using UTubeBulkUploader.Core;
using Xunit;
using static UTubeBulkUploader.Tests.Fixture;

namespace UTubeBulkUploader.Tests;

public class NamingTests
{
    private static readonly DateTime Day1 = new(2026, 10, 26, 9, 0, 0);
    private static readonly DateTime Day2 = new(2026, 10, 27, 9, 0, 0);

    [Fact]
    public void Clips_directly_in_story_use_space_format()
    {
        using var t = new TempDir();
        t.Clip("Collector Visit/MVI_0002.MP4", Day1.AddMinutes(5));
        t.Clip("Collector Visit/MVI_0001.MP4", Day1);

        var story = Scan(t.Path_("Collector Visit"));

        Assert.Equal("Collector Visit 1 (26.10.26).MP4", ClipNamed(story, "MVI_0001.MP4").NewName);
        Assert.Equal("Collector Visit 2 (26.10.26).MP4", ClipNamed(story, "MVI_0002.MP4").NewName);
    }

    [Fact]
    public void Three_levels_of_nesting_join_with_dashes_and_number_per_folder()
    {
        using var t = new TempDir();
        t.Clip("Collector Visit/a.mp4", Day1);
        t.Clip("Collector Visit/Phone 1/b.mp4", Day1);
        t.Clip("Collector Visit/Phone 1/Day 2/c.mov", Day2);
        t.Clip("Collector Visit/Phone 1/Day 2/d.mov", Day2.AddHours(1));
        t.Clip("Collector Visit/Phone 1/Day 2/e.mov", Day2.AddHours(2));

        var story = Scan(t.Path_("Collector Visit"));

        Assert.Equal("Collector Visit 1 (26.10.26).mp4", ClipNamed(story, "a.mp4").NewName);
        Assert.Equal("Collector Visit - Phone 1 - 1 (26.10.26).mp4", ClipNamed(story, "b.mp4").NewName);
        Assert.Equal("Collector Visit - Phone 1 - Day 2 - 1 (27.10.26).mov", ClipNamed(story, "c.mov").NewName);
        Assert.Equal("Collector Visit - Phone 1 - Day 2 - 3 (27.10.26).mov", ClipNamed(story, "e.mov").NewName);
    }

    [Fact]
    public void Numbering_follows_recording_time_not_file_name()
    {
        using var t = new TempDir();
        t.Clip("S/z.mp4", Day1);
        t.Clip("S/a.mp4", Day1.AddMinutes(10));

        var story = Scan(t.Path_("S"));

        Assert.Equal(1, ClipNamed(story, "z.mp4").Number);
        Assert.Equal(2, ClipNamed(story, "a.mp4").Number);
    }

    [Fact]
    public void Folder_date_defaults_to_earliest_clip_and_edit_applies_to_all_clips()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day2);
        t.Clip("S/b.mp4", Day1.AddHours(3));

        var story = Scan(t.Path_("S"));
        Assert.Equal(new DateOnly(2026, 10, 26), story.Date);
        Assert.All(story.Clips, c => Assert.Contains("(26.10.26)", c.NewName));

        story.EditedDate = new DateOnly(2026, 1, 5);
        Namer.Apply(story);
        Assert.All(story.Clips, c => Assert.Contains("(05.01.26)", c.NewName));
    }

    [Fact]
    public void Editing_a_folder_name_changes_titles_but_not_the_disk()
    {
        using var t = new TempDir();
        t.Clip("S/Cam A/a.mp4", Day1);
        var story = Scan(t.Path_("S"));

        story.Children[0].DisplayName = "Main camera";
        story.DisplayName = "Flood Relief";
        Namer.Apply(story);

        Assert.Equal("Flood Relief - Main camera - 1 (26.10.26).mp4", story.Children[0].Clips[0].NewName);
        Assert.True(Directory.Exists(t.Path_("S/Cam A")));
    }

    [Fact]
    public void Reordering_renumbers()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        t.Clip("S/b.mp4", Day1.AddMinutes(1));
        t.Clip("S/c.mp4", Day1.AddMinutes(2));
        var story = Scan(t.Path_("S"));

        story.MoveClip(ClipNamed(story, "c.mp4"), 0);
        Namer.Apply(story);

        Assert.Equal(1, ClipNamed(story, "c.mp4").Number);
        Assert.Equal(2, ClipNamed(story, "a.mp4").Number);
        Assert.Equal(3, ClipNamed(story, "b.mp4").Number);
    }

    [Fact]
    public void Unticked_clips_are_not_renamed_or_numbered()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        t.Clip("S/b.mp4", Day1.AddMinutes(1));
        t.Clip("S/c.mp4", Day1.AddMinutes(2));
        var story = Scan(t.Path_("S"));

        ClipNamed(story, "a.mp4").State = ClipState.Skipped;
        Namer.Apply(story);

        Assert.Equal("a.mp4", ClipNamed(story, "a.mp4").NewName);
        Assert.False(ClipNamed(story, "a.mp4").NeedsRename);
        Assert.Equal("S 1 (26.10.26).mp4", ClipNamed(story, "b.mp4").NewName);
    }

    [Fact]
    public void Only_video_types_are_picked_up_case_insensitively()
    {
        using var t = new TempDir();
        t.Clip("S/a.MTS", Day1);
        t.Clip("S/b.m2ts", Day1);
        t.Clip("S/c.MXF", Day1);
        t.Clip("S/c.XML", Day1);
        t.Clip("S/c.THM", Day1);
        t.Clip("S/notes.txt", Day1);

        var story = Scan(t.Path_("S"));

        Assert.Equal(3, story.Clips.Count);
    }

    [Fact]
    public void Illegal_characters_and_trailing_dots_are_removed()
    {
        Assert.Equal("Visit Phone 1", Namer.SanitizePart("Visit: Phone|1"));
        Assert.Equal("A B C", Namer.SanitizePart("A*B?C"));
        Assert.Equal("Ends with dots", Namer.SanitizePart("Ends with dots... "));
        Assert.Equal("Untitled", Namer.SanitizePart("???"));
        Assert.Equal("Quote it", Namer.SanitizePart("\"Quote\" <it>"));
    }

    [Fact]
    public void Titles_are_kept_to_100_characters_by_cutting_the_middle()
    {
        var parts = new[] { "Very Long Story Name About The District Collector Visit", "Phone camera of the second reporter", "Day 2 afternoon session" };
        var (title, shortened) = Namer.BuildTitle(parts, 12, "26.10.26");

        Assert.True(shortened);
        Assert.True(title.Length <= 100, $"Length was {title.Length}");
        Assert.StartsWith("Very Long Story", title);
        Assert.EndsWith("afternoon session - 12 (26.10.26)", title);
        Assert.Contains("…", title);
    }

    [Fact]
    public void Short_titles_are_not_marked_shortened()
    {
        var (title, shortened) = Namer.BuildTitle(["Story", "Cam"], 1, "01.01.26");
        Assert.False(shortened);
        Assert.Equal("Story - Cam - 1 (01.01.26)", title);
    }

    [Fact]
    public void Existing_file_with_target_name_is_a_collision()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        t.Clip("S/S 1 (26.10.26).mp4", Day1.AddMinutes(5));
        var story = Scan(t.Path_("S"));

        // The user skips the second clip, so it keeps its name, which is the target for the first.
        ClipNamed(story, "S 1 (26.10.26).mp4").State = ClipState.Skipped;
        Namer.Apply(story);
        int n = Namer.DetectCollisions([story], f => Directory.GetFiles(f).Select(Path.GetFileName)!);

        Assert.Equal(1, n);
        Assert.NotNull(ClipNamed(story, "a.mp4").Collision);
    }

    [Fact]
    public void Names_swapping_between_clips_is_not_a_collision()
    {
        using var t = new TempDir();
        t.Clip("S/S 2 (26.10.26).mp4", Day1);              // recorded first, so becomes 1
        t.Clip("S/S 1 (26.10.26).mp4", Day1.AddMinutes(5)); // becomes 2
        var story = Scan(t.Path_("S"));

        Assert.Equal(0, story.Clips.Count(c => c.Collision is not null));
        Assert.Equal("S 1 (26.10.26).mp4", ClipNamed(story, "S 2 (26.10.26).mp4").NewName);
    }

    [Fact]
    public void Overlapping_folders_are_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "Footage");
        Assert.NotNull(Namer.CheckOverlap(Path.Combine(root, "Story"), [root]));
        Assert.NotNull(Namer.CheckOverlap(root, [Path.Combine(root, "Story")]));
        Assert.NotNull(Namer.CheckOverlap(root, [root]));
        Assert.Null(Namer.CheckOverlap(Path.Combine(root, "Story 2"), [Path.Combine(root, "Story")]));
    }

    [Fact]
    public void Natural_sort_puts_2_before_10()
    {
        var list = new List<string> { "Clip 10", "Clip 2", "Clip 1" };
        list.Sort(NaturalStringComparer.Instance);
        Assert.Equal(new[] { "Clip 1", "Clip 2", "Clip 10" }, list);
    }

    [Fact]
    public void Empty_folders_are_flagged()
    {
        using var t = new TempDir();
        t.Clip("S/a.mp4", Day1);
        Directory.CreateDirectory(t.Path_("S/Empty"));
        var story = Scan(t.Path_("S"));
        Assert.True(story.Children.Single().IsEmpty);
        Assert.False(story.IsEmpty);
    }
}

public class DateParsingTests
{
    [Fact]
    public void Accepts_common_date_forms()
    {
        foreach (var s in new[] { "26.10.26", "26.10.2026", "26/10/26", "26-10-26", " 26.10.26 " })
        {
            Assert.True(Namer.TryParseDate(s, out var d), s);
            Assert.Equal(new DateOnly(2026, 10, 26), d);
        }
        Assert.True(Namer.TryParseDate("6.1.26", out var e));
        Assert.Equal(new DateOnly(2026, 1, 6), e);
    }

    [Fact]
    public void Rejects_impossible_dates()
    {
        foreach (var s in new[] { "31.02.26", "26.13.26", "abc", "26.10", "" })
            Assert.False(Namer.TryParseDate(s, out _));
    }
}
