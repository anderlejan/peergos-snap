using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PeergosSnap.Core;

/// <summary>A part of the settings that is exported and imported as a whole. <see cref="Keys"/> are the names in the
/// settings file.</summary>
public sealed record SettingsGroup(string Key, string Name, string Contents, string[] Keys, bool Personal = false);

/// <summary>
/// Exporting and importing settings (Settings → General): all or chosen groups, as a JSON file to keep, to help a
/// friend set up, or to show an AI. The sign-in, passwords and links are never in it; personal details (the account
/// name, friends, folders on this PC) only when that group is chosen.
/// </summary>
public static class SettingsTransfer
{
    public const string Format = "Peergos Snap settings";

    public static readonly SettingsGroup[] Groups =
    [
        new("capture", "Capture", "mode, picture and video formats, frame rate, quality, mouse pointer, sound, delay, the questions after a picture, drawing after every picture, camera sound",
            ["DefaultKind", "ImageFormat", "VideoFormat", "FrameRate", "VideoQuality", "RecordCursor", "RecordSound", "AskBeforePictureUpload", "DelaySeconds", "AnnotateAfterPicture", "ShutterSound"]),
        new("overlay", "Selection", "darkening, border colour, size label, a click takes a window, extra wait",
            ["OverlayDimPercent", "BorderColor", "ShowSizeLabel", "PickWindowOnClick", "CaptureDelayMs"]),
        new("output", "After a capture and notifications", "what happens after a capture, the clipboard fallback, notifications and how long cards stay",
            ["Output", "FallbackToClipboard", "Notifications", "ToastSeconds"]),
        new("files", "Files & history", "subfolders, copies on this PC, discarding, clean-up, the mirror folder on or off, the history's delete choices",
            ["Subfolders", "KeepLocalCopies", "DiscardPermanently", "CacheKeepDays", "MirrorEnabled", "RememberApp", "HistoryDeleteAction", "DeleteBothRemovesEntry", "ConfirmHistoryDelete"]),
        new("drawing", "Drawing", "the editor's colour and line size",
            ["AnnotateColor", "AnnotateSize"]),
        new("direct", "Direct", "receiving, showing the window, keeping received files, how often to check, drawing first, the flashing icon",
            ["DirectReceive", "DirectBringToFront", "DirectKeepOnPc", "DirectCheckSeconds", "DirectDrawFirst", "DirectFlash"]),
        new("hotkeys", "Hotkeys", "the four hotkeys",
            ["HotkeyPicture", "HotkeyVideo", "HotkeyPause", "HotkeyToggleOutput"]),
        new("appearance", "Appearance", "colour scheme and font size",
            ["ColorScheme", "FontPercent"]),
        new("general", "General", "updates, User notes in the menu, Explorer's right-click menu",
            ["CheckForUpdates", "InstallUpdatesAutomatically", "ShowUserNotes", "ExplorerMenu", "ExplorerMenuTop"]),
        new("personal", "Personal details", "your Peergos server, username and folder, your direct friends and the one chosen, the mirror folder, the source location for AI prompts – never your sign-in",
            ["Server", "Username", "AccountFolder", "DirectFriends", "DirectLastFriend", "DirectFriend", "MirrorFolder", "PromptSourceLocation"], Personal: true),
    ];

    /// <summary>Never exported or imported: the sign-in and other secrets, and what the app notes for itself.</summary>
    public static readonly string[] Never =
    [
        "SessionProtected", "AccountPasswordProtected", "FolderLinkProtected", "FolderLinkPasswordProtected",
        "FolderRemovalNoticeShown", "CapturesTidied", "LastUpdateCheck", "LastRunVersion",
    ];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>The chosen groups as a settings file.</summary>
    public static string Export(Settings s, IEnumerable<string> groups, string appVersion, DateTime utc)
    {
        var all = JsonSerializer.SerializeToNode(s, Json)!.AsObject();
        var wanted = groups.ToHashSet();
        var chosen = Groups.Where(g => wanted.Contains(g.Key)).ToList();
        var values = new JsonObject();
        foreach (var g in chosen)
            foreach (var k in g.Keys)
                if (all[k] is { } v) values[k] = v.DeepClone();
        var doc = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["app"] = appVersion,
            ["exported"] = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["groups"] = new JsonArray(chosen.Select(g => (JsonNode?)JsonValue.Create(g.Key)).ToArray()),
            ["settings"] = values,
        };
        return doc.ToJsonString(Json);
    }

    /// <summary>A settings file that was read: its values and the groups it has values for.</summary>
    public sealed record Imported(JsonObject Values, IReadOnlyList<SettingsGroup> Groups, string App);

    /// <summary>Reads a settings file. Throws <see cref="FormatException"/> for anything else.</summary>
    public static Imported Read(string json)
    {
        JsonObject? doc;
        try { doc = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { doc = null; }
        if (doc == null || doc["format"]?.GetValueKind() != JsonValueKind.String || (string?)doc["format"] != Format
            || doc["settings"] is not JsonObject values)
            throw new FormatException("This is not a settings file of Peergos Snap.");
        var groups = Groups.Where(g => g.Keys.Any(values.ContainsKey)).ToList();
        var app = doc["app"]?.GetValueKind() == JsonValueKind.String ? (string?)doc["app"] ?? "" : "";
        return new Imported(values, groups, app);
    }

    /// <summary>
    /// Puts the chosen groups of the file into <paramref name="target"/>. Everything else – and always the sign-in –
    /// stays as it was. A value that cannot be used is left out (and named); numbers out of range are corrected as
    /// when settings are loaded.
    /// </summary>
    public static (List<string> Applied, List<string> Skipped) ApplyTo(Settings target, Imported file, IEnumerable<string> groups)
    {
        var wanted = groups.ToHashSet();
        // Signed in, the account stays: a session only works with the server and username it was made for.
        bool signedIn = target.PeergosConfigured;
        var merged = JsonSerializer.SerializeToNode(target, Json)!.AsObject();
        var applied = new List<string>();
        var skipped = new List<string>();
        foreach (var g in Groups.Where(g => wanted.Contains(g.Key)))
            foreach (var k in g.Keys)
            {
                if (!file.Values.ContainsKey(k)) continue;
                if (signedIn && k is "Server" or "Username") { skipped.Add(k); continue; }
                var trial = merged.DeepClone().AsObject();
                trial[k] = file.Values[k]?.DeepClone();
                try
                {
                    if (trial.Deserialize<Settings>(Json) == null) throw new JsonException("empty");
                    merged = trial;
                    applied.Add(k);
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { skipped.Add(k); }
            }
        var result = merged.Deserialize<Settings>(Json)!;
        foreach (var k in applied)
            if (Property(k) is { } p) p.SetValue(target, p.PropertyType == typeof(List<string>) ? new List<string>((List<string>)p.GetValue(result)!) : p.GetValue(result));
        target.Clamp();
        return (applied, skipped);
    }

    /// <summary>The settings property behind a name in the file (most are the same; Mode is stored as "DefaultKind").</summary>
    public static PropertyInfo? Property(string key) =>
        typeof(Settings).GetProperties().FirstOrDefault(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null
                                                             && (p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name) == key);
}
