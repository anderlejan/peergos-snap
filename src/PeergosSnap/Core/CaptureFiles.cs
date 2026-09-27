using System.Globalization;
using System.Text.RegularExpressions;

namespace PeergosSnap.Core;

/// <summary>How captures are sorted into subfolders on disk.</summary>
public enum SubfolderScheme { Month, Day, Year, None }

/// <summary>Where capture files go on disk, and recognising them later.</summary>
public static partial class CaptureFiles
{
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".mp4", ".webm"];

    /// <summary>The folder for a capture taken at <paramref name="t"/>: e.g. root\2026-10 for months.</summary>
    public static string Folder(string root, DateTime t, SubfolderScheme scheme) => scheme switch
    {
        SubfolderScheme.Month => Path.Combine(root, t.ToString("yyyy-MM", CultureInfo.InvariantCulture)),
        SubfolderScheme.Day => Path.Combine(root, t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        SubfolderScheme.Year => Path.Combine(root, t.ToString("yyyy", CultureInfo.InvariantCulture)),
        _ => root,
    };

    public static string Example(SubfolderScheme scheme) => scheme switch
    {
        SubfolderScheme.Month => "2026-10",
        SubfolderScheme.Day => "2026-10-05",
        SubfolderScheme.Year => "2026",
        _ => "",
    };

    [GeneratedRegex(@"^Snap_(\d{4}-\d{2}-\d{2})_(\d{2})-(\d{2})-(\d{2})")]
    private static partial Regex NameDate();

    /// <summary>The time in a capture's name ("Snap_2026-09-27_02-00-50.png"), or null.</summary>
    public static DateTime? DateFromName(string fileName)
    {
        var m = NameDate().Match(Path.GetFileName(fileName));
        if (!m.Success) return null;
        return DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}:{m.Groups[3].Value}:{m.Groups[4].Value}",
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    public static bool IsCapture(string fileName) =>
        Path.GetFileName(fileName).StartsWith("Snap_", StringComparison.OrdinalIgnoreCase)
        && Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public static bool IsVideo(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() is ".mp4" or ".webm";

    /// <summary>All capture files under the folder (the folder itself and two levels of subfolders).</summary>
    public static List<string> Scan(string root)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        void Walk(string dir, int depth)
        {
            try
            {
                found.AddRange(Directory.EnumerateFiles(dir).Where(IsCapture));
                if (depth < 2)
                    foreach (var sub in Directory.EnumerateDirectories(dir)) Walk(sub, depth + 1);
            }
            catch (Exception e) { Log.Error("scan " + dir, e); }
        }
        Walk(root, 0);
        return found;
    }

    /// <summary>
    /// Where captures lying directly in <paramref name="root"/> (as 2.0 stored them) belong under the subfolder
    /// scheme: pairs of (current path, new path). Pure: the caller moves the files.
    /// </summary>
    public static List<(string From, string To)> PlanTidy(IEnumerable<string> rootFiles, string root, SubfolderScheme scheme, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var plan = new List<(string, string)>();
        if (scheme == SubfolderScheme.None) return plan;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in rootFiles)
        {
            if (!IsCapture(f) || !string.Equals(Path.GetDirectoryName(f)?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            var when = DateFromName(f);
            if (when == null) continue;
            var folder = Folder(root, when.Value, scheme);
            var name = FileNames.Unique(folder, Path.GetFileName(f), p => exists(p) || taken.Contains(p));
            var to = Path.Combine(folder, name);
            taken.Add(to);
            plan.Add((f, to));
        }
        return plan;
    }
}
