using System.Text;

namespace UTubeBulkUploader.Core;

/// <summary>Builds new file names per the naming rules (§4).</summary>
public static class Namer
{
    public const int MaxTitleLength = 100;
    public const string DateFormat = "dd.MM.yy";
    private const char Ellipsis = '…';
    private static readonly char[] Illegal = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Recomputes NewName, Number and WasShortened for every clip under the story.</summary>
    public static void Apply(FolderNode story)
    {
        foreach (var folder in story.SelfAndDescendants())
        {
            var parts = folder.TitleParts().Select(SanitizePart).ToList();
            string date = folder.Date?.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            int n = folder.Clips.Count(c => c.State == ClipState.AlreadyDone);

            foreach (var clip in folder.Clips.OrderBy(c => c.Order))
            {
                clip.Collision = null;
                if (!clip.IsIncluded)
                {
                    clip.NewName = clip.CurrentName;
                    clip.Number = 0;
                    clip.WasShortened = false;
                    continue;
                }
                n++;
                var (title, shortened) = BuildTitle(parts, n, date);
                clip.NewName = title + clip.Extension;
                clip.Number = n;
                clip.WasShortened = shortened;
            }
        }
    }

    /// <summary>Builds the title (file name without extension).</summary>
    public static (string title, bool shortened) BuildTitle(IReadOnlyList<string> parts, int number, string date)
    {
        string separator = parts.Count == 1 ? " " : " - ";
        string tail = string.IsNullOrEmpty(date) ? $"{separator}{number}" : $"{separator}{number} ({date})";
        string prefix = string.Join(" - ", parts);

        bool shortened = false;
        int budget = MaxTitleLength - tail.Length;
        if (prefix.Length > budget)
        {
            prefix = ShortenMiddle(prefix, budget);
            shortened = true;
        }
        var title = (prefix + tail).TrimEnd('.', ' ');
        return (title, shortened);
    }

    /// <summary>Cuts characters out of the middle so the story name and deepest folder stay readable.</summary>
    public static string ShortenMiddle(string s, int maxLength)
    {
        if (s.Length <= maxLength) return s;
        if (maxLength <= 1) return s[..Math.Max(0, maxLength)];
        int keep = maxLength - 1; // room for the ellipsis
        int head = (keep + 1) / 2;
        int tail = keep - head;
        return s[..head].TrimEnd() + Ellipsis + s[^tail..].TrimStart();
    }

    /// <summary>Removes characters Windows doesn't allow, plus trailing dots and spaces.</summary>
    public static string SanitizePart(string part)
    {
        var sb = new StringBuilder(part.Length);
        foreach (var ch in part)
        {
            if (Array.IndexOf(Illegal, ch) >= 0 || char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        // Collapse runs of spaces left behind by removed characters.
        var cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.Trim().TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }

    /// <summary>
    /// Marks clips whose new name would clash. A clash is either two clips getting the same
    /// name, or a file already on disk with that name that isn't itself being renamed now.
    /// </summary>
    /// <param name="listFolder">Returns the file names currently in a folder.</param>
    /// <returns>Number of clips with a collision.</returns>
    public static int DetectCollisions(IEnumerable<FolderNode> stories, Func<string, IEnumerable<string>> listFolder)
    {
        int count = 0;
        var folders = stories.SelectMany(s => s.SelfAndDescendants())
            .GroupBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in folders)
        {
            var clips = group.SelectMany(f => f.Clips).ToList();
            foreach (var c in clips) c.Collision = null;

            var moving = clips.Where(c => c.NeedsRename).ToList();
            if (moving.Count == 0) continue;

            // Names that will be free once this run's renames have happened.
            var leaving = new HashSet<string>(moving.Select(c => c.CurrentName), StringComparer.OrdinalIgnoreCase);
            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in listFolder(group.Key)) onDisk.Add(name);
            // Clips not being renamed keep their names, so they stay "on disk" too.
            foreach (var c in clips.Where(c => !c.NeedsRename)) onDisk.Add(c.CurrentName);

            var targets = moving.GroupBy(c => c.NewName, StringComparer.OrdinalIgnoreCase);
            foreach (var t in targets)
            {
                if (t.Count() > 1)
                {
                    foreach (var c in t)
                    {
                        c.Collision = $"{t.Count()} clips would get the same name. Change a folder name or date.";
                        count++;
                    }
                    continue;
                }
                var clip = t.First();
                if (onDisk.Contains(clip.NewName) && !leaving.Contains(clip.NewName))
                {
                    clip.Collision = $"A file called \"{clip.NewName}\" already exists in this folder. Rename or move that file first.";
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>Returns a reason if the folder can't be added next to the existing stories.</summary>
    public static string? CheckOverlap(string newFolder, IEnumerable<string> existing)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newFolder)) + Path.DirectorySeparatorChar;
        foreach (var e in existing)
        {
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(e)) + Path.DirectorySeparatorChar;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return "This folder is already in the list.";
            if (a.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                return $"This folder is inside \"{Path.GetFileName(b.TrimEnd(Path.DirectorySeparatorChar))}\", which is already in the list. Its clips are already included there.";
            if (b.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return $"This folder contains \"{Path.GetFileName(b.TrimEnd(Path.DirectorySeparatorChar))}\", which is already in the list. Remove that one first if you want to add this whole folder as one story.";
        }
        return null;
    }
}
