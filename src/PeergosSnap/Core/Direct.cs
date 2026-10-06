using System.Globalization;
using System.Text.Json;

namespace PeergosSnap.Core;

/// <summary>A picture (or file) shared directly between two friends, as the bridge lists it.</summary>
public sealed record DirectItem(string Name, string Path, string From, long Size, DateTime Modified, string Label, bool Pinned,
    IReadOnlyList<string> Stars)
{
    public bool IsImage => System.IO.Path.GetExtension(Name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
    public FileKind Kind => FileKinds.Of(Name);
    public bool IsVideo => Kind == FileKind.Video;
    /// <summary>A ZIP file: how folders travel (see <see cref="FolderPack"/>).</summary>
    public bool IsArchive => Kind == FileKind.Archive;
    /// <summary>The month folder: …/MONTH/NAME or …/MONTH/received/NAME (in the folders of 2.3 and of 2.2).</summary>
    public string Month => DirectLogic.MonthOf(Path);
}

/// <summary>Who the user can share directly with, from the bridge's "friends" / "discover" answers.</summary>
public sealed record DirectFriends(IReadOnlyList<string> Friends, IReadOnlyList<string> Incoming, IReadOnlyList<string> Outgoing,
    IReadOnlyList<string> Direct);

/// <summary>Pure logic of the direct mode (unit tested).</summary>
public static class DirectLogic
{
    public static string MonthOf(DateTime t) => t.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The month folder of a direct path, counted from its end (the folders moved in 2.3, the end stays):
    /// …/MONTH/NAME or …/MONTH/received/NAME. "" when there is none.</summary>
    public static string MonthOf(string path)
    {
        var p = (path ?? "").Split('/');
        if (p.Length < 3) return "";
        var m = p.Length >= 4 && p[^2] == "received" ? p[^3] : p[^2];
        return m.Length == 7 && m[4] == '-' && m.Remove(4, 1).All(char.IsAsciiDigit) ? m : "";
    }

    /// <summary>Who the other side of a direct path is: /OWNER/…/OTHER/MONTH/[received/]NAME.</summary>
    public static string FriendOf(string path, string me)
    {
        var p = (path ?? "").Split('/');
        if (p.Length < 5) return "";
        int monthAt = p.Length >= 4 && p[^2] == "received" ? p.Length - 3 : p.Length - 2;
        var owner = p[1];
        var other = monthAt >= 1 ? p[monthAt - 1] : "";
        return string.Equals(owner, me, StringComparison.OrdinalIgnoreCase) ? other : owner;
    }
    /// <summary>A Peergos username as the bridge accepts it (lower case letters, digits, '-' and '_').</summary>
    public static string? NormaliseUser(string? text)
    {
        var n = (text ?? "").Trim().TrimStart('@').ToLowerInvariant();
        return n.Length is > 0 and <= 64 && n.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') ? n : null;
    }

    /// <summary>The bridge's items. Their small pictures (Peergos keeps one with a picture or video, since 2.6) go into
    /// <paramref name="thumbs"/> by path – not into the item, which is compared often.</summary>
    public static List<DirectItem> ParseItems(JsonElement items, IDictionary<string, string>? thumbs = null)
    {
        var list = new List<DirectItem>();
        if (items.ValueKind != JsonValueKind.Array) return list;
        foreach (var i in items.EnumerateArray())
        {
            string S(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long N(string k) => i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            var stars = i.TryGetProperty("stars", out var st) && st.ValueKind == JsonValueKind.Array
                ? st.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
            var item = new DirectItem(S("name"), S("path"), S("from"), N("size"),
                DateTimeOffset.FromUnixTimeMilliseconds(N("modified")).LocalDateTime, S("label"),
                i.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True, stars);
            list.Add(item);
            if (thumbs != null && S("thumb") is { Length: > 0 } t && ThumbBytes(t) != null) thumbs[item.Path] = t;
        }
        return list;
    }

    /// <summary>The picture inside a thumbnail "data:image/jpeg;base64,…" (also webp or png), or null.</summary>
    public static byte[]? ThumbBytes(string? dataUrl)
    {
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.Ordinal)) return null;
        int comma = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (comma < 0) return null;
        try
        {
            var b = Convert.FromBase64String(dataUrl[(comma + 8)..]);
            return b.Length > 0 ? b : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The friend the tray menu acts for: the one chosen (Other → Friend) while still a friend, else the only
    /// friend; null when there are several and none is chosen (each action then asks whom).</summary>
    public static string? ChosenFriend(IReadOnlyList<string> friends, string? chosen) =>
        friends.FirstOrDefault(f => string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase)) ?? (friends.Count == 1 ? friends[0] : null);

    /// <summary>What arrived, in words: "a picture", "a video", "a folder" (a ZIP file), "a file", "3 pictures",
    /// "2 videos", "4 files".</summary>
    public static string What(IReadOnlyList<DirectItem> items)
    {
        if (items.Count == 1)
            return items[0].IsImage ? "a picture" : items[0].IsVideo ? "a video" : items[0].IsArchive ? "a folder" : "a file";
        return $"{items.Count} " + (items.All(i => i.IsImage) ? "pictures" : items.All(i => i.IsVideo) ? "videos" : "files");
    }

    public static DirectFriends ParseFriends(JsonElement r)
    {
        List<string> L(string k) => r.TryGetProperty(k, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        return new DirectFriends(L("friends"), L("incoming"), L("outgoing"), L("direct"));
    }

    /// <summary>Pinned first, then the newest.</summary>
    public static IEnumerable<DirectItem> Sort(IEnumerable<DirectItem> items) =>
        items.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.Modified).ThenBy(i => i.Name, StringComparer.Ordinal);

    /// <summary>The friend's pictures that were not there before (what just arrived).</summary>
    public static List<DirectItem> Arrived(IEnumerable<DirectItem> items, string me, IReadOnlySet<string> seen) =>
        items.Where(i => !string.Equals(i.From, me, StringComparison.OrdinalIgnoreCase) && !seen.Contains(i.Path)).ToList();

    /// <summary>Where a direct picture is kept on this PC: root\friend\month\name (names made safe for Windows).</summary>
    public static string CacheFile(string root, string friend, DirectItem item)
    {
        var bad = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars()) { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        string Safe(string s)
        {
            var t = new string(s.Select(c => bad.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
            return t.Length == 0 || t is "." or ".." ? "_" : t;
        }
        var prefix = string.Equals(item.From, friend, StringComparison.OrdinalIgnoreCase) ? "" : "mine-";
        return System.IO.Path.Combine(root, Safe(friend), Safe(item.Month.Length > 0 ? item.Month : "other"), Safe(prefix + item.Name));
    }

    /// <summary>"userd and you", "you", "userd" – who starred a picture.</summary>
    public static string StarredBy(IReadOnlyList<string> stars, string me)
    {
        var names = stars.Select(s => string.Equals(s, me, StringComparison.OrdinalIgnoreCase) ? "you" : s).OrderBy(s => s == "you").ToList();
        return names.Count switch { 0 => "", 1 => names[0], _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1] };
    }

    /// <summary>The state of a friendship in plain words, for the friends list.</summary>
    public static string Status(string user, DirectFriends f) =>
        f.Friends.Contains(user) ? (f.Direct.Contains(user) ? "Friends – sharing with you" : "Friends")
        : f.Incoming.Contains(user) ? "Asked to be your friend"
        : f.Outgoing.Contains(user) ? "Waiting until they accept"
        : "Not friends yet";

    /// <summary>Paths seen so far: only the current and the previous month are kept, so the file stays small.</summary>
    public static HashSet<string> Prune(IEnumerable<string> seen, DateTime now)
    {
        var keep = new[] { MonthOf(now), MonthOf(now.AddMonths(-1)) };
        return seen.Where(p => keep.Any(m => p.Contains("/" + m + "/", StringComparison.Ordinal))).ToHashSet();
    }
}
