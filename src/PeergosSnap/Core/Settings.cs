using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeergosSnap.Core;

public enum CaptureKind { Picture, Video }
public enum OutputMode { SecretLink, DirectMedia }

/// <summary>All user settings. Saved as JSON; secrets are protected with Windows DPAPI (current user).</summary>
public sealed class Settings
{
    public const string DefaultServer = "https://peergos.net";

    // Peergos account. After signing in only the Peergos session is kept (DPAPI), never the password.
    public string Server { get; set; } = DefaultServer;
    public string Username { get; set; } = "";
    public string SessionProtected { get; set; } = "";
    public string AccountFolder { get; set; } = "PeergosSnap";
    /// <summary>Only read once to sign in automatically after an update from 1.x (which stored the password); then cleared.</summary>
    public string AccountPasswordProtected { get; set; } = "";
    /// <summary>The folder link of 1.x (sharing through a folder was removed in 2.0). Kept untouched for a possible
    /// future folder feature; not used.</summary>
    public string FolderLinkProtected { get; set; } = "";
    public string FolderLinkPasswordProtected { get; set; } = "";
    public bool FolderRemovalNoticeShown { get; set; }

    // Capture
    public CaptureKind DefaultKind { get; set; } = CaptureKind.Picture;
    public string ImageFormat { get; set; } = "png";
    public string VideoFormat { get; set; } = "mp4";
    public int FrameRate { get; set; } = 30;
    public int VideoQuality { get; set; } = 23;
    public bool RecordCursor { get; set; } = true;
    public bool AskBeforePictureUpload { get; set; }
    /// <summary>Countdown before a picture is taken (the screen is then frozen for selecting) or before a video starts. 0 = none.</summary>
    public int DelaySeconds { get; set; }

    // Overlay
    public int OverlayDimPercent { get; set; }
    public string BorderColor { get; set; } = "#FF3B30";
    public bool ShowSizeLabel { get; set; } = true;
    public bool PickWindowOnClick { get; set; } = true;
    public int CaptureDelayMs { get; set; }

    // Output
    public OutputMode Output { get; set; } = OutputMode.SecretLink;
    public bool MirrorEnabled { get; set; }
    public string MirrorFolder { get; set; } = "";
    public bool FallbackToClipboard { get; set; } = true;
    public bool Notifications { get; set; } = true;
    /// <summary>Delete local copies older than this many days; 0 = keep forever (the default since 2.1).</summary>
    public int CacheKeepDays { get; set; }
    public SubfolderScheme Subfolders { get; set; } = SubfolderScheme.Month;
    /// <summary>Set once the flat captures folder of 2.0 was sorted into subfolders.</summary>
    public bool CapturesTidied { get; set; }

    // History
    public bool RememberApp { get; set; } = true;

    // Hotkeys
    public string HotkeyPicture { get; set; } = "Ctrl+Shift+1";
    public string HotkeyVideo { get; set; } = "Ctrl+Shift+2";
    public string HotkeyPause { get; set; } = "";
    public string HotkeyToggleOutput { get; set; } = "";

    // Appearance
    public string ColorScheme { get; set; } = "system";
    public int FontPercent { get; set; } = 100;

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public bool InstallUpdatesAutomatically { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The version that ran last time, to say "updated to …" once after an update.</summary>
    public string LastRunVersion { get; set; } = "";

    // General
    public bool ShowUserNotes { get; set; } = true;
    /// <summary>Only for the User notes prompts, only on this PC; empty = a generic description (nothing personal).</summary>
    public string PromptSourceLocation { get; set; } = "";

    [JsonIgnore] public string Session { get => Unprotect(SessionProtected); set => SessionProtected = Protect(value); }
    [JsonIgnore] public string LegacyAccountPassword { get => Unprotect(AccountPasswordProtected); set => AccountPasswordProtected = Protect(value); }
    [JsonIgnore] public bool HadFolderLink => FolderLinkProtected.Length > 0;

    /// <summary>Signed in to a Peergos account (uploads and links need that).</summary>
    [JsonIgnore]
    public bool PeergosConfigured => Username.Trim().Length > 0 && SessionProtected.Length > 0 && Session.Length > 0;

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PeergosSnap.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json) ?? new Settings();
                s.Clamp();
                return s;
            }
        }
        catch
        {
            try { File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
        }
        return new Settings();
    }

    public void Save(string file)
    {
        Clamp();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
        File.Move(tmp, file, true);
    }

    /// <summary>Before 2.1 the default was to delete local copies after 30 days; a still unchanged 30 becomes "keep forever".</summary>
    public static bool IsOldKeepDaysDefault(string lastRunVersion, int keepDays)
    {
        var t = (lastRunVersion ?? "").Trim().TrimStart('v');
        var last = Version.TryParse(t.Contains('.') ? t : t + ".0", out var v) ? v : null;
        return keepDays == 30 && (last == null || last < new Version(2, 1, 0));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Clamp()
    {
        FrameRate = Math.Clamp(FrameRate, 5, 60);
        VideoQuality = Math.Clamp(VideoQuality, 15, 40);
        OverlayDimPercent = Math.Clamp(OverlayDimPercent, 0, 70);
        CaptureDelayMs = Math.Clamp(CaptureDelayMs, 0, 30000);
        CacheKeepDays = Math.Clamp(CacheKeepDays, 0, 3650);
        DelaySeconds = Math.Clamp(DelaySeconds, 0, 60);
        FontPercent = Math.Clamp(FontPercent, 80, 160);
        if (!Theme.Known(ColorScheme)) ColorScheme = "system";
        PromptSourceLocation ??= "";
        ImageFormat = ImageFormat is "png" or "jpg" ? ImageFormat : "png";
        VideoFormat = VideoFormat is "mp4" or "webm" ? VideoFormat : "mp4";
        if (string.IsNullOrWhiteSpace(Server)) Server = DefaultServer;
        Server = Server.Trim().TrimEnd('/');
        AccountFolder = (AccountFolder ?? "").Trim().Trim('/', '\\');
    }
}
