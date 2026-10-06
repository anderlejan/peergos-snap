using Microsoft.Win32;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Peergos Snap in Explorer's right-click menu (Settings → General), for this Windows user only
/// (HKCU\Software\Classes). Files and folders get a "Peergos Snap" submenu: upload and copy the link, or send to a
/// friend; the app asks before either happens. Windows 11 lists such menus under "Show more options".
/// </summary>
public static class ExplorerMenu
{
    const string Verb = "PeergosSnap";
    const string FilesKey = @"Software\Classes\*\shell\" + Verb;
    const string FoldersKey = @"Software\Classes\Directory\shell\" + Verb;

    /// <summary>On: the entries start <paramref name="exe"/> (written again when they start another copy). Off: removed –
    /// but only when they are this copy's, so a test copy with the option off leaves the installed app's entries alone.</summary>
    public static void Sync(bool on, string exe)
    {
        var registered = Registered();
        bool mine = string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase);
        if (on && !mine)
        {
            Remove();
            Write(FilesKey, exe, "Upload to Peergos and copy the link", "Send to a friend…");
            Write(FoldersKey, exe, "Upload the folder to Peergos and copy the link", "Send the folder to a friend…");
            Log.Info("explorer menu on: " + exe);
        }
        else if (!on && mine)
        {
            Remove();
            Log.Info("explorer menu off");
        }
    }

    /// <summary>The program the entries start, or null when there are none.</summary>
    public static string? Registered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(FilesKey + @"\shell\1upload\command");
        var cmd = k?.GetValue("") as string;
        if (string.IsNullOrEmpty(cmd) || !cmd.StartsWith('"')) return null;
        var end = cmd.IndexOf('"', 1);
        return end > 1 ? cmd[1..end] : null;
    }

    static void Write(string key, string exe, string upload, string send)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue("MUIVerb", "Peergos Snap");
        k.SetValue("Icon", exe + ",0");
        k.SetValue("SubCommands", ""); // the entries below are its submenu
        Sub(k, "1upload", upload, $"\"{exe}\" {ExplorerCommand.Upload} \"%1\"");
        Sub(k, "2send", send, $"\"{exe}\" {ExplorerCommand.Send} \"%1\"");
    }

    static void Sub(RegistryKey parent, string name, string text, string command)
    {
        using var s = parent.CreateSubKey(@"shell\" + name);
        s.SetValue("MUIVerb", text);
        using var c = s.CreateSubKey("command");
        c.SetValue("", command);
    }

    public static void Remove()
    {
        foreach (var k in new[] { FilesKey, FoldersKey })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(k, false); }
            catch (Exception e) { Log.Error("explorer menu: remove " + k, e); }
        }
    }
}
