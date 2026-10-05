using PeergosSnap.Core;
using PeergosSnap.Services;
using Xunit;

namespace PeergosSnap.Tests;

public class OutputLogicTests
{
    [Theory]
    [InlineData(LocalCopies.Always, true, false, true)]
    [InlineData(LocalCopies.Always, false, false, true)]
    [InlineData(LocalCopies.OnlyIfUploadFails, true, false, false)]
    [InlineData(LocalCopies.OnlyIfUploadFails, false, true, true)]
    [InlineData(LocalCopies.OnlyIfUploadFails, false, false, true)] // only copied to the clipboard: kept
    [InlineData(LocalCopies.Never, true, false, false)]
    [InlineData(LocalCopies.Never, false, false, false)]            // only copied to the clipboard: not kept
    [InlineData(LocalCopies.Never, false, true, true)]              // a failed upload is never lost
    public void Local_copy_rules(LocalCopies policy, bool uploaded, bool uploadFailed, bool keep) =>
        Assert.Equal(keep, OutputLogic.KeepLocal(policy, uploaded, uploadFailed));

    [Fact]
    public void The_output_hotkey_goes_round_the_three_choices()
    {
        var m = OutputMode.UploadAndMedia;
        var seen = new HashSet<OutputMode>();
        for (int i = 0; i < 3; i++) { seen.Add(m); m = OutputLogic.Next(m); }
        Assert.Equal(OutputMode.UploadAndMedia, m);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void Menu_texts_of_the_choices()
    {
        Assert.Equal("copy + upload", OutputLogic.Short(OutputMode.UploadAndMedia));
        Assert.Equal("secret link", OutputLogic.Short(OutputMode.SecretLink));
        Assert.Equal("copy only", OutputLogic.Short(OutputMode.DirectMedia));
        Assert.Equal("Upload and copy the picture", OutputLogic.Describe(OutputMode.UploadAndMedia, "picture"));
        Assert.Equal("Copy the video only (no upload)", OutputLogic.Describe(OutputMode.DirectMedia, "video"));
        Assert.True(OutputLogic.Uploads(OutputMode.UploadAndMedia) && OutputLogic.CopiesMedia(OutputMode.UploadAndMedia));
        Assert.False(OutputLogic.Uploads(OutputMode.DirectMedia));
        Assert.False(OutputLogic.CopiesMedia(OutputMode.SecretLink));
    }
}

public class Settings24Tests
{
    [Fact]
    public void Older_settings_keep_their_choices()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pst-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        try
        {
            // as 2.3 wrote them
            File.WriteAllText(file, """{"DefaultKind":"Video","Output":"SecretLink","KeepLocalCopies":"OnlyIfUploadFails"}""");
            var s = Settings.Load(file);
            Assert.Equal(TrayMode.Video, s.Mode);
            Assert.Equal(OutputMode.SecretLink, s.Output); // the new default does not change an existing choice
            Assert.Equal(LocalCopies.OnlyIfUploadFails, s.KeepLocalCopies);
            Assert.True(s.ShutterSound);
            s.Mode = TrayMode.Files;
            s.KeepLocalCopies = LocalCopies.Never;
            s.Output = OutputMode.UploadAndMedia;
            s.ShutterSound = false;
            s.Save(file);
            Assert.Contains("\"DefaultKind\": \"Files\"", File.ReadAllText(file)); // still under the name 2.3 reads
            var back = Settings.Load(file);
            Assert.Equal(TrayMode.Files, back.Mode);
            Assert.Equal(LocalCopies.Never, back.KeepLocalCopies);
            Assert.Equal(OutputMode.UploadAndMedia, back.Output);
            Assert.False(back.ShutterSound);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unknown_numbers_fall_back_to_the_defaults()
    {
        var s = new Settings { Mode = (TrayMode)7, Output = (OutputMode)9, KeepLocalCopies = (LocalCopies)5 };
        s.Clamp();
        Assert.Equal(TrayMode.Picture, s.Mode);
        Assert.Equal(OutputMode.UploadAndMedia, s.Output);
        Assert.Equal(LocalCopies.Always, s.KeepLocalCopies);
    }
}

public class ShutterSoundTests
{
    [Fact]
    public void The_sound_is_bundled_as_a_short_wav()
    {
        using var player = ShutterSound.Load(); // reads and checks the WAV header (nothing is played)
        using var s = typeof(ShutterSound).Assembly.GetManifestResourceStream("PeergosSnap.Assets.shutter.wav")!;
        var head = new byte[12];
        Assert.Equal(12, s.Read(head, 0, 12));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(head, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(head, 8, 4));
        Assert.InRange(s.Length, 10_000, 200_000); // well under a second
    }
}
