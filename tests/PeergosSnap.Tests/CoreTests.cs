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
    public void Parses_link_after_log_noise()
    {
        var r = BridgeResult.Parse("some log\n{\"ok\":true,\"link\":\"https://peergos.net/secret/z59abc/1420289907#egfWi1WsdjnV?open=true\",\"path\":\"/neo/PeergosSnap/a.png\"}\n");
        Assert.True(r.Ok);
        Assert.Equal("https://peergos.net/secret/z59abc/1420289907#egfWi1WsdjnV?open=true", r.Link);
        Assert.Equal("/neo/PeergosSnap/a.png", r.PeergosPath);
    }

    [Fact]
    public void Parses_signin_session()
    {
        var r = BridgeResult.Parse("{\"ok\":true,\"session\":\"AAA.BBB\",\"home\":\"/neo\"}");
        Assert.True(r.Ok);
        Assert.Equal("AAA.BBB", r.Session);
        Assert.Equal("/neo", r.PeergosPath);
    }

    [Fact]
    public void Parses_error_and_garbage()
    {
        Assert.Equal("boom", BridgeResult.Parse("{\"ok\":false,\"error\":\"boom\"}").Error);
        Assert.False(BridgeResult.Parse("").Ok);
        Assert.False(BridgeResult.Parse("{broken").Ok);
    }

    [Theory]
    [InlineData("https://peergos.net/secret/z59vuwzfFDomvAhtJvdhFV6NKt2Uu3WnMABssT5SZvqP61AzEtZpWxk/1420289907#egfWi1WsdjnV?open=true", true)]
    [InlineData("https://peergos.net/secret/z59abc/12#key", true)]
    [InlineData("http://localhost:8000/secret/z59abc/12#key?open=true", true)]
    // the long capability links of 1.x are not accepted any more
    [InlineData("https://peergos.net/#6MDZhRRPT4ugkJuUfcdjhvvr6ofx3T6mX3gUR7V52nTuLpuyugiYHLTrjyZc9j/6MDZhRRPT4ug/2QPajpNrK8Pph1ox/5Pf7SvStkNUjKc?open=true", false)]
    [InlineData("https://peergos.net/secret/z59abc/12", false)]
    [InlineData("https://peergos.net/secret/z59abc/notanumber#key", false)]
    [InlineData("peergos.net/secret/z/1#k", false)]
    [InlineData("", false)]
    public void Short_secret_link_shape(string link, bool ok) => Assert.Equal(ok, PeergosLinks.IsShortSecretLink(link));

    [Theory]
    [InlineData("java.net.UnknownHostException: peergos.net", "Cannot reach the Peergos server (offline?)")]
    [InlineData("Incorrect+password", "Wrong password")]
    [InlineData("Invalid+TOTP+code", "Wrong two-factor code")]
    [InlineData("Unknown username. Did you enter it correctly?", "Unknown Peergos username")]
    [InlineData("Session expired: sign in again (bad key)", "Your Peergos session has ended – sign in again (Settings → Peergos)")]
    [InlineData("Not signed in", "Not signed in to Peergos – sign in in Settings → Peergos")]
    public void Friendly_errors(string raw, string expected) => Assert.Equal(expected, PeergosSnap.Services.Uploader.Friendly(raw));

    [Fact]
    public void Session_problems_ask_to_sign_in_again()
    {
        Assert.True(PeergosSnap.Services.Uploader.NeedsSignIn(PeergosSnap.Services.Uploader.Friendly("Not signed in")));
        Assert.True(PeergosSnap.Services.Uploader.NeedsSignIn(PeergosSnap.Services.Uploader.Friendly("Session expired: x")));
        Assert.False(PeergosSnap.Services.Uploader.NeedsSignIn(PeergosSnap.Services.Uploader.Friendly("java.net.ConnectException")));
    }
}

public class UpdaterTests
{
    const string Release = """
        {"tag_name":"v2.1.0","html_url":"https://example/releases/v2.1.0","draft":false,"prerelease":false,
         "assets":[{"name":"PeergosSnap-Setup-2.1.0.exe","browser_download_url":"https://example/setup.exe"},
                   {"name":"SHA256SUMS-2.1.0.txt","browser_download_url":"https://example/sums.txt"},
                   {"name":"PeergosSnap-Source-2.1.0.zip","browser_download_url":"https://example/src.zip"}]}
        """;

    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.0.3", "2.0.3")]
    [InlineData("v3.0", "3.0.0")]
    [InlineData("v2.1.0-beta1", "2.1.0")]
    public void Parses_versions(string tag, string expected) => Assert.Equal(Version.Parse(expected), PeergosSnap.Services.Updater.ParseVersion(tag));

    [Fact]
    public void Rejects_non_versions() => Assert.Null(PeergosSnap.Services.Updater.ParseVersion("latest"));

    [Fact]
    public void Finds_newer_release_with_installer_and_checksums()
    {
        var u = PeergosSnap.Services.Updater.ParseRelease(Release, new Version(2, 0, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(2, 1, 0), u!.Version);
        Assert.Equal("PeergosSnap-Setup-2.1.0.exe", u.SetupName);
        Assert.Equal("https://example/setup.exe", u.SetupUrl);
        Assert.Equal("https://example/sums.txt", u.SumsUrl);
    }

    [Fact]
    public void Ignores_same_or_older_drafts_and_incomplete_releases()
    {
        Assert.Null(PeergosSnap.Services.Updater.ParseRelease(Release, new Version(2, 1, 0)));
        Assert.Null(PeergosSnap.Services.Updater.ParseRelease(Release, new Version(3, 0, 0)));
        Assert.Null(PeergosSnap.Services.Updater.ParseRelease(Release.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(2, 0, 0)));
        Assert.Null(PeergosSnap.Services.Updater.ParseRelease(Release.Replace("SHA256SUMS-2.1.0.txt", "other.txt"), new Version(2, 0, 0)));
    }

    [Fact]
    public void Reads_checksum_lists()
    {
        var sums = "eaad25b8a841ba20b6f615d0958e825f6f60c995912043931ad51cd4e9ef36c0  PeergosSnap-Setup-2.1.0.exe\n" +
                   "4fd5d6a0c310807d62c5a5b6b02eec219a7d52fa80bdd1261d2b87d7dc6070a9  PeergosSnap-Source-2.1.0.zip\n";
        Assert.Equal("eaad25b8a841ba20b6f615d0958e825f6f60c995912043931ad51cd4e9ef36c0", PeergosSnap.Services.Updater.ExpectedHash(sums, "PeergosSnap-Setup-2.1.0.exe"));
        Assert.Null(PeergosSnap.Services.Updater.ExpectedHash(sums, "PeergosSnap-Setup-9.9.9.exe"));
    }
}

public class ThemeTests
{
    public static IEnumerable<object[]> Schemes() => Theme.Schemes.Where(s => s.Id != "system").Select(s => new object[] { s.Id });

    static Scheme Get(string id) => Theme.Schemes.First(s => s.Id == id);

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Text_is_readable(string id)
    {
        var s = Get(id);
        double body = s.Group == "Medium" ? 6 : 7;
        Assert.True(Theme.Contrast(s.Fg, s.Bg) >= body, $"{id}: text {Theme.Contrast(s.Fg, s.Bg):0.0}");
        Assert.True(Theme.Contrast(s.Fg, s.Bg2) >= 5.5, $"{id}: text on cards");
        Assert.True(Theme.Contrast(s.Fg, s.Bg3) >= 5.5, $"{id}: text on inputs");
        Assert.True(Theme.Contrast(s.Fg2, s.Bg) >= 4.5, $"{id}: secondary text");
        Assert.True(Theme.Contrast(s.Fg3, s.Bg) >= 4.5, $"{id}: hints");
    }

    [Theory]
    [MemberData(nameof(Schemes))]
    public void Accents_are_readable(string id)
    {
        var s = Get(id);
        Assert.True(Theme.Contrast(s.Acc, s.Bg) >= 4.5, $"{id}: accent text");
        Assert.True(Theme.Contrast(s.Acc, s.Bg2) >= 4.5, $"{id}: accent text on cards");
        Assert.True(Theme.Contrast(s.OnAcc, s.Acc2) >= 4.5, $"{id}: button text");
        Assert.True(Theme.Contrast(s.Acc2, s.Bg) >= 1.6, $"{id}: buttons stand out");
    }

    [Fact]
    public void A_third_each_dark_medium_bright()
    {
        var groups = Theme.Schemes.Where(s => s.Id != "system").GroupBy(s => s.Group).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(6, groups["Dark"]);
        Assert.Equal(6, groups["Medium"]);
        Assert.Equal(6, groups["Bright"]);
        Assert.Equal(Theme.Schemes.Length, Theme.Schemes.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void Old_scheme_names_still_work()
    {
        Assert.True(Theme.Known("dark"));
        Assert.Equal("graphite", Theme.Resolve("dark").Id);
        Assert.Equal("paper", Theme.Resolve("light").Id);
        Assert.False(Theme.Known("nonsense"));
        Assert.Equal("peergos-dark", Theme.Resolve("nonsense").Id);
    }
}

public class SettingsTests
{
    [Fact]
    public void Roundtrip_keeps_values_and_protects_the_session()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        var file = Path.Combine(dir, "settings.json");
        try
        {
            var s = new Settings { Username = "neo", Session = "rootkey.secretentry", Output = OutputMode.DirectMedia, FrameRate = 24 };
            s.Save(file);
            var raw = File.ReadAllText(file);
            Assert.DoesNotContain("secretentry", raw);
            Assert.Contains("DirectMedia", raw);
            var back = Settings.Load(file);
            Assert.Equal("rootkey.secretentry", back.Session);
            Assert.Equal(24, back.FrameRate);
            Assert.True(back.PeergosConfigured);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Signed_in_needs_user_and_session()
    {
        Assert.False(new Settings().PeergosConfigured);
        Assert.False(new Settings { Username = "neo" }.PeergosConfigured);
        Assert.False(new Settings { Session = "a.b" }.PeergosConfigured);
        Assert.True(new Settings { Username = "neo", Session = "a.b" }.PeergosConfigured);
    }

    [Fact]
    public void Settings_of_version_1_load_without_losing_data()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, """
                {"Storage":"SharedFolder","Server":"https://peergos.net","FolderLinkProtected":"abc","Username":"","ColorScheme":"dark",
                 "Output":"SecretLink","RecordCursor":false}
                """);
            var s = Settings.Load(file);
            Assert.Equal("abc", s.FolderLinkProtected);
            Assert.True(s.HadFolderLink);
            Assert.False(s.PeergosConfigured);
            Assert.Equal("dark", s.ColorScheme);
            Assert.False(s.RecordCursor);
            Assert.True(s.CheckForUpdates);
            s.Save(file);
            Assert.Contains("\"FolderLinkProtected\": \"abc\"", File.ReadAllText(file));
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
        Assert.True(s.RecordCursor);
        Assert.True(s.InstallUpdatesAutomatically);
        s.FrameRate = 500; s.ImageFormat = "bmp"; s.Server = "https://x.org//"; s.CaptureDelayMs = -5; s.ColorScheme = "nope";
        s.Clamp();
        Assert.Equal(60, s.FrameRate);
        Assert.Equal("png", s.ImageFormat);
        Assert.Equal("https://x.org", s.Server);
        Assert.Equal(0, s.CaptureDelayMs);
        Assert.Equal("system", s.ColorScheme);
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

public class HelpTests
{
    static string Dir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "PeergosSnap", "help"));

    [Fact]
    public void Help_has_quick_start_and_every_settings_page()
    {
        var html = File.ReadAllText(Path.Combine(Dir, "index.html"));
        foreach (var id in new[] { "quick-start", "pictures", "pictures-delay", "videos", "videos-delay", "output", "notifications", "peergos",
                     "history", "hotkeys", "updates", "user-notes", "troubleshooting", "settings-peergos", "settings-capture", "settings-overlay",
                     "settings-output", "settings-files", "settings-hotkeys", "settings-appearance", "settings-general" })
            Assert.Contains($"id=\"{id}\"", html);
    }

    [Fact]
    public void Help_is_about_usage_only()
    {
        var text = File.ReadAllText(Path.Combine(Dir, "index.html"));
        foreach (var word in new[] { "GPL", "licen", "ffmpeg", "java", "github", "copyright", "anderle", "bridge", "WebView", "AppData" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".NET", text, StringComparison.Ordinal); // "peergos.net" is fine
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
