namespace PeergosSnap.Core;

/// <summary>
/// What the tray menu offers in each mode (unit tested). "Direct to a friend" has one action that fits the mode – a
/// picture, a video, files or a folder – for the chosen friend; with several friends and none chosen the action is a
/// submenu of friends. The "draw first" switch belongs to pictures.
/// </summary>
public static class TrayMenuLogic
{
    /// <summary>The words of a mode's direct action, before and after the friend's name.</summary>
    public static (string Verb, string After) DirectAction(TrayMode mode) => mode switch
    {
        TrayMode.Video => ("Record a video for", ""),
        TrayMode.Files => ("Send files to", "…"),
        TrayMode.Folders => ("Send a folder to", "…"),
        _ => ("Take a picture for", ""),
    };

    /// <summary>"Take a picture for example-friend (draw first)", "Record a video for example-friend", "Send files to example-friend…".</summary>
    public static string DirectEntry(TrayMode mode, string friend, bool drawFirst)
    {
        var (verb, after) = DirectAction(mode);
        return $"{verb} {friend}{after}" + (mode == TrayMode.Picture && drawFirst ? " (draw first)" : "");
    }

    /// <summary>Sending files or a folder works during a recording; a new picture or video does not.</summary>
    public static bool DirectAllowedWhileRecording(TrayMode mode) => mode is TrayMode.Files or TrayMode.Folders;

    /// <summary>The "Draw on pictures before sending them" switch: in Pictures, once there is a friend.</summary>
    public static bool ShowsDrawSwitch(TrayMode mode, int friends) => mode == TrayMode.Picture && friends > 0;

    /// <summary>The Delay submenu: only where a capture waits for it (pictures and videos).</summary>
    public static bool ShowsDelay(TrayMode mode) => mode is TrayMode.Picture or TrayMode.Video;

    /// <summary>What a left click on the icon does, for its tooltip.</summary>
    public static string Click(TrayMode mode) => mode switch
    {
        TrayMode.Video => "record video",
        TrayMode.Files => "upload files",
        TrayMode.Folders => "upload a folder",
        _ => "take picture",
    };

    /// <summary>The tooltip while something a friend sent waits (Windows shows 127 characters at most).</summary>
    public static string ArrivedTip(string friend, string what, DateTime at) =>
        Fit($"Peergos Snap – {friend} sent {what} at {at:HH:mm} – click to see");

    public static string Fit(string tip) => tip.Length > 127 ? tip[..127] : tip;
}
