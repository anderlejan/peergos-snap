using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeergosSnap.Core;

/// <summary>One capture in the history: where it is on this PC and in Peergos, its link, label and origin.</summary>
public sealed class HistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime Created { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "picture";
    /// <summary>The copy in the captures folder.</summary>
    public string? File { get; set; }
    public string? MirrorFile { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Seconds { get; set; }
    public long Bytes { get; set; }
    public string? App { get; set; }
    public string? WindowTitle { get; set; }
    public string Label { get; set; } = "";
    public string? Link { get; set; }
    public string? PeergosPath { get; set; }
    public DateTime? Uploaded { get; set; }

    [JsonIgnore] public bool IsVideo => Kind == "video";
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(File ?? PeergosPath ?? "");
    [JsonIgnore] public string Title => Label.Trim().Length > 0 ? Label.Trim() : Name;
}

/// <summary>A file found in the Peergos capture folder.</summary>
public sealed record RemoteFile(string Name, string Path, long Size, DateTime Modified, IReadOnlyList<string> Links);

/// <summary>The history file: history.json with rotating backups.</summary>
public sealed class HistoryStore
{
    readonly string file;
    List<HistoryRecord> records = [];
    /// <summary>Files (local paths and Peergos paths) the user removed from the history: scans do not add them again.</summary>
    HashSet<string> dismissed = new(StringComparer.OrdinalIgnoreCase);
    public event Action? Changed;

    sealed class FileData
    {
        public int Version { get; set; } = 1;
        public List<HistoryRecord> Records { get; set; } = [];
        public List<string> Dismissed { get; set; } = [];
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    HistoryStore(string file) => this.file = file;

    public IReadOnlyList<HistoryRecord> Records => records;
    public IReadOnlySet<string> Dismissed => dismissed;

    public static HistoryStore Load(string file)
    {
        var s = new HistoryStore(file);
        try
        {
            if (System.IO.File.Exists(file))
            {
                Backup(file, 10);
                var data = JsonSerializer.Deserialize<FileData>(System.IO.File.ReadAllText(file), Json) ?? new FileData();
                s.records = data.Records;
                s.dismissed = new HashSet<string>(data.Dismissed, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e)
        {
            Log.Error("history load", e);
            try { System.IO.File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        }
        return s;
    }

    static void Backup(string file, int keep)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file)!, "backups");
            Directory.CreateDirectory(dir);
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            System.IO.File.Copy(file, System.IO.Path.Combine(dir, $"{name}-{DateTime.Now:yyyy-MM-dd_HHmmss}.json"), true);
            var old = Directory.GetFiles(dir, name + "-*.json").OrderBy(f => f).ToList();
            while (old.Count > keep) { System.IO.File.Delete(old[0]); old.RemoveAt(0); }
        }
        catch (Exception e) { Log.Error("history backup", e); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            var data = new FileData { Records = records, Dismissed = dismissed.ToList() };
            System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
            System.IO.File.Move(tmp, file, true);
        }
        catch (Exception e) { Log.Error("history save", e); }
        Changed?.Invoke();
    }

    public HistoryRecord Add(HistoryRecord r)
    {
        records.Add(r);
        Save();
        return r;
    }

    public void AddRange(IEnumerable<HistoryRecord> rs)
    {
        records.AddRange(rs);
        Save();
    }

    /// <summary>Removes records (not their files); the files are not added back by later scans.</summary>
    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var r in records.Where(r => set.Contains(r.Id))) Dismiss(r);
        records.RemoveAll(r => set.Contains(r.Id));
        Save();
    }

    /// <summary>Empties the history (the files stay where they are).</summary>
    public void Clear()
    {
        foreach (var r in records) Dismiss(r);
        records.Clear();
        Save();
    }

    void Dismiss(HistoryRecord r)
    {
        if (!string.IsNullOrEmpty(r.File)) dismissed.Add(r.File);
        if (!string.IsNullOrEmpty(r.PeergosPath)) dismissed.Add(r.PeergosPath);
    }

    public HistoryRecord? Find(string id) => records.FirstOrDefault(r => r.Id == id);
}

/// <summary>Pure logic of the history manager (unit tested).</summary>
public static class HistoryLogic
{
    /// <summary>
    /// Records for capture files that are not in the history yet: local files (from before the history existed or
    /// copied in) and files in the Peergos folder. A Peergos file with the same name as a local capture joins it.
    /// Also fills in the Peergos path and a link for known records that lack them.
    /// </summary>
    public static List<HistoryRecord> Discover(IReadOnlyList<HistoryRecord> records, IEnumerable<string> localFiles,
        IReadOnlyList<RemoteFile>? remote, Func<string, DateTime> fileTime, IReadOnlySet<string>? dismissed = null)
    {
        var added = new List<HistoryRecord>();
        var byFile = new HashSet<string>(records.Where(r => r.File != null).Select(r => r.File!), StringComparer.OrdinalIgnoreCase);
        foreach (var f in localFiles)
        {
            if (byFile.Contains(f) || dismissed?.Contains(f) == true) continue;
            byFile.Add(f);
            added.Add(new HistoryRecord
            {
                Created = CaptureFiles.DateFromName(f) ?? fileTime(f),
                Kind = CaptureFiles.IsVideo(f) ? "video" : "picture",
                File = f,
            });
        }
        if (remote == null) return added;
        var all = records.Concat(added).ToList();
        var byRemote = new HashSet<string>(all.Where(r => r.PeergosPath != null).Select(r => r.PeergosPath!), StringComparer.Ordinal);
        foreach (var rf in remote)
        {
            var known = all.FirstOrDefault(r => r.PeergosPath == rf.Path);
            if (known != null)
            {
                if (string.IsNullOrEmpty(known.Link) && rf.Links.Count > 0) known.Link = rf.Links[0];
                continue;
            }
            if (byRemote.Contains(rf.Path) || dismissed?.Contains(rf.Path) == true) continue;
            // A local capture with the same name that was never linked to Peergos: the same capture.
            var twin = all.FirstOrDefault(r => r.PeergosPath == null && r.File != null
                                               && string.Equals(System.IO.Path.GetFileName(r.File), rf.Name, StringComparison.OrdinalIgnoreCase));
            if (twin != null)
            {
                twin.PeergosPath = rf.Path;
                if (string.IsNullOrEmpty(twin.Link) && rf.Links.Count > 0) twin.Link = rf.Links[0];
                byRemote.Add(rf.Path);
                continue;
            }
            var r = new HistoryRecord
            {
                Created = CaptureFiles.DateFromName(rf.Name) ?? rf.Modified.ToLocalTime(),
                Kind = CaptureFiles.IsVideo(rf.Name) ? "video" : "picture",
                PeergosPath = rf.Path,
                Link = rf.Links.Count > 0 ? rf.Links[0] : null,
                Bytes = rf.Size,
            };
            added.Add(r);
            all.Add(r);
            byRemote.Add(rf.Path);
        }
        return added;
    }

    /// <summary>Is the file in Peergos? null = unknown (not signed in, not looked yet).</summary>
    public static bool? InPeergos(HistoryRecord r, IReadOnlyCollection<string>? remotePaths) =>
        r.PeergosPath == null ? false : remotePaths == null ? null : remotePaths.Contains(r.PeergosPath);

    /// <summary>Records that no longer lead to any file: not on this PC and not (or no longer) in Peergos.</summary>
    public static List<HistoryRecord> Gone(IEnumerable<HistoryRecord> records, Func<string, bool> localExists, IReadOnlyCollection<string>? remotePaths) =>
        records.Where(r => (r.File == null || !localExists(r.File)) && InPeergos(r, remotePaths) != true
                           && !(remotePaths == null && r.PeergosPath != null)).ToList();

    public static IEnumerable<HistoryRecord> Filter(IEnumerable<HistoryRecord> records, string show, string search,
        Func<string, bool> localExists, IReadOnlyCollection<string>? remotePaths)
    {
        var words = search.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var r in records)
        {
            bool local = r.File != null && localExists(r.File);
            bool? peer = InPeergos(r, remotePaths);
            bool keep = show switch
            {
                "pictures" => !r.IsVideo,
                "videos" => r.IsVideo,
                "labelled" => r.Label.Trim().Length > 0,
                "pc" => local,
                "peergos" => peer == true || (peer == null && r.PeergosPath != null),
                "pc-only" => local && peer == false,
                "peergos-only" => !local && (peer == true || (peer == null && r.PeergosPath != null)),
                "missing" => !local,
                _ => true,
            };
            if (!keep) continue;
            if (words.Length > 0)
            {
                var hay = $"{r.Label} {r.Name} {r.App} {r.WindowTitle} {r.Created:yyyy-MM-dd} {r.Link}".ToLowerInvariant();
                if (!words.All(hay.Contains)) continue;
            }
            yield return r;
        }
    }

    public static IEnumerable<HistoryRecord> Sort(IEnumerable<HistoryRecord> records, string by) => by switch
    {
        "oldest" => records.OrderBy(r => r.Created),
        "name" => records.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Created),
        "size" => records.OrderByDescending(r => r.Bytes).ThenByDescending(r => r.Created),
        "app" => records.OrderBy(r => string.IsNullOrEmpty(r.App) ? "￿" : r.App, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(r => r.Created),
        _ => records.OrderByDescending(r => r.Created),
    };

    /// <summary>"Today", "Yesterday", "Friday 25 September" or "25 September 2025" – the group heading of a date.</summary>
    public static string DayLabel(DateTime d, DateTime today)
    {
        var days = (today.Date - d.Date).TotalDays;
        if (days == 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days > 1 && days < 7) return d.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture);
        return d.Year == today.Year
            ? d.ToString("d MMMM", System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB",
    };
}
