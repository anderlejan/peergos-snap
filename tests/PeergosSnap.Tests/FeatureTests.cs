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
