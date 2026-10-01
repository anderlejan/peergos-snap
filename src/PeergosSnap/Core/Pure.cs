using System.Globalization;
using System.Text.Json;

namespace PeergosSnap.Core;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public static PxRect FromPoints(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public PxRect Intersect(PxRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : new PxRect(l, t, r - l, b - t);
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

public static class Geometry
{
    /// <summary>H.264 with yuv420p needs even sizes: shrink by one pixel where needed (never below 2).</summary>
    public static PxRect EvenSize(PxRect r) =>
        new(r.X, r.Y, Math.Max(2, r.Width - r.Width % 2), Math.Max(2, r.Height - r.Height % 2));

    /// <summary>
    /// Where the recording controls go: just below the region, else just above, else to the right,
    /// else to the left, else inside the region's bottom right corner (the panel is excluded from capture).
    /// </summary>
    public static (int X, int Y, bool Inside) PlaceControls(PxRect region, PxRect screen, int w, int h, int gap = 6)
    {
        int cx = Math.Clamp(region.Right - w, screen.X, screen.Right - w);
        if (region.Bottom + gap + h <= screen.Bottom) return (cx, region.Bottom + gap, false);
        if (region.Y - gap - h >= screen.Y) return (cx, region.Y - gap - h, false);
        int cy = Math.Clamp(region.Bottom - h, screen.Y, screen.Bottom - h);
        if (region.Right + gap + w <= screen.Right) return (region.Right + gap, cy, false);
        if (region.X - gap - w >= screen.X) return (region.X - gap - w, cy, false);
        return (Math.Clamp(region.Right - w - 12, screen.X, screen.Right - w), Math.Clamp(region.Bottom - h - 12, screen.Y, screen.Bottom - h), true);
    }
}

public static class FileNames
{
    public static string ForCapture(DateTime t, string ext) =>
        $"Snap_{t:yyyy-MM-dd_HH-mm-ss}.{ext.TrimStart('.')}";

    /// <summary>Numbered name when the file exists: "a.png" -> "a (2).png".</summary>
    public static string Unique(string folder, string name, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (int i = 2; exists(Path.Combine(folder, candidate)); i++)
            candidate = $"{baseName} ({i}){ext}";
        return candidate;
    }
}

public static class FfmpegArgs
{
    /// <summary>Arguments for recording one segment of a screen region with gdigrab.</summary>
    public static List<string> Record(PxRect region, int fps, bool cursor, int quality, string format, string output)
    {
        var r = Geometry.EvenSize(region);
        var a = new List<string>
        {
            // "level+info": FFmpeg's start line ("Press [q] to stop") marks when the video begins, to align the sound.
            "-hide_banner", "-loglevel", "level+info", "-nostats", "-y",
            "-f", "gdigrab", "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", cursor ? "1" : "0",
            "-offset_x", r.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", r.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{r.Width}x{r.Height}",
            "-show_region", "0",
            "-i", "desktop",
        };
        if (format == "webm")
            a.AddRange(["-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1",
                "-crf", (quality + 8).ToString(CultureInfo.InvariantCulture), "-b:v", "0", "-pix_fmt", "yuv420p"]);
        else
            a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                "-crf", quality.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"]);
        a.Add(output);
        return a;
    }

    /// <summary>Joins recorded segments (after pauses) without re-encoding.</summary>
    public static List<string> Concat(string listFile, string format, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy" };
        if (format == "mp4") a.AddRange(["-movflags", "+faststart"]);
        a.Add(output);
        return a;
    }

    public static string ConcatList(IEnumerable<string> files) =>
        string.Join("\n", files.Select(f => "file '" + f.Replace("\\", "/").Replace("'", "'\\''") + "'")) + "\n";

    /// <summary>Faststart remux of a single segment so the MP4 plays while streaming.</summary>
    public static List<string> Remux(string input, string output) =>
        ["-hide_banner", "-loglevel", "warning", "-y", "-i", input, "-c", "copy", "-movflags", "+faststart", output];

    public static string Quote(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteOne));

    public static string QuoteOne(string s)
    {
        if (s.Length > 0 && s.IndexOfAny([' ', '\t', '"']) < 0) return s;
        var sb = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes); slashes = 0; sb.Append(c);
        }
        sb.Append('\\', slashes * 2);
        return sb.Append('"').ToString();
    }
}

/// <summary>A keyboard shortcut such as "Ctrl+Shift+1".</summary>
public readonly record struct Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public uint Modifiers => (Ctrl ? MOD_CONTROL : 0) | (Alt ? MOD_ALT : 0) | (Shift ? MOD_SHIFT : 0) | (Win ? MOD_WIN : 0);

    public static bool TryParse(string? text, out Hotkey hk)
    {
        hk = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool c = false, a = false, s = false, w = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": c = true; break;
                case "alt": a = true; break;
                case "shift": s = true; break;
                case "win": case "windows": w = true; break;
                default:
                    if (key != null) return false;
                    key = raw.Length == 1 ? raw.ToUpperInvariant() : char.ToUpperInvariant(raw[0]) + raw[1..];
                    break;
            }
        }
        if (key == null) return false;
        // A plain letter or digit without a modifier would steal normal typing.
        if (!c && !a && !w && key.Length == 1) return false;
        hk = new Hotkey(c, a, s, w, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}

public sealed record BridgeResult(bool Ok, string? Link, string? PeergosPath, string? Error, string? Session = null, string? Raw = null)
{
    public static BridgeResult Parse(string stdout)
    {
        var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line == null) return new(false, null, null, "The uploader gave no answer");
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, S("link"), S("path") ?? S("home") ?? S("folder"), S("error"), S("session"), line);
        }
        catch (JsonException) { return new(false, null, null, "The uploader answer could not be read"); }
    }
}

public static class PeergosLinks
{
    /// <summary>The files of a bridge "list" answer.</summary>
    public static List<RemoteFile> ParseList(string? raw)
    {
        var list = new List<RemoteFile>();
        if (string.IsNullOrEmpty(raw)) return list;
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return list;
        foreach (var f in files.EnumerateArray())
        {
            var links = new List<string>();
            if (f.TryGetProperty("links", out var ls) && ls.ValueKind == JsonValueKind.Array)
                links.AddRange(ls.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x))!);
            var ms = f.TryGetProperty("modified", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0;
            list.Add(new RemoteFile(
                f.GetProperty("name").GetString() ?? "",
                f.GetProperty("path").GetString() ?? "",
                f.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                links));
        }
        return list;
    }

    /// <summary>The paths a bridge "delete" answer reports as deleted (or already gone).</summary>
    public static (List<string> Deleted, List<string> Missing, List<string> Failed) ParseDelete(string? raw)
    {
        var d = new List<string>(); var m = new List<string>(); var f = new List<string>();
        if (string.IsNullOrEmpty(raw)) return (d, m, f);
        using var doc = JsonDocument.Parse(raw);
        void Read(string key, List<string> into)
        {
            if (doc.RootElement.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array)
                into.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        }
        Read("deleted", d); Read("missing", m); Read("failed", f);
        return (d, m, f);
    }

    /// <summary>
    /// A share link in the short form the Peergos web app creates: https://HOST/secret/OWNER/ID#KEY, optionally
    /// followed by ?open=true. The long capability form (https://HOST/#key/key/key/key) is not accepted.
    /// </summary>
    public static bool IsShortSecretLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != "https" && u.Scheme != "http") return false;
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 3 || parts[0] != "secret" || !parts[1].StartsWith('z') || !long.TryParse(parts[2], out _)) return false;
        var key = u.Fragment.TrimStart('#');
        var q = key.IndexOf('?');
        if (q >= 0) key = key[..q];
        return key.Length > 0 && key.All(char.IsLetterOrDigit);
    }
}
