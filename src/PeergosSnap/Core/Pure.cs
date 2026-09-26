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
            "-hide_banner", "-loglevel", "warning", "-y",
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

public sealed record BridgeResult(bool Ok, string? Link, string? PeergosPath, string? Error)
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
            return new(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, S("link"), S("path"), S("error"));
        }
        catch (JsonException) { return new(false, null, null, "The uploader answer could not be read"); }
    }
}

public static class LinkCheck
{
    /// <summary>A writable-folder secret link looks like https://host/secret/&lt;owner&gt;/&lt;id&gt;#&lt;key&gt;.</summary>
    public static bool LooksLikeSecretLink(string? link, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(link)) { problem = "Paste the folder's secret link"; return false; }
        link = link.Trim();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
        { problem = "Not a web link"; return false; }
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 3 || parts[0] != "secret" || !long.TryParse(parts[2], out _))
        { problem = "Expected …/secret/<owner>/<number>#<key>"; return false; }
        if (u.Fragment.Length < 2) { problem = "The part after # (the key) is missing"; return false; }
        return true;
    }

    /// <summary>The server the link lives on, e.g. https://peergos.net.</summary>
    public static string? ServerOf(string link) =>
        Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null;
}
