using System.Buffers.Binary;
using UTubeBulkUploader.Core;
using Xunit;

namespace UTubeBulkUploader.Tests;

public class FileTests
{
    [Fact]
    public void Fingerprint_survives_rename_and_tells_files_apart()
    {
        using var t = new TempDir();
        var a = t.Clip("x/a.mp4", DateTime.Now, sizeKb: 3000); // bigger than 2 MB: head and tail
        var b = t.Clip("x/b.mp4", DateTime.Now, sizeKb: 3000);
        var fa = Fingerprint.Compute(a);

        var moved = Path.Combine(Path.GetDirectoryName(a)!, "renamed.mp4");
        File.Move(a, moved);

        Assert.Equal(fa, Fingerprint.Compute(moved));
        Assert.NotEqual(fa, Fingerprint.Compute(b));
        Assert.StartsWith((3000 * 1024).ToString() + "-", fa);
    }

    [Fact]
    public void Fingerprint_works_for_small_files()
    {
        using var t = new TempDir();
        var a = t.Clip("x/a.mp4", DateTime.Now, sizeKb: 1);
        Assert.Equal(Fingerprint.Compute(a), Fingerprint.Compute(a));
    }

    [Fact]
    public void Csv_round_trips_commas_quotes_and_unicode()
    {
        var rows = new List<LogRow>
        {
            new()
            {
                RunId = "r1", DateTime = new DateTime(2026, 11, 1, 10, 0, 0), FolderPath = @"E:\News\Visit, ""Day 1""",
                OldName = "MVI_1.MP4", NewName = "धार दौरा 1 (01.11.26).MP4", Size = 123, Fingerprint = "123-abc",
                Status = LogStatus.Renamed, StatusTime = new DateTime(2026, 11, 1, 10, 0, 1), Note = "line1\nline2",
            },
        };
        var text = LogStore.Serialize(rows);
        var back = LogStore.Parse("\uFEFF" + text);

        var r = Assert.Single(back);
        Assert.Equal(rows[0].FolderPath, r.FolderPath);
        Assert.Equal(rows[0].NewName, r.NewName);
        Assert.Equal(rows[0].Note, r.Note);
        Assert.Equal(rows[0].StatusTime, r.StatusTime);
        Assert.Null(r.RecordingDate);
    }

    [Fact]
    public void Log_backup_keeps_changes_when_log_is_missing()
    {
        using var t = new TempDir();
        var log = new LogStore(Path.Combine(t.Root, "log.csv"), t.State);
        log.Rows.Add(new LogRow { RunId = "r", Fingerprint = "f", Status = LogStatus.Renamed });
        log.Save();
        File.Delete(log.LogPath);

        var again = new LogStore(log.LogPath, t.State);
        again.Load();
        Assert.Single(again.Rows);
    }

    [Fact]
    public void Mp4_creation_time_is_read_from_mvhd()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Root, "clip.mp4");
        var when = new DateTime(2026, 10, 26, 4, 30, 0, DateTimeKind.Utc);
        File.WriteAllBytes(path, MinimalMp4(when));

        var read = new Mp4DateReader().Read(path);

        Assert.Equal(when.ToLocalTime(), read);
    }

    [Fact]
    public void Mp4_reader_ignores_other_types_and_junk()
    {
        using var t = new TempDir();
        var junk = Path.Combine(t.Root, "junk.mp4");
        File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });
        Assert.Null(new Mp4DateReader().Read(junk));
        Assert.Null(new Mp4DateReader().Read(Path.Combine(t.Root, "x.mts")));
    }

    [Fact]
    public void Implausible_dates_fall_through_to_next_reader()
    {
        var chain = new ChainedDateReader(new Fixed(new DateTime(1904, 1, 1)), new Fixed(new DateTime(2026, 1, 1)));
        Assert.Equal(new DateTime(2026, 1, 1), chain.Read("x"));
    }

    private sealed class Fixed(DateTime d) : IRecordingDateReader
    {
        public DateTime? Read(string path) => d;
    }

    private static byte[] MinimalMp4(DateTime utc)
    {
        static byte[] Box(string type, byte[] body)
        {
            var b = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            body.CopyTo(b, 8);
            return b;
        }
        var mvhd = new byte[100];
        uint secs = (uint)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(4), secs);
        var ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("isom\0\0\0\0isom"));
        var mdat = Box("mdat", new byte[64]);
        var moov = Box("moov", Box("mvhd", mvhd));
        return [.. ftyp, .. mdat, .. moov];
    }
}
