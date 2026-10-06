using System.IO.Compression;

namespace PeergosSnap.Core;

/// <summary>What kind of file a name is: for the direct window's preview, its icons and the words "PNG picture".</summary>
public enum FileKind { Picture, Video, Audio, Pdf, Archive, Text, Document, Other }

public static class FileKinds
{
    static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];
    static readonly string[] Videos = [".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv"];
    static readonly string[] Sounds = [".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma"];
    static readonly string[] Texts = [".txt", ".csv", ".md", ".log", ".json", ".xml"];
    static readonly string[] Documents = [".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf"];

    public static FileKind Of(string name)
    {
        var e = Path.GetExtension(name ?? "").ToLowerInvariant();
        return Pictures.Contains(e) ? FileKind.Picture
            : Videos.Contains(e) ? FileKind.Video
            : Sounds.Contains(e) ? FileKind.Audio
            : e == ".pdf" ? FileKind.Pdf
            : e == ".zip" ? FileKind.Archive
            : Texts.Contains(e) ? FileKind.Text
            : Documents.Contains(e) ? FileKind.Document
            : FileKind.Other;
    }

    /// <summary>"PNG picture", "MP4 video", "ZIP archive" … – what the file is, in plain words.</summary>
    public static string Describe(string name)
    {
        var e = Path.GetExtension(name ?? "").TrimStart('.').ToUpperInvariant();
        return Of(name ?? "") switch
        {
            FileKind.Picture => (e == "JPEG" ? "JPG" : e) + " picture",
            FileKind.Video => e + " video",
            FileKind.Audio => e + " sound",
            FileKind.Pdf => "PDF document",
            FileKind.Archive => "ZIP archive (a packed folder)",
            FileKind.Text => e == "CSV" ? "CSV table" : e + " text",
            FileKind.Document => e + " document",
            _ => e.Length > 0 ? e + " file" : "File",
        };
    }

    /// <summary>The Segoe Fluent / MDL2 icon of a kind.</summary>
    public static string Glyph(FileKind k) => k switch
    {
        FileKind.Picture => "\uEB9F",
        FileKind.Video => "\uE714",
        FileKind.Audio => "\uE8D6",
        FileKind.Archive => "\uF012",
        FileKind.Pdf or FileKind.Document or FileKind.Text => "\uE8A5",
        _ => "\uE7C3",
    };
}

/// <summary>A video's length and picture size, as FFmpeg describes the file (unknown: null and 0).</summary>
public sealed record VideoFacts(TimeSpan? Duration, int Width, int Height)
{
    public static VideoFacts Parse(string ffmpegText)
    {
        var t = ffmpegText ?? "";
        TimeSpan? d = null;
        var dm = System.Text.RegularExpressions.Regex.Match(t, @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)");
        if (dm.Success)
            d = TimeSpan.FromHours(int.Parse(dm.Groups[1].Value)) + TimeSpan.FromMinutes(int.Parse(dm.Groups[2].Value))
                + TimeSpan.FromSeconds(double.Parse(dm.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        var sm = System.Text.RegularExpressions.Regex.Match(t, @"Stream #[^\n]*Video:[^\n]*?\s(\d{2,5})x(\d{2,5})[\s,\[]");
        return sm.Success ? new VideoFacts(d, int.Parse(sm.Groups[1].Value), int.Parse(sm.Groups[2].Value)) : new VideoFacts(d, 0, 0);
    }

    /// <summary>"0:05", "12:03", "1:02:03".</summary>
    public static string Length(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A folder travels to a friend as one ZIP file: its files and subfolders, without the folder itself, named like the
/// folder. The friend unpacks it into a new folder of that name; unpacking never writes outside that folder.
/// </summary>
public static class FolderPack
{
    public static string ZipName(string folder)
    {
        var n = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(n) || n.EndsWith(':')) n = "Folder"; // a drive (C:\) has no name of its own
        return n + ".zip";
    }

    /// <summary>Packs the folder into <paramref name="targetDir"/> (hidden and system files stay out, as for uploads;
    /// links to other folders are not followed). Returns the ZIP file.</summary>
    public static string Pack(string folder, string targetDir, Action<int>? percent = null)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        var files = UploadLogic.FolderFiles(root);
        Directory.CreateDirectory(targetDir);
        var zip = Path.Combine(targetDir, FileNames.Unique(targetDir, ZipName(root)));
        long total = Math.Max(1, files.Sum(Length)), done = 0;
        var part = zip + ".part";
        try
        {
            using (var fs = File.Create(part))
            using (var a = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    a.CreateEntryFromFile(f, Path.GetRelativePath(root, f).Replace('\\', '/'), CompressionLevel.Fastest);
                    done += Length(f);
                    percent?.Invoke((int)(done * 100 / total));
                }
                // Empty subfolders go too, so the friend gets the same folders.
                var dirs = new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
                };
                foreach (var d in Directory.EnumerateDirectories(root, "*", dirs))
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        a.CreateEntry(Path.GetRelativePath(root, d).Replace('\\', '/') + "/");
            }
            File.Move(part, zip, true);
            return zip;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    static long Length(string f)
    {
        try { return new FileInfo(f).Length; }
        catch { return 0; }
    }

    /// <summary>What a ZIP file holds: files, folders, the size unpacked and the names at its top (folders end in '/').</summary>
    public sealed record Contents(int Files, int Folders, long Bytes, IReadOnlyList<string> Top);

    public static Contents Read(string zip)
    {
        using var a = ZipFile.OpenRead(zip);
        var names = a.Entries.Select(e => e.FullName.Replace('\\', '/').TrimStart('/')).Where(n => n.Length > 0).ToList();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var parts = n.TrimEnd('/').Split('/');
            int dirs = n.EndsWith('/') ? parts.Length : parts.Length - 1;
            for (int i = 1; i <= dirs; i++) folders.Add(string.Join('/', parts.Take(i)));
        }
        var top = names.Select(n => n.Contains('/') ? n[..(n.IndexOf('/') + 1)] : n)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => !n.EndsWith('/')).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var files = a.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        return new Contents(files.Count, folders.Count, files.Sum(e => e.Length), top);
    }

    /// <summary>Unpacks into a new folder in <paramref name="parent"/>, named like the ZIP file ("Photos.zip" →
    /// "Photos", "Photos (2)" when that exists) or <paramref name="name"/>. Returns the folder.</summary>
    public static string Unpack(string zip, string parent, string? name = null)
    {
        name = Path.GetFileNameWithoutExtension(name ?? zip).Trim();
        if (name.Length == 0) name = "Folder";
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, FileNames.Unique(parent, name, p => Directory.Exists(p) || File.Exists(p)));
        // .NET refuses entries that would land outside the target (a "../" in a name).
        ZipFile.ExtractToDirectory(zip, target);
        return target;
    }
}

/// <summary>
/// Explorer's right-click menu (Settings → General). Explorer starts PeergosSnap.exe once for each selected item
/// with a command and the item's path; the running copy gets "command|path" on one line ('|' never occurs in a Windows
/// path), gathers what arrives within a moment, and asks before anything is uploaded or sent.
/// </summary>
public static class ExplorerCommand
{
    public const string Upload = "--explorer-upload", Send = "--explorer-send";

    public static bool Is(string? cmd) => cmd is Upload or Send;

    /// <summary>The line for the running copy from a command line ("--explorer-send", path), or null.</summary>
    public static string? FromArgs(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
            if (Is(args[i]) && args[i + 1].Trim().Length > 0 && !args[i + 1].Contains('|'))
                return args[i] + "|" + args[i + 1].Trim();
        return null;
    }

    public static (string Command, string Path)? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var i = line.IndexOf('|');
        if (i <= 0) return null;
        var cmd = line[..i];
        var path = line[(i + 1)..].Trim().Trim('"');
        return Is(cmd) && path.Length > 0 ? (cmd, path) : null;
    }
}
