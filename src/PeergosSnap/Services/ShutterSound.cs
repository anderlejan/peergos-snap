using System.Media;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// The sound when a picture is taken (Settings → Capture): a telephoto lens focusing, then the mirror and the shutter.
/// Made for Peergos Snap by build\make-shutter-sound.js.
/// </summary>
public static class ShutterSound
{
    const string Resource = "PeergosSnap.Assets.shutter.wav";
    static SoundPlayer? player;

    /// <summary>Plays it without waiting; a problem only goes to the log (a picture never fails because of it).</summary>
    public static void Play()
    {
        try
        {
            player ??= Load();
            player.Play();
        }
        catch (Exception e) { Log.Error("shutter sound", e); }
    }

    /// <summary>The sound, read and checked (the self-test calls this without playing it).</summary>
    public static SoundPlayer Load()
    {
        var stream = typeof(ShutterSound).Assembly.GetManifestResourceStream(Resource)
                     ?? throw new InvalidOperationException("The shutter sound is missing from the app.");
        var p = new SoundPlayer(stream);
        p.Load();
        return p;
    }
}
