using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeergosSnap.Core;
using PeergosSnap.UI;
using Xunit;

namespace PeergosSnap.Tests;

public class ToastLogicTests
{
    [Theory]
    [InlineData(false, false, 6, 6)]
    [InlineData(false, true, 6, 14)]  // warnings and errors stay at least 14 s
    [InlineData(false, true, 30, 30)]
    [InlineData(false, false, 0, 0)]  // until closed
    [InlineData(false, true, 0, 0)]
    [InlineData(true, false, 6, 0)]   // still running: stays until it is done
    public void How_long_a_card_stays(bool busy, bool problem, int seconds, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), ToastLogic.Duration(busy, problem, seconds));

    [Fact]
    public void The_choices_name_the_default_and_until_closed()
    {
        Assert.Contains(6, ToastLogic.Choices);
        Assert.Contains(0, ToastLogic.Choices);
        Assert.Equal("6 seconds (default)", ToastLogic.Describe(6));
        Assert.Equal("10 seconds", ToastLogic.Describe(10));
        Assert.Equal("Until I close them", ToastLogic.Describe(0));
    }

    [Fact]
    public void New_settings_have_safe_defaults_and_stay_in_range()
    {
        var s = new Settings();
        Assert.Equal(6, s.ToastSeconds);
        Assert.False(s.DirectDrawFirst);
        Assert.False(s.ExplorerMenu); // Explorer's menu only when switched on
        s.ToastSeconds = 999;
        s.Clamp();
        Assert.Equal(120, s.ToastSeconds);
        s.ToastSeconds = -3;
        s.Clamp();
        Assert.Equal(0, s.ToastSeconds);
    }
}

public class FileKindTests
{
    [Theory]
    [InlineData("a.PNG", FileKind.Picture, "PNG picture")]
    [InlineData("a.jpeg", FileKind.Picture, "JPG picture")]
    [InlineData("clip.mp4", FileKind.Video, "MP4 video")]
    [InlineData("clip.webm", FileKind.Video, "WEBM video")]
    [InlineData("song.mp3", FileKind.Audio, "MP3 sound")]
    [InlineData("doc.pdf", FileKind.Pdf, "PDF document")]
    [InlineData("Holiday.zip", FileKind.Archive, "ZIP archive (a packed folder)")]
    [InlineData("t.csv", FileKind.Text, "CSV table")]
    [InlineData("n.txt", FileKind.Text, "TXT text")]
    [InlineData("r.docx", FileKind.Document, "DOCX document")]
    [InlineData("x.bin", FileKind.Other, "BIN file")]
    [InlineData("noext", FileKind.Other, "File")]
    public void Kinds_and_their_words(string name, FileKind kind, string words)
    {
        Assert.Equal(kind, FileKinds.Of(name));
        Assert.Equal(words, FileKinds.Describe(name));
        Assert.False(string.IsNullOrEmpty(FileKinds.Glyph(kind)));
    }

    [Fact]
    public void Direct_items_know_pictures_videos_and_packed_folders()
    {
        var v = new DirectItem("a.mp4", "/example-a/PeergosSnap/Direct/example-b/2026-10/a.mp4", "example-a", 1, DateTime.Now, "", false, []);
        Assert.True(v.IsVideo);
        Assert.False(v.IsImage);
        Assert.False(v.IsArchive);
        Assert.True((v with { Name = "b.zip" }).IsArchive);
        Assert.True((v with { Name = "c.png" }).IsImage);
    }
}

public class VideoFactsTests
{
    [Fact]
    public void Length_and_picture_size_from_ffmpegs_description()
    {
        const string text = "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':\n"
            + "  Duration: 00:01:05.48, start: 0.000000, bitrate: 1234 kb/s\n"
            + "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 1280x720 [SAR 1:1 DAR 16:9], 1105 kb/s, 30 fps\n"
            + "  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 96 kb/s\n";
        var f = VideoFacts.Parse(text);
        Assert.Equal(65480, f.Duration!.Value.TotalMilliseconds, 0);
        Assert.Equal((1280, 720), (f.Width, f.Height));
        Assert.Equal("1:05", VideoFacts.Length(f.Duration.Value));
        Assert.Equal("0:05", VideoFacts.Length(TimeSpan.FromSeconds(5)));
        Assert.Equal("1:02:03", VideoFacts.Length(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void Unknown_when_ffmpeg_says_nothing_useful()
    {
        var f = VideoFacts.Parse("clip.mp4: Invalid data found when processing input");
        Assert.Null(f.Duration);
        Assert.Equal(0, f.Width);
    }
}

public class FolderPackTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "peergos-snap-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_folder_travels_as_one_zip_and_unpacks_the_same()
    {
        var root = NewRoot();
        var src = Path.Combine(root, "Holiday photos");
        Directory.CreateDirectory(Path.Combine(src, "day 1"));
        Directory.CreateDirectory(Path.Combine(src, "empty"));
        File.WriteAllText(Path.Combine(src, "notes.txt"), "hello");
        File.WriteAllBytes(Path.Combine(src, "day 1", "a.bin"), [1, 2, 3]);
        var hidden = Path.Combine(src, "lock.tmp");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        try
        {
            int last = -1;
            var zip = FolderPack.Pack(src, Path.Combine(root, "out"), p => last = p);
            Assert.Equal("Holiday photos.zip", Path.GetFileName(zip));
            Assert.Equal(100, last);
            Assert.False(File.Exists(zip + ".part"));
            var c = FolderPack.Read(zip);
            Assert.Equal(2, c.Files); // the hidden file stays out, as for uploads
            Assert.Equal(8, c.Bytes);
            Assert.Equal(2, c.Folders);
            Assert.Equal(new[] { "day 1/", "empty/", "notes.txt" }, c.Top);

            var back = FolderPack.Unpack(zip, Path.Combine(root, "in"));
            Assert.Equal("Holiday photos", Path.GetFileName(back));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(back, "notes.txt")));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(back, "day 1", "a.bin")));
            Assert.True(Directory.Exists(Path.Combine(back, "empty")));
            Assert.False(File.Exists(Path.Combine(back, "lock.tmp")));
            // A second unpack never mixes into the first folder.
            Assert.Equal("Holiday photos (2)", Path.GetFileName(FolderPack.Unpack(zip, Path.Combine(root, "in"))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Unpacking_never_writes_outside_its_folder()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "odd.zip");
        try
        {
            using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(a.CreateEntry("../escaped.txt").Open()))
                w.Write("x");
            Assert.ThrowsAny<IOException>(() => FolderPack.Unpack(zip, Path.Combine(root, "in")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Folder_names_become_zip_names()
    {
        Assert.Equal("Photos.zip", FolderPack.ZipName(@"D:\Pictures\Photos\"));
        Assert.Equal("Folder.zip", FolderPack.ZipName(@"D:\"));
    }
}

public class ExplorerCommandTests
{
    [Fact]
    public void Explorer_items_travel_as_one_line_to_the_running_copy()
    {
        var line = ExplorerCommand.FromArgs(["--explorer-send", @"D:\Pictures\My photo.png"]);
        Assert.Equal(@"--explorer-send|D:\Pictures\My photo.png", line);
        Assert.Equal((ExplorerCommand.Send, @"D:\Pictures\My photo.png"), ExplorerCommand.Parse(line));
        Assert.Equal((ExplorerCommand.Upload, @"D:\Folder"), ExplorerCommand.Parse("--explorer-upload|\"D:\\Folder\""));
    }

    [Fact]
    public void Other_commands_carry_no_path()
    {
        Assert.Null(ExplorerCommand.FromArgs(["--picture"]));
        Assert.Null(ExplorerCommand.FromArgs(["--explorer-upload"])); // no path
        Assert.Null(ExplorerCommand.Parse("--picture"));
        Assert.Null(ExplorerCommand.Parse(@"--quit|D:\x"));
        Assert.Null(ExplorerCommand.Parse("--explorer-send|"));
    }

    [Fact]
    public void The_question_says_what_and_how_much()
    {
        Assert.Equal("1 file (1 KB)", ShareText.What(1, 0, 1024));
        Assert.Equal("2 files and 1 folder", ShareText.What(2, 1, 0));
        Assert.Equal("2 folders", ShareText.What(0, 2, 0));
        Assert.Equal("a.png\nb.png", ShareText.Names(["a.png", "b.png"]));
        var many = Enumerable.Range(1, 10).Select(i => $"f{i}.png").ToList();
        Assert.EndsWith("… and 3 more", ShareText.Names(many));
    }
}

public class SettingsTransferTests
{
    static readonly DateTime When = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    static IEnumerable<string> All => SettingsTransfer.Groups.Select(g => g.Key);

    [Fact]
    public void Every_setting_is_in_one_group_or_never_leaves()
    {
        // A new setting must be sorted in here: exported with a group, or never.
        var keys = JsonSerializer.SerializeToNode(new Settings(), Json)!.AsObject().Select(p => p.Key).ToList();
        var grouped = SettingsTransfer.Groups.SelectMany(g => g.Keys).ToList();
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        foreach (var k in keys) Assert.True(grouped.Contains(k) ^ SettingsTransfer.Never.Contains(k), k);
        foreach (var k in grouped.Concat(SettingsTransfer.Never)) Assert.Contains(k, keys);
        foreach (var k in grouped) Assert.NotNull(SettingsTransfer.Property(k));
    }

    [Fact]
    public void Secrets_never_and_personal_details_only_when_chosen()
    {
        var s = new Settings { Username = "example-user", MirrorFolder = @"D:\Mirror", DirectFriends = ["example-friend"], FrameRate = 60 };
        s.Session = "example-session";
        var json = SettingsTransfer.Export(s, SettingsTransfer.Groups.Where(g => !g.Personal).Select(g => g.Key), "2.5.0", When);
        Assert.DoesNotContain("example-session", json);
        Assert.DoesNotContain(s.SessionProtected, json);
        Assert.DoesNotContain("example-user", json);
        Assert.DoesNotContain("example-friend", json);
        var file = SettingsTransfer.Read(json);
        Assert.Equal(60, (int)file.Values["FrameRate"]!);
        Assert.DoesNotContain(file.Groups, g => g.Personal);
        Assert.Equal("2.5.0", file.App);

        var full = SettingsTransfer.Export(s, All, "2.5.0", When);
        Assert.Contains("example-user", full);
        Assert.DoesNotContain("SessionProtected", full);
        Assert.DoesNotContain(s.SessionProtected, full);
    }

    [Fact]
    public void Import_changes_only_the_chosen_groups_and_never_the_sign_in()
    {
        var source = new Settings { FrameRate = 60, ColorScheme = "graphite", HotkeyPicture = "Ctrl+Alt+P", Username = "example-other", DirectFriends = ["example-friend"] };
        var file = SettingsTransfer.Read(SettingsTransfer.Export(source, All, "2.5.0", When));
        var target = new Settings { FrameRate = 30, Username = "example-user" };
        target.Session = "example-session";
        var (applied, skipped) = SettingsTransfer.ApplyTo(target, file, ["capture", "hotkeys", "personal"]);
        Assert.Equal(60, target.FrameRate);
        Assert.Equal("Ctrl+Alt+P", target.HotkeyPicture);
        Assert.Equal("system", target.ColorScheme);        // Appearance was not chosen
        Assert.Equal("example-user", target.Username);     // signed in: the account stays
        Assert.Contains("Username", skipped);
        Assert.Equal(["example-friend"], target.DirectFriends);
        Assert.Equal("example-session", target.Session);
        Assert.Contains("FrameRate", applied);
    }

    [Fact]
    public void A_made_up_file_cannot_set_the_sign_in_and_unusable_values_are_left_out()
    {
        const string json = """{"format":"Peergos Snap settings","version":1,"settings":{"SessionProtected":"AAAA","FrameRate":"fast","VideoQuality":99,"ColorScheme":"graphite"}}""";
        var file = SettingsTransfer.Read(json);
        var target = new Settings();
        var (_, skipped) = SettingsTransfer.ApplyTo(target, file, All);
        Assert.Equal("", target.SessionProtected);
        Assert.Contains("FrameRate", skipped);
        Assert.Equal(30, target.FrameRate);
        Assert.Equal(40, target.VideoQuality); // corrected into range, as when settings are loaded
        Assert.Equal("graphite", target.ColorScheme);
    }

    [Fact]
    public void Other_files_are_refused()
    {
        Assert.Throws<FormatException>(() => SettingsTransfer.Read("{}"));
        Assert.Throws<FormatException>(() => SettingsTransfer.Read("not json"));
        Assert.Throws<FormatException>(() => SettingsTransfer.Read("[1,2]"));
        Assert.Throws<FormatException>(() => SettingsTransfer.Read("""{"format":"something else","settings":{}}"""));
    }
}
