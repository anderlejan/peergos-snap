using PeergosSnap.Core;

namespace PeergosSnap.Tests;

/// <summary>2.3: locked entries, deleting while signed out, uploaded files and folders.</summary>
public class LockAndDeleteTests
{
    static HistoryRecord R(string id, string? file, string? remote, bool locked = false) =>
        new() { Id = id, File = file, PeergosPath = remote, Locked = locked };

    static bool Exists(string f) => f.StartsWith("here");

    [Fact]
    public void Locked_entries_are_left_out_of_every_delete()
    {
        var sel = new List<HistoryRecord> { R("a", "here/a.png", "/example-user/PeergosSnap/a.png"), R("b", "here/b.png", "/example-user/PeergosSnap/b.png", locked: true) };
        var plan = HistoryLogic.PlanDelete(sel, local: true, remote: true, signedIn: true, Exists, new HashSet<string> { "/example-user/PeergosSnap/a.png", "/example-user/PeergosSnap/b.png" });
        Assert.Equal(["a"], plan.Local.Select(r => r.Id));
        Assert.Equal(["a"], plan.Remote.Select(r => r.Id));
        Assert.Equal(["b"], plan.Locked.Select(r => r.Id));
        Assert.Empty(plan.NeedSignIn);
        // Even "gone everywhere" never removes a locked entry.
        Assert.Empty(HistoryLogic.RemovableAfterDelete([sel[1]], new HashSet<string> { "here/b.png" }, new HashSet<string> { "/example-user/PeergosSnap/b.png" }));
        Assert.Empty(HistoryLogic.Gone([R("c", "gone.png", null, locked: true)], _ => false, new HashSet<string>()));
    }

    [Fact]
    public void Signed_out_the_Peergos_part_is_named_not_attempted()
    {
        var sel = new List<HistoryRecord> { R("a", "here/a.png", "/example-user/PeergosSnap/a.png"), R("b", null, "/example-user/PeergosSnap/b.png") };
        // Not signed in: the Peergos state is unknown (null), so both files may still be there.
        var plan = HistoryLogic.PlanDelete(sel, local: true, remote: true, signedIn: false, Exists, remotePaths: null);
        Assert.Equal(["a"], plan.Local.Select(r => r.Id));
        Assert.Empty(plan.Remote);
        Assert.Equal(["a", "b"], plan.NeedSignIn.Select(r => r.Id));
        // Only "this PC": nothing needs Peergos.
        Assert.Empty(HistoryLogic.PlanDelete(sel, true, false, false, Exists, null).NeedSignIn);
        // Signed in and the file known to be gone from Peergos: nothing to do there.
        var gone = HistoryLogic.PlanDelete(sel, false, true, true, Exists, new HashSet<string>());
        Assert.True(gone.Nothing);
    }

    [Fact]
    public void Store_keeps_locked_entries_on_remove_and_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        try
        {
            var store = HistoryStore.Load(Path.Combine(dir, "history.json"));
            store.AddRange([R("a", "x/a.png", null), R("b", "x/b.png", null, locked: true), R("c", "x/c.png", null)]);
            store.Remove(["a", "b"]);
            Assert.Equal(["b", "c"], store.Records.Select(r => r.Id));
            store.Clear();
            Assert.Equal(["b"], store.Records.Select(r => r.Id));
            Assert.DoesNotContain("x/b.png", store.Dismissed); // a locked entry was not removed, so not dismissed either
            var again = HistoryStore.Load(Path.Combine(dir, "history.json"));
            Assert.True(again.Records.Single().Locked); // the lock is saved
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Filters_for_uploads_and_locked()
    {
        var recs = new List<HistoryRecord>
        {
            new() { Id = "p", Kind = "picture", File = "here/p.png" },
            new() { Id = "f", Kind = "file", Source = @"C:\docs\report.pdf", PeergosPath = "/example-user/PeergosSnap/report.pdf" },
            new() { Id = "d", Kind = "folder", Source = @"C:\docs\trip", PeergosPath = "/example-user/PeergosSnap/trip", Locked = true },
        };
        string[] Show(string what, string search = "") => HistoryLogic.Filter(recs, what, search, Exists, null).Select(r => r.Id).ToArray();
        Assert.Equal(["f", "d"], Show("uploads"));
        Assert.Equal(["d"], Show("locked"));
        Assert.Equal(["p"], Show("pictures")); // uploaded files are not pictures of the screen
        Assert.Equal(["d"], Show("all", "trip"));
        Assert.Equal("report.pdf", recs[1].Name);
        Assert.Equal("trip", recs[2].Name);
        Assert.True(recs[2].IsUpload && recs[2].IsFolder);
    }

    [Fact]
    public void Old_settings_ask_before_deleting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, """{"Username":"example-user","AnnotateSize":9,"AnnotateColor":"red"}""");
            var s = Settings.Load(file);
            Assert.True(s.ConfirmHistoryDelete);
            Assert.False(s.AnnotateAfterPicture);
            Assert.Equal(2, s.AnnotateSize);
            Assert.Equal("#E53935", s.AnnotateColor);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class UploadTests
{
    [Fact]
    public void Reads_the_put_answer()
    {
        var p = PeergosLinks.ParsePut("""
            {"ok":true,"files":[{"name":"a.pdf","path":"/example-user/PeergosSnap/a.pdf","size":12,"local":"C:\\x\\a.pdf","link":"https://peergos.net/secret/z1/2#k?open=true"},
            {"name":"b (2).txt","path":"/example-user/PeergosSnap/b (2).txt","size":3,"local":"C:\\x\\b.txt","link":null}],
            "folder":null,"link":null,"failed":["C:\\x\\c.bin: too large"],"error":"1 of 3 not uploaded"}
            """);
        Assert.Equal(2, p.Files.Count);
        Assert.Equal("https://peergos.net/secret/z1/2#k?open=true", p.Files[0].Link);
        Assert.Equal(@"C:\x\a.pdf", p.Files[0].Local);
        Assert.Null(p.Files[1].Link);
        Assert.Null(p.Folder);
        Assert.Single(p.Failed);

        var folder = PeergosLinks.ParsePut("""{"ok":true,"files":[],"folder":"/example-user/PeergosSnap/trip (2)","link":"https://peergos.net/secret/z1/3#k","failed":[]}""");
        Assert.Equal("/example-user/PeergosSnap/trip (2)", folder.Folder);
        Assert.Equal("https://peergos.net/secret/z1/3#k", folder.FolderLink);
    }

    [Fact]
    public void Lists_tell_folders_from_files()
    {
        var l = PeergosLinks.ParseList("""
            {"ok":true,"folder":"/example-user/PeergosSnap","files":[{"name":"Direct","path":"/example-user/PeergosSnap/Direct","size":0,"modified":0,"links":[],"dir":true},
            {"name":"Snap_2026-10-01_10-00-00.png","path":"/example-user/PeergosSnap/Snap_2026-10-01_10-00-00.png","size":5,"modified":0,"links":[]}]}
            """);
        Assert.True(l[0].Folder);
        Assert.False(l[1].Folder); // answers of 2.2 bridges have no "dir": a file
    }

    [Fact]
    public void Reads_the_folders_answer()
    {
        var (path, exists, folders) = PeergosLinks.ParseFolders("""{"ok":true,"path":"/example-user/Pictures","exists":true,"folders":["2026","Snaps"]}""");
        Assert.Equal("/example-user/Pictures", path);
        Assert.True(exists);
        Assert.Equal(["2026", "Snaps"], folders);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("PeergosSnap", "")]
    [InlineData("a/b/c", "a/b")]
    [InlineData("/a/b/", "a")]
    public void Folder_parent(string rel, string parent) => Assert.Equal(parent, PeergosFolders.Parent(rel));

    [Fact]
    public void Folder_names()
    {
        Assert.Equal("Snaps", PeergosFolders.Join("", "Snaps"));
        Assert.Equal("Pictures/Snaps", PeergosFolders.Join("/Pictures/", "Snaps"));
        Assert.Equal("Work 2026", PeergosFolders.CleanName("  Work 2026 "));
        Assert.Null(PeergosFolders.CleanName(".."));
        Assert.Null(PeergosFolders.CleanName("a/b"));
        Assert.Null(PeergosFolders.CleanName(@"a\b"));
        Assert.Null(PeergosFolders.CleanName(".hidden"));
        Assert.Null(PeergosFolders.CleanName(" "));
    }

    [Fact]
    public void Big_folders_ask_first()
    {
        Assert.False(UploadLogic.NeedsConfirm(200, 500L * 1024 * 1024));
        Assert.True(UploadLogic.NeedsConfirm(201, 1));
        Assert.True(UploadLogic.NeedsConfirm(1, 500L * 1024 * 1024 + 1));
    }
}

public class DirectPathTests
{
    [Theory]
    [InlineData("/usera/PeergosSnap/Direct/userb/2026-10/a.png", "2026-10", "userb")]
    [InlineData("/usera/PeergosSnap/Direct/userb/2026-09/received/a.png", "2026-09", "userb")]
    [InlineData("/userb/PeergosSnap/Direct/usera/2026-10/a.png", "2026-10", "userb")]
    [InlineData("/usera/PeergosSnap-Direct/userb/2026-10/a.png", "2026-10", "userb")]            // 2.2
    [InlineData("/usera/PeergosSnap-Direct/userb/2026-08/received/a.png", "2026-08", "userb")]   // 2.2
    [InlineData("/userb/PeergosSnap-Direct/usera/2026-10/a.png", "2026-10", "userb")]            // 2.2
    public void Month_and_friend_in_both_folder_layouts(string path, string month, string friend)
    {
        Assert.Equal(month, DirectLogic.MonthOf(path));
        Assert.Equal(friend, DirectLogic.FriendOf(path, "usera"));
        var item = new DirectItem("a.png", path, "x", 1, DateTime.Now, "", false, []);
        Assert.Equal(month, item.Month);
    }

    [Fact]
    public void Not_a_month()
    {
        Assert.Equal("", DirectLogic.MonthOf("/usera/PeergosSnap/a.png"));
        Assert.Equal("", DirectLogic.MonthOf(""));
        Assert.Equal("", DirectLogic.MonthOf("/userb/PeergosSnap-Direct/usera/../.."));
    }

    [Fact]
    public void Cached_files_are_the_same_for_both_layouts()
    {
        var old = new DirectItem("a.png", "/userb/PeergosSnap-Direct/usera/2026-10/a.png", "userb", 1, DateTime.Now, "", false, []);
        var now = old with { Path = "/userb/PeergosSnap/Direct/usera/2026-10/a.png" };
        Assert.Equal(DirectLogic.CacheFile("r", "userb", old), DirectLogic.CacheFile("r", "userb", now));
    }
}

public class AnnotateTests
{
    [Fact]
    public void Arrow_head_sits_at_the_tip()
    {
        var (shaft, left, right) = AnnotateLogic.ArrowHead(0, 0, 100, 0, 5);
        Assert.True(shaft.X < 100 && shaft.X > 50);
        Assert.Equal(0, shaft.Y, 6);
        Assert.Equal(left.X, right.X, 6);           // the back of the head is square to the shaft
        Assert.Equal(-left.Y, right.Y, 6);          // and symmetric
        Assert.True(left.X < shaft.X);              // the shaft ends inside the head
        var (s0, l0, r0) = AnnotateLogic.ArrowHead(5, 5, 5, 5, 5); // a click without dragging does not break
        Assert.Equal((5.0, 5.0), s0);
    }

    [Fact]
    public void Shift_makes_45_degree_lines_and_squares()
    {
        var (x, y) = AnnotateLogic.Constrain(0, 0, 100, 10, line: true);
        Assert.Equal(0, y, 6);
        Assert.Equal(Math.Sqrt(100 * 100 + 10 * 10), x, 6);
        var (dx, dy) = AnnotateLogic.Constrain(0, 0, 90, 80, line: true);
        Assert.Equal(dx, dy, 6);
        Assert.Equal((-50.0, 50.0), AnnotateLogic.Constrain(0, 0, -50, 20, line: false));
    }

    [Fact]
    public void Dragged_areas_stay_in_the_picture()
    {
        Assert.Equal((10, 20, 30, 40), AnnotateLogic.PixelRect(40, 60, 10, 20, 100, 100));
        Assert.Equal((90, 0, 10, 5), AnnotateLogic.PixelRect(90.5, -3, 130, 4.2, 100, 100));
    }

    [Fact]
    public void Blur_replaces_each_block_by_its_average()
    {
        // 4 × 2 picture, Bgra32: left half black, right half white.
        int w = 4, h = 2;
        var src = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte v = (byte)(x < 2 ? 0 : 255);
                src[o] = src[o + 1] = src[o + 2] = v;
                src[o + 3] = 255;
            }
        var one = AnnotateLogic.Pixelate(src, w, h, 0, 0, 4, 2, 4);
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(127, one[i * 4])); // one block: all grey
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(255, one[i * 4 + 3]));
        var two = AnnotateLogic.Pixelate(src, w, h, 1, 0, 2, 2, 2); // the middle two columns: one block
        Assert.Equal(2 * 2 * 4, two.Length);
        Assert.Equal(127, two[0]);
    }

    [Fact]
    public void Blur_blocks_are_coarse_enough()
    {
        Assert.Equal(8, AnnotateLogic.BlurBlock(0, 40, 20));
        Assert.Equal(30, AnnotateLogic.BlurBlock(0, 900, 300));
        Assert.Equal(40, AnnotateLogic.BlurBlock(2, 2000, 2000));
    }

    [Fact]
    public void Sizes_grow_with_big_pictures()
    {
        Assert.Equal(5, AnnotateLogic.StrokeWidth(1, 800, 600));
        Assert.Equal(10, AnnotateLogic.StrokeWidth(1, 3200, 1800));
        Assert.True(AnnotateLogic.TextSize(2, 800, 600) > AnnotateLogic.TextSize(0, 800, 600));
        Assert.True(AnnotateLogic.DarkText(0xFD, 0xD8, 0x35));  // yellow: dark text
        Assert.False(AnnotateLogic.DarkText(0xE5, 0x39, 0x35)); // red: white text
    }
}

public class ViewerTests
{
    [Fact]
    public void The_pixel_under_the_mouse_stays_when_zooming()
    {
        const int pictureW = 4000;
        const double screen = 1000, mouse = 300;
        foreach (var scale in new[] { 0.5, 1.0, 2.0, 3.7 })
        {
            var offset = ViewerLogic.Offset(mouse, screen, pictureW * scale);
            var pixel = (mouse - offset) / scale;
            Assert.Equal(pictureW * mouse / screen, pixel, 6); // 30 % of the screen shows 30 % of the picture
        }
    }

    [Fact]
    public void Small_pictures_are_centred_and_never_enlarged_to_fit()
    {
        Assert.Equal(250, ViewerLogic.Offset(999, 1000, 500));
        Assert.Equal(1, ViewerLogic.Fit(1920, 1080, 800, 600));
        Assert.Equal(0.5, ViewerLogic.Fit(1920, 1080, 3840, 1000));
        Assert.Equal(1, ViewerLogic.ClickZoom(0.5));
        Assert.Equal(2, ViewerLogic.ClickZoom(1));
    }
}
