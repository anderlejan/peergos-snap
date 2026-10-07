using PeergosSnap.Core;
using Xunit;

namespace PeergosSnap.Tests;

public class CardLogicTests
{
    [Fact]
    public void A_card_offers_open_and_show_file_only_for_files_on_this_pc()
    {
        Assert.Equal(new[] { CardLogic.View, CardLogic.Open, CardLogic.ShowFile }, CardLogic.Arrival(kept: true, 1));
        Assert.Equal(new[] { CardLogic.View, CardLogic.ShowFile }, CardLogic.Arrival(kept: true, 3));
        Assert.Equal(new[] { CardLogic.View, CardLogic.Download, CardLogic.GetLink }, CardLogic.Arrival(kept: false, 1));
        Assert.Equal(new[] { CardLogic.View, CardLogic.Download, CardLogic.GetLinks }, CardLogic.Arrival(kept: false, 2));
        foreach (var n in new[] { 1, 2, 5 })
        {
            Assert.DoesNotContain(CardLogic.Open, CardLogic.Arrival(kept: false, n));
            Assert.DoesNotContain(CardLogic.ShowFile, CardLogic.Arrival(kept: false, n));
            Assert.DoesNotContain(CardLogic.Download, CardLogic.Arrival(kept: true, n));
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)] // the copy goes right after the card
    [InlineData(true, true, false)]   // still uploading or sending
    public void Show_file_only_for_a_copy_that_stays_and_when_done(bool stays, bool busy, bool expected) =>
        Assert.Equal(expected, CardLogic.OffersShowFile(stays, busy));
}

public class PreviewsFromPeergosTests
{
    const string Jpeg = "data:image/jpeg;base64,/9j/4AAQSkZJRg==";

    [Fact]
    public void The_listing_carries_usable_small_pictures_only()
    {
        var files = PeergosLinks.ParseList($$"""
            {"ok":true,"folder":"/example-user/PeergosSnap","files":[
              {"name":"a.png","path":"/example-user/PeergosSnap/a.png","size":5,"modified":0,"links":[],"thumb":"{{Jpeg}}"},
              {"name":"b.png","path":"/example-user/PeergosSnap/b.png","size":5,"modified":0,"links":[],"thumb":"not a picture"},
              {"name":"c.png","path":"/example-user/PeergosSnap/c.png","size":5,"modified":0,"links":[]}]}
            """);
        Assert.Equal(Jpeg, files[0].Thumb);
        Assert.Null(files[1].Thumb);
        Assert.Null(files[2].Thumb);
    }

    [Fact]
    public void Parses_the_previews_answer()
    {
        var map = PeergosLinks.ParsePreviews($$$"""
            {"ok":true,"thumbs":{"/example-user/PeergosSnap/a.png":"{{{Jpeg}}}","/example-user/PeergosSnap/b.png":"data:text/plain;base64,eA==","/example-user/PeergosSnap/c.png":null}}
            """);
        Assert.Single(map);
        Assert.Equal(Jpeg, map["/example-user/PeergosSnap/a.png"]);
        Assert.Empty(PeergosLinks.ParsePreviews(null));
        Assert.Empty(PeergosLinks.ParsePreviews("{"));
        Assert.Empty(PeergosLinks.ParsePreviews("""{"ok":true}"""));
    }

    [Fact]
    public void The_option_is_on_by_default_and_travels_with_the_files_settings()
    {
        Assert.True(new Settings().PreviewsFromPeergos);
        Assert.Contains("PreviewsFromPeergos", SettingsTransfer.Groups.Single(g => g.Key == "files").Keys);
        var json = SettingsTransfer.Export(new Settings { PreviewsFromPeergos = false }, ["files"], "2.7.0",
            new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
        Assert.False((bool)SettingsTransfer.Read(json).Values["PreviewsFromPeergos"]!);
    }
}

public class DownloadTargetTests
{
    static readonly string Root = Path.Combine("C:" + Path.DirectorySeparatorChar, "caps");
    static readonly string Downloads = Path.Combine("C:" + Path.DirectorySeparatorChar, "dl");
    static readonly DateTime Taken = new(2026, 9, 14, 10, 0, 0);

    static string? Target(HistoryRecord r, string[] files, string[] folders) =>
        HistoryLogic.DownloadTarget(r, Root, SubfolderScheme.Month, Downloads, f => files.Contains(f), d => folders.Contains(d));

    [Fact]
    public void A_capture_goes_back_where_it_was()
    {
        var was = Path.Combine(Root, "2026-09", "Snap.png");
        var r = new HistoryRecord { Created = Taken, File = was, PeergosPath = "/example-user/PeergosSnap/Snap.png" };
        Assert.Equal(was, Target(r, [], [Path.Combine(Root, "2026-09")]));
        // Its name is taken meanwhile: a numbered one next to it.
        Assert.Equal(Path.Combine(Root, "2026-09", "Snap (2).png"), Target(r, [was], [Path.Combine(Root, "2026-09")]));
    }

    [Fact]
    public void A_capture_whose_folder_is_gone_goes_into_the_captures_folder_by_its_date()
    {
        var r = new HistoryRecord { Created = Taken, File = Path.Combine("D:" + Path.DirectorySeparatorChar, "gone", "Snap.png"), PeergosPath = "/example-user/PeergosSnap/Snap.png" };
        Assert.Equal(Path.Combine(CaptureFiles.Folder(Root, Taken, SubfolderScheme.Month), "Snap.png"), Target(r, [], []));
        var noFile = new HistoryRecord { Created = Taken, PeergosPath = "/example-user/PeergosSnap/Old.png" };
        Assert.Equal(Path.Combine(Root, "2026-09", "Old.png"), Target(noFile, [], []));
    }

    [Fact]
    public void An_uploaded_file_goes_back_to_where_it_came_from_else_into_downloads()
    {
        var src = Path.Combine("C:" + Path.DirectorySeparatorChar, "docs", "report.pdf");
        var r = new HistoryRecord { Kind = "file", Source = src, PeergosPath = "/example-user/PeergosSnap/report.pdf" };
        Assert.Equal(src, Target(r, [], [Path.GetDirectoryName(src)!]));
        Assert.Equal(Path.Combine(Downloads, "report.pdf"), Target(r, [], []));
        Assert.Equal(Path.Combine(Downloads, "report (2).pdf"), Target(r, [Path.Combine(Downloads, "report.pdf")], []));
    }

    [Fact]
    public void Folders_are_not_downloaded() =>
        Assert.Null(Target(new HistoryRecord { Kind = "folder", PeergosPath = "/example-user/PeergosSnap/f" }, [], []));
}
