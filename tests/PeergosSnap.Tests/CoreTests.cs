using PeergosSnap.Core;
using PeergosSnap.UI;

namespace PeergosSnap.Tests;

public class GeometryTests
{
    [Fact]
    public void FromPoints_normalizes_any_drag_direction()
    {
        Assert.Equal(new PxRect(10, 20, 30, 40), PxRect.FromPoints(40, 60, 10, 20));
        Assert.Equal(new PxRect(10, 20, 30, 40), PxRect.FromPoints(10, 20, 40, 60));
    }

    [Fact]
    public void Intersect_clips_and_empty_when_apart()
    {
        Assert.Equal(new PxRect(5, 5, 5, 5), new PxRect(0, 0, 10, 10).Intersect(new PxRect(5, 5, 20, 20)));
        Assert.True(new PxRect(0, 0, 10, 10).Intersect(new PxRect(20, 20, 5, 5)).IsEmpty);
    }

    [Fact]
    public void EvenSize_for_h264()
    {
        var r = Geometry.EvenSize(new PxRect(-1921, 3, 201, 121));
        Assert.Equal(new PxRect(-1921, 3, 200, 120), r);
        Assert.Equal(2, Geometry.EvenSize(new PxRect(0, 0, 1, 1)).Width);
    }

    [Fact]
    public void Controls_go_below_then_above_then_inside()
    {
        var screen = new PxRect(0, 0, 1920, 1080);
        var below = Geometry.PlaceControls(new PxRect(100, 100, 400, 300), screen, 200, 40);
        Assert.Equal((300, 406, false), below);
        var above = Geometry.PlaceControls(new PxRect(100, 900, 400, 170), screen, 200, 40);
        Assert.Equal(854, above.Y);
        Assert.False(above.Inside);
        var full = Geometry.PlaceControls(screen, screen, 200, 40);
        Assert.True(full.Inside);
        Assert.InRange(full.X, 0, 1920 - 200);
        Assert.InRange(full.Y, 0, 1080 - 40);
    }

    [Fact]
    public void Controls_respect_negative_monitor_coordinates()
    {
        var left = new PxRect(-2560, 0, 2560, 1440);
        var p = Geometry.PlaceControls(new PxRect(-2500, 100, 300, 200), left, 250, 40);
        Assert.InRange(p.X, -2560, -250);
        Assert.Equal(306, p.Y);
    }
}

public class FileNameTests
{
    [Fact]
    public void Capture_names_sort_by_time()
    {
        Assert.Equal("Snap_2026-09-26_21-05-09.png", FileNames.ForCapture(new DateTime(2026, 9, 26, 21, 5, 9), "png"));
        Assert.Equal("Snap_2026-09-26_21-05-09.mp4", FileNames.ForCapture(new DateTime(2026, 9, 26, 21, 5, 9), ".mp4"));
    }

    [Fact]
    public void Unique_numbers_clashes()
    {
        var taken = new HashSet<string> { @"C:\x\a.png", @"C:\x\a (2).png" };
        Assert.Equal("a (3).png", FileNames.Unique(@"C:\x", "a.png", taken.Contains));
        Assert.Equal("b.png", FileNames.Unique(@"C:\x", "b.png", taken.Contains));
    }
}

public class FfmpegArgsTests
{
    [Fact]
    public void Record_uses_region_and_even_size()
    {
        var a = FfmpegArgs.Record(new PxRect(-100, 50, 301, 199), 30, true, 23, "mp4", "out.mp4");
        string After(string k) => a[a.IndexOf(k) + 1];
        Assert.Equal("gdigrab", After("-f"));
        Assert.Equal("-100", After("-offset_x"));
        Assert.Equal("50", After("-offset_y"));
        Assert.Equal("300x198", After("-video_size"));
        Assert.Equal("libx264", After("-c:v"));
        Assert.Equal("1", After("-draw_mouse"));
        Assert.Equal("out.mp4", a[^1]);
    }

    [Fact]
    public void Webm_uses_vp9()
    {
        var a = FfmpegArgs.Record(new PxRect(0, 0, 100, 100), 15, false, 23, "webm", "o.webm");
        Assert.Contains("libvpx-vp9", a);
        Assert.Equal("0", a[a.IndexOf("-draw_mouse") + 1]);
    }

    [Fact]
    public void Concat_list_escapes_quotes()
    {
        var l = FfmpegArgs.ConcatList([@"C:\a b\seg000.mp4", @"C:\it's\seg001.mp4"]);
        Assert.Contains("file 'C:/a b/seg000.mp4'", l);
        Assert.Contains(@"file 'C:/it'\''s/seg001.mp4'", l);
    }

    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void Quote_follows_windows_rules(string input, string expected) => Assert.Equal(expected, FfmpegArgs.QuoteOne(input));
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+1", true, "Ctrl+Shift+1")]
    [InlineData("shift+ctrl+s", true, "Ctrl+Shift+S")]
    [InlineData("Alt+F9", true, "Alt+F9")]
    [InlineData("F9", true, "F9")]
    [InlineData("PrintScreen", true, "PrintScreen")]
    [InlineData("Shift+A", false, "")]
    [InlineData("A", false, "")]
    [InlineData("Ctrl+Shift", false, "")]
    [InlineData("", false, "")]
    public void Parse(string text, bool ok, string normal)
    {
        Assert.Equal(ok, Hotkey.TryParse(text, out var hk));
        if (ok) Assert.Equal(normal, hk.ToString());
    }

    [Fact]
    public void Modifier_bits()
    {
        Hotkey.TryParse("Ctrl+Alt+Shift+Win+X", out var hk);
        Assert.Equal(Hotkey.MOD_CONTROL | Hotkey.MOD_ALT | Hotkey.MOD_SHIFT | Hotkey.MOD_WIN, hk.Modifiers);
    }
}

public class BridgeAndLinkTests
{
    [Fact]
    public void Parses_success_after_log_noise()
    {
        var r = BridgeResult.Parse("some log\n{\"ok\":true,\"link\":\"https://peergos.net/#a/b/c/d?open=true\",\"path\":\"/x/y.png\"}\n");
        Assert.True(r.Ok);
        Assert.Equal("https://peergos.net/#a/b/c/d?open=true", r.Link);
        Assert.Equal("/x/y.png", r.PeergosPath);
    }

    [Fact]
    public void Parses_error_and_garbage()
    {
        Assert.Equal("boom", BridgeResult.Parse("{\"ok\":false,\"error\":\"boom\"}").Error);
        Assert.False(BridgeResult.Parse("").Ok);
        Assert.False(BridgeResult.Parse("{broken").Ok);
    }

    [Theory]
    [InlineData("https://peergos.net/secret/z59vuwzfFDorjWRiEtcEu6BQWWsLYCAJpmkAcVkuV8P5b4ykYwm1NE6/8057131#moCvfdkPxWLb", true)]
    [InlineData("http://localhost:8000/secret/z59abc/12#key", true)]
    [InlineData("https://peergos.net/secret/z59abc/12", false)]
    [InlineData("https://peergos.net/#abc/def/ghi/jkl", false)]
    [InlineData("peergos.net/secret/z/1#k", false)]
    [InlineData("", false)]
    public void Secret_link_shape(string link, bool ok) => Assert.Equal(ok, LinkCheck.LooksLikeSecretLink(link, out _));

    [Fact]
    public void Server_of_link()
    {
        Assert.Equal("https://peergos.net", LinkCheck.ServerOf("https://peergos.net/secret/z/1#k"));
        Assert.Equal("http://localhost:8000", LinkCheck.ServerOf("http://localhost:8000/secret/z/1#k"));
    }

    [Theory]
    [InlineData("java.net.UnknownHostException: peergos.net", "Cannot reach the Peergos server (offline?)")]
    [InlineData("No secret link found", "The folder link no longer exists")]
    public void Friendly_errors(string raw, string expected) => Assert.Equal(expected, PeergosSnap.Services.Uploader.Friendly(raw));
}

public class SettingsTests
{
    [Fact]
    public void Roundtrip_keeps_values_and_protects_secrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var s = new Settings { FolderLink = "https://peergos.net/secret/z/1#secretkey", FolderLinkPassword = "pw", Output = OutputMode.DirectMedia, FrameRate = 24 };
            s.Save(file);
            var raw = File.ReadAllText(file);
            Assert.DoesNotContain("secretkey", raw);
            Assert.DoesNotContain("\"pw\"", raw);
            Assert.Contains("DirectMedia", raw);
            var back = Settings.Load(file);
            Assert.Equal("https://peergos.net/secret/z/1#secretkey", back.FolderLink);
            Assert.Equal("pw", back.FolderLinkPassword);
            Assert.Equal(24, back.FrameRate);
            Assert.True(back.PeergosConfigured);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Defaults_and_clamping()
    {
        var s = new Settings();
        Assert.Equal("png", s.ImageFormat);
        Assert.Equal("mp4", s.VideoFormat);
        Assert.Equal(0, s.CaptureDelayMs);
        Assert.Equal(OutputMode.SecretLink, s.Output);
        Assert.Equal(0, s.OverlayDimPercent);
        s.FrameRate = 500; s.ImageFormat = "bmp"; s.Server = "https://x.org//"; s.CaptureDelayMs = -5;
        s.Clamp();
        Assert.Equal(60, s.FrameRate);
        Assert.Equal("png", s.ImageFormat);
        Assert.Equal("https://x.org", s.Server);
        Assert.Equal(0, s.CaptureDelayMs);
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, "{ not json");
            var s = Settings.Load(file);
            Assert.Equal(CaptureKind.Picture, s.DefaultKind);
            Assert.Single(Directory.GetFiles(dir, "settings.json.corrupt-*"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class NotesStoreTests
{
    [Fact]
    public void Save_load_and_text_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        try
        {
            var store = new NotesStore(Path.Combine(dir, "user-notes.json"));
            Assert.Empty(store.Load()["notes"]!.AsArray());
            store.Save([new System.Text.Json.Nodes.JsonObject { ["id"] = "1", ["title"] = "Fix it" }]);
            var again = new NotesStore(Path.Combine(dir, "user-notes.json")).Load();
            Assert.Equal(2, again["version"]!.GetValue<int>());
            Assert.Equal("Fix it", again["notes"]![0]!["title"]!.GetValue<string>());
            Assert.Single(Directory.GetFiles(Path.Combine(dir, "backups")));
            var f = NotesStore.SaveText(Path.Combine(dir, "prompts"), "# hi", "prompt-full!");
            Assert.StartsWith("prompt-full-", Path.GetFileName(f));
            Assert.Equal("# hi", File.ReadAllText(f));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class PrivacyTests
{
    static readonly string[] Personal = ["anderle", "Jan ", @"E:\", @"Data\AI", "regis", @"Claude\Projects", "github.com/"];

    [Fact]
    public void Prompt_config_reveals_nothing_about_the_developer()
    {
        var text = NotesWindow.Config(null).ToJsonString();
        foreach (var p in Personal) Assert.DoesNotContain(p, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prompt_config_uses_the_users_own_location_when_set()
    {
        var location = NotesWindow.Config(@"D:\my\peergos-snap")["location"]!.GetValue<string>();
        Assert.Contains(@"D:\my\peergos-snap", location);
    }

    [Fact]
    public void Shipped_notes_files_contain_nothing_personal()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "PeergosSnap"));
        var files = new[] { "usernotes/usernotes-core.js", "usernotes/usernotes-ui.js", "usernotes/usernotes.css",
                            "notes-host/index.html", "notes-host/host.js", "notes-host/host.css" };
        foreach (var f in files)
        {
            var text = File.ReadAllText(Path.Combine(dir, f));
            foreach (var p in Personal) Assert.False(text.Contains(p, StringComparison.OrdinalIgnoreCase), $"{f} contains '{p}'");
        }
    }
}
