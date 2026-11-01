using UTubeBulkUploader.Core;

namespace UTubeBulkUploader.Tests;

/// <summary>A throwaway folder for one test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ubu-test-" + Guid.NewGuid().ToString("N")[..8]);
    public string State => Path.Combine(Root, "_state");

    public TempDir()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(State);
    }

    /// <summary>Creates a fake clip with unique content and the given modified time.</summary>
    public string Clip(string relativePath, DateTime modified, int sizeKb = 4)
    {
        var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[sizeKb * 1024];
        new Random(relativePath.GetHashCode()).NextBytes(bytes);
        var tag = System.Text.Encoding.UTF8.GetBytes(relativePath);
        Array.Copy(tag, bytes, Math.Min(tag.Length, bytes.Length));
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTime(path, modified);
        return path;
    }

    public string Path_(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string[] Names(string relativeFolder) =>
        Directory.GetFiles(Path_(relativeFolder)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

public static class Fixture
{
    public static readonly ISet<string> Ext = new AppSettings().VideoExtensionSet;

    /// <summary>Reads no metadata, so every clip uses its modified time.</summary>
    public sealed class NoMetadata : IRecordingDateReader
    {
        public DateTime? Read(string path) => null;
    }

    public static FolderNode Scan(string folder, LogStore? log = null)
    {
        var story = new Scanner(new NoMetadata(), Ext).Scan(folder, log?.DoneFingerprints() ?? new HashSet<string>());
        Namer.Apply(story);
        Namer.DetectCollisions([story], f => Directory.Exists(f) ? Directory.GetFiles(f).Select(Path.GetFileName)! : []);
        return story;
    }

    public static (LogStore log, RenameEngine engine) Engine(TempDir t)
    {
        var log = new LogStore(Path.Combine(t.Root, "_log", "log.csv"), t.State);
        log.Load();
        return (log, new RenameEngine(log, t.State));
    }

    public static RenameResult RenameAll(RenameEngine engine, params FolderNode[] stories)
    {
        var run = RenameEngine.NewRunId();
        return engine.Rename(RenameEngine.PlanFromStories(stories, run), run);
    }

    public static Clip ClipNamed(FolderNode story, string currentName) =>
        story.AllClips().Single(c => c.CurrentName == currentName);
}
