using PeergosSnap.Core;

namespace PeergosSnap.Tests;

public class CaptureFilesTests
{
    static readonly DateTime T = new(2026, 10, 5, 14, 3, 9);

    [Theory]
    [InlineData(SubfolderScheme.Month, @"C:\caps\2026-10")]
    [InlineData(SubfolderScheme.Day, @"C:\caps\2026-10-05")]
    [InlineData(SubfolderScheme.Year, @"C:\caps\2026")]
    [InlineData(SubfolderScheme.None, @"C:\caps")]
    public void Folder_per_scheme(SubfolderScheme scheme, string expected) => Assert.Equal(expected, CaptureFiles.Folder(@"C:\caps", T, scheme));

    [Fact]
    public void Reads_the_time_from_capture_names()
    {
        Assert.Equal(new DateTime(2026, 9, 27, 2, 0, 50), CaptureFiles.DateFromName(@"C:\x\Snap_2026-09-27_02-00-50.png"));
        Assert.Equal(new DateTime(2026, 9, 27, 2, 0, 50), CaptureFiles.DateFromName("Snap_2026-09-27_02-00-50 (2).mp4"));
        Assert.Null(CaptureFiles.DateFromName("holiday.png"));
        Assert.Null(CaptureFiles.DateFromName("Snap_2026-99-27_02-00-50.png"));
    }

    [Theory]
    [InlineData("Snap_2026-09-27_02-00-50.png", true)]
    [InlineData("snap_2026-09-27_02-00-50.MP4", true)]
    [InlineData("Snap_x.webm", true)]
    [InlineData("Snap_2026-09-27_02-00-50.txt", false)]
    [InlineData("photo.png", false)]
    public void Recognises_captures(string name, bool ok) => Assert.Equal(ok, CaptureFiles.IsCapture(name));

    [Fact]
    public void Tidy_plan_moves_root_captures_into_month_folders()
    {
        var root = @"C:\caps";
        var files = new[]
        {
            @"C:\caps\Snap_2026-09-26_21-39-38.png",
            @"C:\caps\Snap_2026-10-01_08-00-00.mp4",
            @"C:\caps\notes.txt",
            @"C:\caps\2026-09\Snap_2026-09-01_10-00-00.png", // already in a subfolder
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\caps\2026-09\Snap_2026-09-26_21-39-38.png" };
        var plan = CaptureFiles.PlanTidy(files, root, SubfolderScheme.Month, existing.Contains);
        Assert.Equal(2, plan.Count);
        Assert.Equal(@"C:\caps\2026-09\Snap_2026-09-26_21-39-38 (2).png", plan[0].To); // never overwrites
        Assert.Equal(@"C:\caps\2026-10\Snap_2026-10-01_08-00-00.mp4", plan[1].To);
        Assert.Empty(CaptureFiles.PlanTidy(files, root, SubfolderScheme.None, existing.Contains));
    }
}

public class HistoryLogicTests
{
    static DateTime FileTime(string _) => new(2026, 1, 1);

    static RemoteFile Remote(string name, params string[] links) =>
        new(name, "/example-user/PeergosSnap/" + name, 1000, new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc), links);

    [Fact]
    public void Adds_unknown_local_files_with_their_date()
    {
        var added = HistoryLogic.Discover([], [@"C:\c\2026-09\Snap_2026-09-27_02-00-50.png", @"C:\c\2026-09\Snap_2026-09-27_03-00-00.mp4"], null, FileTime);
        Assert.Equal(2, added.Count);
        Assert.Equal(new DateTime(2026, 9, 27, 2, 0, 50), added[0].Created);
        Assert.Equal("picture", added[0].Kind);
        Assert.Equal("video", added[1].Kind);
    }

    [Fact]
    public void Known_files_are_not_added_twice()
    {
        var known = new HistoryRecord { File = @"C:\c\Snap_2026-09-27_02-00-50.png" };
        Assert.Empty(HistoryLogic.Discover([known], [@"c:\C\Snap_2026-09-27_02-00-50.png"], null, FileTime));
    }

    [Fact]
    public void Remote_twin_of_a_local_capture_joins_it_and_brings_its_link()
    {
        var local = new HistoryRecord { File = @"C:\c\2026-09\Snap_2026-09-27_02-00-50.png" };
        var added = HistoryLogic.Discover([local], [], [Remote("Snap_2026-09-27_02-00-50.png", "https://peergos.net/secret/z59a/1#k?open=true")], FileTime);
        Assert.Empty(added);
        Assert.Equal("/example-user/PeergosSnap/Snap_2026-09-27_02-00-50.png", local.PeergosPath);
        Assert.Equal("https://peergos.net/secret/z59a/1#k?open=true", local.Link);
    }

    [Fact]
    public void Remote_only_files_become_records_and_known_ones_get_missing_links()
    {
        var uploaded = new HistoryRecord { File = @"C:\c\a.png", PeergosPath = "/example-user/PeergosSnap/a.png" };
        var added = HistoryLogic.Discover([uploaded], [], [Remote("a.png", "L1"), Remote("Snap_2026-08-01_10-00-00.png")], FileTime);
        Assert.Equal("L1", uploaded.Link);
        var r = Assert.Single(added);
        Assert.Null(r.File);
        Assert.Equal("/example-user/PeergosSnap/Snap_2026-08-01_10-00-00.png", r.PeergosPath);
        Assert.Equal(new DateTime(2026, 8, 1, 10, 0, 0), r.Created);
    }

    [Fact]
    public void Removed_entries_are_not_added_back()
    {
        var dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\c\a.png", "/example-user/PeergosSnap/Snap_2026-08-01_10-00-00.png" };
        var added = HistoryLogic.Discover([], [@"C:\c\a.png"], [Remote("Snap_2026-08-01_10-00-00.png")], FileTime, dismissed);
        Assert.Empty(added);
    }

    [Fact]
    public void Other_files_in_the_Peergos_folder_are_not_captures()
    {
        var added = HistoryLogic.Discover([], [], [Remote("test"), Remote("notes.txt"), Remote("holiday.png"), Remote("Snap_2026-08-01_10-00-00.mp4")], FileTime);
        var r = Assert.Single(added);
        Assert.Equal("video", r.Kind);
        Assert.Equal("/example-user/PeergosSnap/Snap_2026-08-01_10-00-00.mp4", r.PeergosPath);
    }

    [Fact]
    public void Peergos_state_known_or_unknown()
    {
        var r = new HistoryRecord { PeergosPath = "/example-user/x.png" };
        Assert.Null(HistoryLogic.InPeergos(r, null));
        Assert.True(HistoryLogic.InPeergos(r, ["/example-user/x.png"]));
        Assert.False(HistoryLogic.InPeergos(r, []));
        Assert.False(HistoryLogic.InPeergos(new HistoryRecord(), null));
    }

    [Fact]
    public void Gone_means_no_file_anywhere()
    {
        var localOnly = new HistoryRecord { File = "L" };
        var missingNeverUploaded = new HistoryRecord { File = "M" };
        var missingButInPeergos = new HistoryRecord { File = "M2", PeergosPath = "/p/2" };
        var missingDeletedRemotely = new HistoryRecord { File = "M3", PeergosPath = "/p/3" };
        var all = new[] { localOnly, missingNeverUploaded, missingButInPeergos, missingDeletedRemotely };
        bool Exists(string f) => f == "L";
        Assert.Equal([missingNeverUploaded, missingDeletedRemotely], HistoryLogic.Gone(all, Exists, ["/p/2"]));
        // Peergos not checked: uploaded records are kept (they may still be there)
        Assert.Equal([missingNeverUploaded], HistoryLogic.Gone(all, Exists, null));
    }

    [Fact]
    public void Filters_and_search()
    {
        var pic = new HistoryRecord { File = "a.png", Label = "Team meeting", App = "Firefox", Created = new DateTime(2026, 9, 1) };
        var vid = new HistoryRecord { File = "b.mp4", Kind = "video", PeergosPath = "/p/b.mp4", Created = new DateTime(2026, 9, 2) };
        var remote = new HistoryRecord { PeergosPath = "/p/c.png", Created = new DateTime(2026, 9, 3) };
        var all = new[] { pic, vid, remote };
        bool Exists(string f) => f is "a.png" or "b.mp4";
        HashSet<string> rp = ["/p/b.mp4", "/p/c.png"];
        List<HistoryRecord> F(string show, string q = "") => HistoryLogic.Filter(all, show, q, Exists, rp).ToList();
        Assert.Equal(3, F("all").Count);
        Assert.Equal([pic, remote], F("pictures"));
        Assert.Equal([vid], F("videos"));
        Assert.Equal([pic], F("labelled"));
        Assert.Equal([pic, vid], F("pc"));
        Assert.Equal([vid, remote], F("peergos"));
        Assert.Equal([pic], F("pc-only"));
        Assert.Equal([remote], F("peergos-only"));
        Assert.Equal([remote], F("missing"));
        Assert.Equal([pic], F("all", "meeting fire"));
        Assert.Equal([vid], F("all", "2026-09-02"));
    }

    [Fact]
    public void Sorting()
    {
        var a = new HistoryRecord { File = "a.png", Created = new DateTime(2026, 9, 1), Bytes = 5, App = "Zed" };
        var b = new HistoryRecord { File = "b.png", Created = new DateTime(2026, 9, 3), Bytes = 50, Label = "Alpha" };
        var c = new HistoryRecord { File = "c.png", Created = new DateTime(2026, 9, 2), Bytes = 20, App = "Brave" };
        var all = new[] { a, b, c };
        Assert.Equal([b, c, a], HistoryLogic.Sort(all, "newest"));
        Assert.Equal([a, c, b], HistoryLogic.Sort(all, "oldest"));
        Assert.Equal([a, b, c], HistoryLogic.Sort(all, "name")); // "a.png", "Alpha", "c.png"
        Assert.Equal([b, c, a], HistoryLogic.Sort(all, "size"));
        Assert.Equal([c, a, b], HistoryLogic.Sort(all, "app"));
    }

    [Fact]
    public void Day_labels_and_sizes()
    {
        var today = new DateTime(2026, 10, 5);
        Assert.Equal("Today", HistoryLogic.DayLabel(new DateTime(2026, 10, 5, 23, 0, 0), today));
        Assert.Equal("Yesterday", HistoryLogic.DayLabel(new DateTime(2026, 10, 4), today));
        Assert.Equal("Friday 2 October", HistoryLogic.DayLabel(new DateTime(2026, 10, 2), today));
        Assert.Equal("5 September", HistoryLogic.DayLabel(new DateTime(2026, 9, 5), today));
        Assert.Equal("5 September 2025", HistoryLogic.DayLabel(new DateTime(2025, 9, 5), today));
        Assert.Equal("57 KB", HistoryLogic.Size(58218));
        Assert.Equal("1.5 MB", HistoryLogic.Size(1_572_864));
        Assert.Equal("", HistoryLogic.Size(0));
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Saves_loads_and_remembers_removed_entries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        var file = Path.Combine(dir, "history.json");
        try
        {
            var s = HistoryStore.Load(file);
            var a = s.Add(new HistoryRecord { File = @"C:\c\a.png", Label = "first", Link = "L" });
            s.Add(new HistoryRecord { File = @"C:\c\b.png", PeergosPath = "/p/b.png" });
            var back = HistoryStore.Load(file);
            Assert.Equal(2, back.Records.Count);
            Assert.Equal("first", back.Find(a.Id)!.Label);
            back.Remove([a.Id]);
            Assert.Contains(@"C:\c\a.png", back.Dismissed);
            back.Clear();
            Assert.Empty(back.Records);
            var again = HistoryStore.Load(file);
            Assert.Empty(again.Records);
            Assert.Contains("/p/b.png", again.Dismissed);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir, "backups")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class BridgeAnswerTests
{
    [Fact]
    public void Parses_the_folder_listing()
    {
        var files = PeergosLinks.ParseList("""
            {"ok":true,"folder":"/example-user/PeergosSnap","files":[
              {"name":"a.png","path":"/example-user/PeergosSnap/a.png","size":58218,"modified":1790460000000,"links":["https://peergos.net/secret/z59a/1#k?open=true"]},
              {"name":"b.mp4","path":"/example-user/PeergosSnap/b.mp4","size":10,"modified":0,"links":[]}]}
            """);
        Assert.Equal(2, files.Count);
        Assert.Equal("a.png", files[0].Name);
        Assert.Equal(58218, files[0].Size);
        Assert.Single(files[0].Links);
        Assert.Empty(files[1].Links);
        Assert.Empty(PeergosLinks.ParseList(null));
    }

    [Fact]
    public void Parses_the_delete_answer()
    {
        var (d, m, f) = PeergosLinks.ParseDelete("""{"ok":false,"deleted":["/example-user/a"],"missing":["/example-user/b"],"failed":["/example-user/c: boom"],"error":"1 could not be deleted"}""");
        Assert.Equal(["/example-user/a"], d);
        Assert.Equal(["/example-user/b"], m);
        Assert.Equal(["/example-user/c: boom"], f);
    }
}

public class KeepDaysTests
{
    [Theory]
    [InlineData("2.0.0", 30, true)]
    [InlineData("", 30, true)]
    [InlineData("1.1.1", 30, true)]
    [InlineData("2.0.0", 14, false)]
    [InlineData("2.1.0", 30, false)]
    [InlineData("2.0.0", 0, false)]
    public void Old_default_becomes_keep_forever(string last, int days, bool reset) =>
        Assert.Equal(reset, Settings.IsOldKeepDaysDefault(last, days));

    [Fact]
    public void New_defaults()
    {
        var s = new Settings();
        Assert.Equal(0, s.CacheKeepDays);
        Assert.Equal(SubfolderScheme.Month, s.Subfolders);
        Assert.Equal(0, s.DelaySeconds);
        Assert.True(s.RememberApp);
        s.DelaySeconds = 500;
        s.Clamp();
        Assert.Equal(60, s.DelaySeconds);
    }
}
