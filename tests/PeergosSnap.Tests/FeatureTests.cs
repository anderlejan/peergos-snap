using PeergosSnap.Core;

namespace PeergosSnap.Tests;

/// <summary>2.2: deleting from the history, discarding, local copies.</summary>
public class DeleteAndDiscardTests
{
    static HistoryRecord R(string id, string? file, string? peergos) => new() { Id = id, File = file, PeergosPath = peergos };

    [Fact]
    public void Defaults_of_2_2()
    {
        var s = new Settings();
        Assert.Equal(HistoryDelete.Both, s.HistoryDeleteAction);
        Assert.True(s.DeleteBothRemovesEntry);
        Assert.True(s.DiscardPermanently);
        Assert.Equal(LocalCopies.Always, s.KeepLocalCopies);
    }

    [Fact]
    public void Only_entries_gone_from_both_places_leave_the_history()
    {
        var both = R("both", "a.png", "/example-user/x/a.png");
        var localOnly = R("local", "b.png", null);
        var remoteOnly = R("remote", null, "/example-user/x/c.png");
        var remoteFailed = R("rfail", "d.png", "/example-user/x/d.png");
        var localFailed = R("lfail", "e.png", "/example-user/x/e.png");
        var ids = HistoryLogic.RemovableAfterDelete([both, localOnly, remoteOnly, remoteFailed, localFailed],
            localGone: new HashSet<string> { "a.png", "b.png", "d.png" },
            remoteGone: new HashSet<string> { "/example-user/x/a.png", "/example-user/x/c.png", "/example-user/x/e.png" });
        Assert.Equal(["both", "local", "remote"], ids);
    }

    [Fact]
    public void An_entry_without_any_file_is_not_touched()
    {
        Assert.Empty(HistoryLogic.RemovableAfterDelete([R("x", null, null)], new HashSet<string>(), new HashSet<string>()));
    }

    [Fact]
    public void Old_settings_files_get_the_new_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        try
        {
            File.WriteAllText(file, """{"Username":"example-user","Output":"SecretLink","RecordCursor":false}""");
            var s = Settings.Load(file);
            Assert.Equal(HistoryDelete.Both, s.HistoryDeleteAction);
            Assert.True(s.DeleteBothRemovesEntry);
            Assert.True(s.DiscardPermanently);
            Assert.False(s.RecordCursor);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>2.2: the sound of videos.</summary>
public class SoundTests
{
    [Fact]
    public void Sound_is_on_by_default() => Assert.True(new Settings().RecordSound);

    [Fact]
    public void Silence_fills_gaps_but_not_jitter()
    {
        Assert.Equal(0, AudioMath.PaddingFrames(TimeSpan.FromSeconds(1), 48000, 48000));
        Assert.Equal(0, AudioMath.PaddingFrames(TimeSpan.FromSeconds(1.05), 48000, 48000)); // 50 ms: normal jitter
        Assert.Equal(24000, AudioMath.PaddingFrames(TimeSpan.FromSeconds(1.5), 48000, 48000)); // half a second missing
        Assert.Equal(0, AudioMath.PaddingFrames(TimeSpan.FromSeconds(1), 60000, 48000)); // ahead: nothing to add
        Assert.Equal(48000, AudioMath.PaddingFrames(TimeSpan.FromSeconds(1), 0, 48000, 0));
        Assert.Equal(0, AudioMath.PaddingFrames(TimeSpan.Zero, 0, 48000));
    }

    [Fact]
    public void Peak_of_float_and_pcm_blocks()
    {
        var f = new byte[16];
        BitConverter.GetBytes(0.25f).CopyTo(f, 4);
        BitConverter.GetBytes(-0.5f).CopyTo(f, 12);
        Assert.Equal(0.5f, AudioMath.Peak(f, true, 32));
        Assert.Equal(0f, AudioMath.Peak(new byte[64], true, 32));
        var p16 = new byte[4];
        BitConverter.GetBytes((short)-16384).CopyTo(p16, 2);
        Assert.Equal(0.5f, AudioMath.Peak(p16, false, 16));
        var p24 = new byte[] { 0, 0, 0x40 }; // 0x400000 = half of full scale
        Assert.Equal(0.5f, AudioMath.Peak(p24, false, 24));
        Assert.True(AudioMath.Peak(new byte[64], true, 32) < AudioMath.SilenceThreshold);
    }

    [Theory]
    [InlineData("[info] Press [q] to stop, [?] for help", true)]
    [InlineData("Press [q] to stop, [?] for help", true)]
    [InlineData("[info] Output #0, mp4, to 'x.mp4':", false)]
    [InlineData(null, false)]
    public void Recognises_the_video_start(string? line, bool start) => Assert.Equal(start, AudioMath.IsVideoStartLine(line));

    [Theory]
    [InlineData("[info] Stream mapping:", false)]
    [InlineData("[gdigrab @ 000001] [error] Failed to capture image", true)]
    [InlineData("[warning] something", true)]
    [InlineData("", false)]
    public void Only_problems_count_as_errors(string line, bool problem) => Assert.Equal(problem, AudioMath.IsProblemLine(line));

    [Fact]
    public void Mux_keeps_the_picture_and_cuts_the_early_sound()
    {
        var a = AudioMath.MuxArgs("v.mp4", "s.wav", 0.3456, "mp4", "o.mp4");
        string After(string k) => a[a.IndexOf(k) + 1];
        Assert.Equal("0.346", After("-ss"));
        Assert.True(a.IndexOf("-ss") < a.IndexOf("s.wav"), "-ss must come before the sound input");
        Assert.Equal("copy", After("-c:v"));
        Assert.Equal("aac", After("-c:a"));
        Assert.Equal("apad", After("-af"));
        Assert.Contains("-shortest", a);
        Assert.Equal("+faststart", After("-movflags"));
        Assert.Equal("o.mp4", a[^1]);
        var w = AudioMath.MuxArgs("v.webm", "s.wav", 0, "webm", "o.webm");
        Assert.Equal("libopus", w[w.IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-ss", w);
        Assert.DoesNotContain("-movflags", w);
    }

    [Fact]
    public void Recording_logs_the_start_line()
    {
        var a = FfmpegArgs.Record(new PxRect(0, 0, 100, 100), 30, true, 23, "mp4", "o.mp4");
        Assert.Equal("level+info", a[a.IndexOf("-loglevel") + 1]);
        Assert.Contains("-nostats", a);
    }
}

/// <summary>2.2: direct sharing with a friend. The JSON lines are shaped like answers of the bridge (sample names).</summary>
public class DirectTests
{
    const string List = """
        {"id":"1","ok":true,"friend":"userb","month":"2026-10","items":[{"name":"Snap_test.png","path":"/usera/PeergosSnap-Direct/userb/2026-10/Snap_test.png","from":"usera","size":300000,"modified":1790824756000,"label":"newer","pinned":false,"stars":["usera","userb"]},{"name":"Photo äöü (2).png","path":"/userb/PeergosSnap-Direct/usera/2026-10/Photo äöü (2).png","from":"userb","size":5000,"modified":1790824789000,"label":"","pinned":false,"stars":[]}],"months":["2026-10"]}
        """;
    const string Discover = """
        {"id":"2","ok":true,"friends":["userb"],"followers":["userb"],"following":["userb"],"incoming":[],"outgoing":[],"direct":["userb"]}
        """;

    static List<DirectItem> Items() => DirectLogic.ParseItems(System.Text.Json.JsonDocument.Parse(List).RootElement.GetProperty("items"));

    [Fact]
    public void Reads_the_bridge_list()
    {
        var items = Items();
        Assert.Equal(2, items.Count);
        var mine = items.Single(i => i.From == "usera");
        Assert.Equal("Snap_test.png", mine.Name);
        Assert.Equal("newer", mine.Label);
        Assert.Equal(["usera", "userb"], mine.Stars);
        Assert.Equal(300000, mine.Size);
        Assert.True(mine.IsImage);
        Assert.Equal(mine.Path.Split('/')[4], mine.Month);
        Assert.Contains(items, i => i.From == "userb" && i.Name == "Photo äöü (2).png");
    }

    [Fact]
    public void Reads_the_bridge_friends()
    {
        var f = DirectLogic.ParseFriends(System.Text.Json.JsonDocument.Parse(Discover).RootElement);
        Assert.Equal(["userb"], f.Friends);
        Assert.Equal(["userb"], f.Direct);
        Assert.Empty(f.Incoming);
        Assert.Equal("Friends – sharing with you", DirectLogic.Status("userb", f));
        Assert.Equal("Not friends yet", DirectLogic.Status("userc", f));
        Assert.Equal("Asked to be your friend", DirectLogic.Status("x", f with { Incoming = ["x"] }));
        Assert.Equal("Waiting until they accept", DirectLogic.Status("y", f with { Outgoing = ["y"] }));
    }

    static DirectItem I(string name, string from, int minute, bool pinned = false) =>
        new(name, $"/{from}/PeergosSnap-Direct/x/2026-10/{name}", from, 1, new DateTime(2026, 10, 1, 10, minute, 0), "", pinned, []);

    [Fact]
    public void Pinned_first_then_newest()
    {
        var sorted = DirectLogic.Sort([I("a.png", "usera", 1), I("b.png", "userb", 5), I("c.png", "userb", 3, pinned: true)]).Select(i => i.Name);
        Assert.Equal(["c.png", "b.png", "a.png"], sorted);
    }

    [Fact]
    public void Only_the_friends_new_pictures_arrive()
    {
        var items = new[] { I("a.png", "usera", 1), I("b.png", "userb", 2), I("c.png", "userb", 3) };
        var seen = new HashSet<string> { items[1].Path };
        Assert.Equal(["c.png"], DirectLogic.Arrived(items, "usera", seen).Select(i => i.Name));
        Assert.All(DirectLogic.Arrived(items, "userb", new HashSet<string>()), i => Assert.Equal("usera", i.From)); // own pictures never "arrive"
    }

    [Theory]
    [InlineData("Userd", "userd")]
    [InlineData(" @usera_b-2 ", "usera_b-2")]
    [InlineData("../evil", null)]
    [InlineData("a b", null)]
    [InlineData("", null)]
    public void Usernames_are_checked(string input, string? expected) => Assert.Equal(expected, DirectLogic.NormaliseUser(input));

    [Fact]
    public void Local_copies_are_safe_file_names()
    {
        var root = Path.Combine("r");
        var theirs = new DirectItem("x:y?.png", "/userb/PeergosSnap-Direct/usera/2026-10/x:y?.png", "userb", 1, DateTime.Now, "", false, []);
        var mine = theirs with { From = "usera", Path = "/usera/PeergosSnap-Direct/userb/2026-10/x:y?.png" };
        Assert.Equal(Path.Combine("r", "userb", "2026-10", "x_y_.png"), DirectLogic.CacheFile(root, "userb", theirs));
        Assert.Equal(Path.Combine("r", "userb", "2026-10", "mine-x_y_.png"), DirectLogic.CacheFile(root, "userb", mine)); // same name from both sides
        var tricky = theirs with { Name = "..", Path = "/userb/PeergosSnap-Direct/usera/../.." };
        Assert.DoesNotContain("..", DirectLogic.CacheFile(root, "userb", tricky));
    }

    [Fact]
    public void Who_starred()
    {
        Assert.Equal("", DirectLogic.StarredBy([], "usera"));
        Assert.Equal("you", DirectLogic.StarredBy(["usera"], "usera"));
        Assert.Equal("userb and you", DirectLogic.StarredBy(["usera", "userb"], "usera"));
    }

    [Fact]
    public void Seen_list_keeps_two_months()
    {
        var kept = DirectLogic.Prune(["/a/PeergosSnap-Direct/b/2026-10/x.png", "/a/PeergosSnap-Direct/b/2026-09/y.png", "/a/PeergosSnap-Direct/b/2026-07/z.png"],
            new DateTime(2026, 10, 15));
        Assert.Equal(2, kept.Count);
        Assert.DoesNotContain("/a/PeergosSnap-Direct/b/2026-07/z.png", kept);
    }

    [Fact]
    public void Direct_settings_defaults_and_cleanup()
    {
        var s = new Settings();
        Assert.True(s.DirectReceive);
        Assert.True(s.DirectBringToFront);
        Assert.Equal(3, s.DirectCheckSeconds);
        s.DirectFriends = ["Userb", "userb", "bad name", "userc"];
        s.DirectCheckSeconds = 0;
        s.Clamp();
        Assert.Equal(["userb", "userc"], s.DirectFriends);
        Assert.Equal(2, s.DirectCheckSeconds);
    }
}
