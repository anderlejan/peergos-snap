using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeergosSnap.Core;

public enum CaptureKind { Picture, Video }
public enum OutputMode { SecretLink, DirectMedia }
public enum StorageMode { SharedFolder, Account }

/// <summary>All user settings. Saved as JSON; secrets are protected with Windows DPAPI (current user).</summary>
public sealed class Settings
{
    public const string DefaultServer = "https://peergos.net";

    // Peergos
    public StorageMode Storage { get; set; } = StorageMode.SharedFolder;
    public string Server { get; set; } = DefaultServer;
    public string FolderLinkProtected { get; set; } = "";
    public string FolderLinkPasswordProtected { get; set; } = "";
    public string Username { get; set; } = "";
    public string AccountPasswordProtected { get; set; } = "";
    public string AccountFolder { get; set; } = "PeergosSnap";

    // Capture
    public CaptureKind DefaultKind { get; set; } = CaptureKind.Picture;
    public string ImageFormat { get; set; } = "png";
    public string VideoFormat { get; set; } = "mp4";
    public int FrameRate { get; set; } = 30;
    public int VideoQuality { get; set; } = 23;
    public bool RecordCursor { get; set; } = true;
    public bool AskBeforePictureUpload { get; set; }

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
    public int CacheKeepDays { get; set; } = 30;

    // Hotkeys
    public string HotkeyPicture { get; set; } = "Ctrl+Shift+1";
    public string HotkeyVideo { get; set; } = "Ctrl+Shift+2";
    public string HotkeyPause { get; set; } = "";
    public string HotkeyToggleOutput { get; set; } = "";

    // Appearance
    public string ColorScheme { get; set; } = "system";
    public int FontPercent { get; set; } = 100;

    // General
    public bool ShowUserNotes { get; set; } = true;
    /// <summary>Only for the User notes prompts, only on this PC; empty = a generic description (nothing personal).</summary>
    public string PromptSourceLocation { get; set; } = "";

    [JsonIgnore] public string FolderLink { get => Unprotect(FolderLinkProtected); set => FolderLinkProtected = Protect(value); }
    [JsonIgnore] public string FolderLinkPassword { get => Unprotect(FolderLinkPasswordProtected); set => FolderLinkPasswordProtected = Protect(value); }
    [JsonIgnore] public string AccountPassword { get => Unprotect(AccountPasswordProtected); set => AccountPasswordProtected = Protect(value); }

    [JsonIgnore]
    public bool PeergosConfigured => Storage == StorageMode.SharedFolder
        ? FolderLink.Contains("/secret/")
        : Username.Length > 0 && AccountPassword.Length > 0;

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

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Clamp()
    {
        FrameRate = Math.Clamp(FrameRate, 5, 60);
        VideoQuality = Math.Clamp(VideoQuality, 15, 40);
        OverlayDimPercent = Math.Clamp(OverlayDimPercent, 0, 70);
        CaptureDelayMs = Math.Clamp(CaptureDelayMs, 0, 30000);
        CacheKeepDays = Math.Clamp(CacheKeepDays, 0, 3650);
        FontPercent = Math.Clamp(FontPercent, 80, 160);
        if (Theme.Schemes.All(x => x.Id != ColorScheme)) ColorScheme = "system";
        PromptSourceLocation ??= "";
        ImageFormat = ImageFormat is "png" or "jpg" ? ImageFormat : "png";
        VideoFormat = VideoFormat is "mp4" or "webm" ? VideoFormat : "mp4";
        if (string.IsNullOrWhiteSpace(Server)) Server = DefaultServer;
        Server = Server.Trim().TrimEnd('/');
        AccountFolder = (AccountFolder ?? "").Trim().Trim('/', '\\');
    }
}
