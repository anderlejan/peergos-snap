using System.Text.Json;
using PeergosSnap.Core;
using Xunit;

namespace PeergosSnap.Tests;

public class TrayMenuLogicTests
{
    [Theory]
    [InlineData(TrayMode.Picture, false, "Take a picture for example-friend")]
    [InlineData(TrayMode.Picture, true, "Take a picture for example-friend (draw first)")]
    [InlineData(TrayMode.Video, true, "Record a video for example-friend")]       // drawing belongs to pictures
    [InlineData(TrayMode.Files, true, "Send files to example-friend…")]
    [InlineData(TrayMode.Folders, false, "Send a folder to example-friend…")]
    public void Each_mode_has_its_own_direct_action(TrayMode mode, bool drawFirst, string expected) =>
        Assert.Equal(expected, TrayMenuLogic.DirectEntry(mode, "example-friend", drawFirst));

    [Fact]
    public void Only_pictures_have_the_draw_switch_and_only_captures_the_delay()
    {
        Assert.True(TrayMenuLogic.ShowsDrawSwitch(TrayMode.Picture, 1));
        Assert.False(TrayMenuLogic.ShowsDrawSwitch(TrayMode.Picture, 0));
        foreach (var m in new[] { TrayMode.Video, TrayMode.Files, TrayMode.Folders })
            Assert.False(TrayMenuLogic.ShowsDrawSwitch(m, 3));
        Assert.True(TrayMenuLogic.ShowsDelay(TrayMode.Picture));
        Assert.True(TrayMenuLogic.ShowsDelay(TrayMode.Video));
        Assert.False(TrayMenuLogic.ShowsDelay(TrayMode.Files));
        Assert.False(TrayMenuLogic.ShowsDelay(TrayMode.Folders));
    }

    [Fact]
    public void Sending_files_works_while_recording_a_new_capture_does_not()
    {
        Assert.True(TrayMenuLogic.DirectAllowedWhileRecording(TrayMode.Files));
        Assert.True(TrayMenuLogic.DirectAllowedWhileRecording(TrayMode.Folders));
        Assert.False(TrayMenuLogic.DirectAllowedWhileRecording(TrayMode.Picture));
        Assert.False(TrayMenuLogic.DirectAllowedWhileRecording(TrayMode.Video));
    }

    [Fact]
    public void Every_mode_says_what_a_click_does() =>
        Assert.Equal(["take picture", "record video", "upload files", "upload a folder"],
            Enum.GetValues<TrayMode>().Select(TrayMenuLogic.Click).ToArray());

    [Fact]
    public void The_tooltip_says_who_sent_what_and_fits_windows_limit()
    {
        var at = new DateTime(2026, 10, 6, 14, 32, 0);
        Assert.Equal("Peergos Snap – example-friend sent 2 pictures at 14:32 – click to see", TrayMenuLogic.ArrivedTip("example-friend", "2 pictures", at));
        Assert.Equal(127, TrayMenuLogic.ArrivedTip(new string('x', 200), "a file", at).Length);
    }
}

public class ChosenFriendTests
{
    [Theory]
    [InlineData("account-b", "account-b")]
    [InlineData("ACCOUNT-B", "account-b")]   // as typed in the settings file
    [InlineData("", null)]                   // several friends, none chosen: each action asks
    [InlineData("gone-friend", null)]        // no longer a friend
    public void Several_friends(string chosen, string? expected) =>
        Assert.Equal(expected, DirectLogic.ChosenFriend(["account-a", "account-b"], chosen));

    [Fact]
    public void The_only_friend_needs_no_choice()
    {
        Assert.Equal("account-a", DirectLogic.ChosenFriend(["account-a"], ""));
        Assert.Equal("account-a", DirectLogic.ChosenFriend(["account-a"], "gone-friend"));
        Assert.Null(DirectLogic.ChosenFriend([], "account-a"));
    }
}

public class ArrivalTests
{
    static DirectItem Item(string name) => new(name, "/a/PeergosSnap/Direct/b/2026-10/" + name, "a", 10, DateTime.Now, "", false, []);

    [Fact]
    public void What_arrived_in_words()
    {
        Assert.Equal("a picture", DirectLogic.What([Item("x.png")]));
        Assert.Equal("a video", DirectLogic.What([Item("x.mp4")]));
        Assert.Equal("a folder", DirectLogic.What([Item("Holiday.zip")]));
        Assert.Equal("a file", DirectLogic.What([Item("x.pdf")]));
        Assert.Equal("2 pictures", DirectLogic.What([Item("x.png"), Item("y.jpg")]));
        Assert.Equal("2 videos", DirectLogic.What([Item("x.mp4"), Item("y.webm")]));
        Assert.Equal("3 files", DirectLogic.What([Item("x.png"), Item("y.mp4"), Item("z.txt")]));
    }

    [Fact]
    public void Small_pictures_come_with_the_list_but_not_into_the_items()
    {
        var jpeg = "data:image/jpeg;base64," + Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        var text = "[{\"name\":\"a.jpg\",\"path\":\"/a/PeergosSnap/Direct/b/2026-10/a.jpg\",\"from\":\"a\",\"size\":5,\"modified\":0,\"thumb\":\"" + jpeg + "\"},"
                   + "{\"name\":\"b.txt\",\"path\":\"/a/PeergosSnap/Direct/b/2026-10/b.txt\",\"from\":\"a\",\"size\":5,\"modified\":0},"
                   + "{\"name\":\"c.png\",\"path\":\"/a/PeergosSnap/Direct/b/2026-10/c.png\",\"from\":\"a\",\"size\":5,\"modified\":0,\"thumb\":\"not a picture\"}]";
        var json = JsonDocument.Parse(text).RootElement;
        var thumbs = new Dictionary<string, string>();
        var items = DirectLogic.ParseItems(json, thumbs);
        Assert.Equal(3, items.Count);
        Assert.Single(thumbs);
        Assert.Equal(jpeg, thumbs["/a/PeergosSnap/Direct/b/2026-10/a.jpg"]);
        Assert.Equal(items.Select(i => i.Path), DirectLogic.ParseItems(json).Select(i => i.Path)); // the same items, with or without their pictures
    }

    [Theory]
    [InlineData("data:image/jpeg;base64,AQID", 3)]
    [InlineData("data:image/webp;base64,AQIDBA==", 4)]
    [InlineData("data:image/jpeg;base64,***", 0)]   // not base64
    [InlineData("data:text/plain;base64,AQID", 0)]  // not a picture
    [InlineData("", 0)]
    public void The_picture_inside_a_data_url(string url, int bytes) =>
        Assert.Equal(bytes, DirectLogic.ThumbBytes(url)?.Length ?? 0);

    [Fact]
    public void Files_not_kept_go_to_a_temporary_folder_with_the_same_names()
    {
        var item = Item("x.png");
        var kept = DirectLogic.CacheFile(AppPaths.DirectDir, "b", item);
        var view = DirectLogic.CacheFile(AppPaths.DirectViewDir, "b", item);
        Assert.Equal(Path.GetFileName(kept), Path.GetFileName(view));
        Assert.StartsWith(AppPaths.WorkDir, view); // the work folder is emptied at every start
        Assert.False(view.StartsWith(AppPaths.DirectDir, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_zip_in_memory_reads_like_one_on_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "psnap-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "Sub"));
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "hello");
            File.WriteAllText(Path.Combine(root, "src", "Sub", "b.txt"), "world");
            var zip = FolderPack.Pack(Path.Combine(root, "src"), Path.Combine(root, "out"), null);
            var fromDisk = FolderPack.Read(zip);
            var fromMemory = FolderPack.Read(new MemoryStream(File.ReadAllBytes(zip)));
            Assert.Equal(fromDisk.Files, fromMemory.Files);
            Assert.Equal(fromDisk.Folders, fromMemory.Folders);
            Assert.Equal(fromDisk.Bytes, fromMemory.Bytes);
            Assert.Equal(fromDisk.Top, fromMemory.Top);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class PictureCheckTests
{
    [Fact]
    public void A_file_that_only_looks_like_a_picture_is_sent_as_it_is_instead_of_opened_for_drawing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psnap-test-" + Guid.NewGuid().ToString("N"));
        bool fakeOpens = true, pngOpens = false;
        try
        {
            Directory.CreateDirectory(dir);
            var fake = Path.Combine(dir, "not really.jpg");
            File.WriteAllText(fake, "plain text with a picture's name");
            var png = Path.Combine(dir, "real.png");
            // WPF pictures want a thread of their own (STA).
            var t = new Thread(() =>
            {
                var bmp = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(png)) enc.Save(fs);
                fakeOpens = PeergosSnap.UI.AnnotateWindow.CanOpen(fake);
                pngOpens = PeergosSnap.UI.AnnotateWindow.CanOpen(png);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Assert.False(fakeOpens);
        Assert.True(pngOpens);
    }
}

public class Settings26Tests
{
    [Fact]
    public void New_settings_have_safe_defaults()
    {
        var s = new Settings();
        Assert.True(s.DirectFlash);
        Assert.Equal("", s.DirectFriend);
        Assert.True(s.ExplorerMenuTop);
        Assert.False(s.ExplorerMenu); // Explorer's menu itself stays opt-in
    }

    [Fact]
    public void The_chosen_friend_is_a_username_or_nothing()
    {
        var s = new Settings { DirectFriend = " @Account-B " };
        s.Clamp();
        Assert.Equal("account-b", s.DirectFriend);
        s.DirectFriend = "not a name!";
        s.Clamp();
        Assert.Equal("", s.DirectFriend);
    }

    [Fact]
    public void The_folders_mode_is_kept_by_name()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psnap-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "settings.json");
            new Settings { Mode = TrayMode.Folders }.Save(file);
            Assert.Contains("\"Folders\"", File.ReadAllText(file));
            Assert.Equal(TrayMode.Folders, Settings.Load(file).Mode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void The_chosen_friend_is_a_personal_detail()
    {
        var s = new Settings { DirectFriend = "example-friend", DirectFlash = false };
        var json = SettingsTransfer.Export(s, SettingsTransfer.Groups.Where(g => !g.Personal).Select(g => g.Key), "2.6.0",
            new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        Assert.DoesNotContain("example-friend", json);
        var file = SettingsTransfer.Read(json);
        Assert.False((bool)file.Values["DirectFlash"]!);
        Assert.True((bool)file.Values["ExplorerMenuTop"]!);
    }
}
