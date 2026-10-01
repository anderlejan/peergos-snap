using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap;

/// <summary>
/// "PeergosSnap.exe --selftest [--report file] [--upload]" checks the INSTALLED/packaged app on its own temporary
/// data folders: bundled FFmpeg, a 2-second region recording with pause/resume, a picture capture, the Java bridge
/// and (with --upload and PEERGOS_SNAP_TEST_LINK set) a real upload whose link is printed in the report.
/// Nothing touches the user's settings, notes or captures.
/// </summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        var report = new List<string>();
        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            report.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " – " + detail : "")}");
        }

        // --upload uses the app's saved Peergos sign-in (never a password); read it before switching to test folders.
        var saved = args.Contains("--upload") ? Settings.Load(AppPaths.SettingsFile) : null;
        var tmp = Path.Combine(Path.GetTempPath(), "PeergosSnap-selftest-" + Environment.ProcessId);
        AppPaths.Override(Path.Combine(tmp, "data"), Path.Combine(tmp, "local"));
        try
        {
            var s = new Settings { FrameRate = 15, RecordSound = false };

            Check("ffmpeg bundled", Recorder.FfmpegAvailable, AppPaths.FfmpegExe);
            Check("java runtime bundled", File.Exists(AppPaths.JavaExe), AppPaths.JavaExe);
            Check("bridge bundled", Uploader.BridgeAvailable, AppPaths.BridgeDir);
            Check("notes module bundled", File.Exists(Path.Combine(AppPaths.AppDir, "usernotes", "usernotes-ui.js")) &&
                                          File.Exists(Path.Combine(AppPaths.AppDir, "notes-host", "host.js")));
            Check("licences bundled", File.Exists(Path.Combine(AppPaths.AppDir, "licenses", "THIRD-PARTY-NOTICES.md")));
            Check("help bundled", File.Exists(Path.Combine(AppPaths.AppDir, "help", "index.html")) &&
                                  File.Exists(Path.Combine(AppPaths.AppDir, "help", "help.js")));

            // Picture
            var screen = Native.MonitorAt(0, 0);
            var rect = new PxRect(screen.X + 10, screen.Y + 10, 201, 121);
            var pic = Path.Combine(AppPaths.CacheDir, "selftest.png");
            using (var bmp = ScreenCapture.Grab(rect)) ScreenCapture.Save(bmp, pic, "png");
            using (var img = System.Drawing.Image.FromFile(pic))
                Check("picture capture", img.Width == 201 && img.Height == 121, $"{img.Width}x{img.Height}");

            // Recording with a pause in the middle
            if (Recorder.FfmpegAvailable)
            {
                var rec = new Recorder(rect, s);
                rec.Start();
                Thread.Sleep(1500);
                rec.PauseAsync().Wait();
                Thread.Sleep(400);
                rec.Resume();
                Thread.Sleep(1500);
                var video = rec.StopAsync().Result;
                var probe = Run(AppPaths.FfmpegExe, ["-hide_banner", "-i", video]);
                Check("video recording (pause/resume, joined)", File.Exists(video) && new FileInfo(video).Length > 1000 && probe.Contains("200x120"),
                    $"{new FileInfo(video).Length} bytes; {FirstLine(probe, "Video:")}");
                Check("video duration ≈ 3 s", probe.Contains("Duration: 00:00:02") || probe.Contains("Duration: 00:00:03"), FirstLine(probe, "Duration"));
                rec.Cleanup();

                // Mouse pointer option: the same spot recorded with the pointer shown and hidden must differ there.
                Native.GetCursorPos(out var before);
                int cx = rect.X + 100, cy = rect.Y + 50;
                Native.SetCursorPos(cx, cy);
                Thread.Sleep(1200); // let hover effects under the pointer settle
                var withPointer = FrameOf(new Settings { FrameRate = 15, RecordCursor = true, RecordSound = false }, rect);
                var withoutPointer = FrameOf(new Settings { FrameRate = 15, RecordCursor = false, RecordSound = false }, rect);
                var withoutAgain = FrameOf(new Settings { FrameRate = 15, RecordCursor = false, RecordSound = false }, rect);
                Native.SetCursorPos(before.X, before.Y);
                int w = Geometry.EvenSize(rect).Width;
                int changed = Diff(withPointer, withoutPointer, w, 100, 50, 14, 20);
                int noise = Diff(withoutPointer, withoutAgain, w, 100, 50, 14, 20);
                // Compressed video differs a little between two recordings of the same screen; the pointer must stand far above that.
                Check("mouse pointer shown / hidden in videos", changed >= 40 && changed >= 3 * Math.Max(noise, 1),
                    $"{changed} pixels differ at the pointer (shown vs hidden), {noise} between two recordings without it");

                // Sound: a tone played while recording (with a pause in between) must be in the video, in full length.
                try
                {
                    var toned = new Recorder(rect, new Settings { FrameRate = 15, RecordSound = true });
                    toned.Start();
                    var tone = LoopbackRecorder.PlayToneAsync(TimeSpan.FromSeconds(3.2));
                    Thread.Sleep(1500);
                    toned.PauseAsync().Wait();
                    Thread.Sleep(300);
                    toned.Resume();
                    Thread.Sleep(1300);
                    var tv = toned.StopAsync().Result;
                    tone.Wait();
                    var tp = Run(AppPaths.FfmpegExe, ["-hide_banner", "-i", tv]);
                    Check("video with sound (test tone, paused once)", toned.HasSound && tp.Contains("Audio:") && tp.Contains("Duration: 00:00:02"),
                        $"{toned.SoundNote}; {FirstLine(tp, "Duration")}; {FirstLine(tp, "Audio:")} (if this fails: is the PC muted or without speakers?)");
                    toned.Cleanup();

                    // Silence: no sound track at all (only noted, because something else may be playing on this PC).
                    var quiet = new Recorder(rect, new Settings { FrameRate = 15, RecordSound = true });
                    quiet.Start();
                    Thread.Sleep(1200);
                    var qv = quiet.StopAsync().Result;
                    var qp = Run(AppPaths.FfmpegExe, ["-hide_banner", "-i", qv]);
                    if (!quiet.HasSound && !qp.Contains("Audio:")) Check("silent recording has no sound track", true, quiet.SoundNote);
                    else report.Add("NOTE silent recording got sound – was something playing on this PC? " + quiet.SoundNote);
                    quiet.Cleanup();
                }
                catch (Exception e) { Check("video with sound", false, e.Message); }
            }

            // Bridge
            if (Uploader.BridgeAvailable)
            {
                var r = Run(AppPaths.JavaExe, ["-cp", Path.Combine(AppPaths.BridgeDir, "peergos-snap-bridge.jar") + ";" + Path.Combine(AppPaths.BridgeDir, "Peergos.jar"),
                    "snap.bridge.PeergosBridge", "nonsense"], stdoutOnly: true);
                Check("bridge starts", r.Contains("\"ok\":false") && r.Contains("unknown command"), r.Trim());
                if (saved != null)
                {
                    if (!saved.PeergosConfigured) Check("peergos upload", false, "not signed in to Peergos in the app");
                    else
                    {
                        var up = Path.Combine(AppPaths.CacheDir, $"selftest_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                        File.Copy(pic, up);
                        int lastPct = -1;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var u = Uploader.UploadAsync(saved, up, p => lastPct = p).Result;
                        Check("peergos upload with short secret link", u.Ok && PeergosLinks.IsShortSecretLink(u.Link),
                            u.Ok ? $"{u.Link} ({u.PeergosPath}, {sw.Elapsed.TotalSeconds:0.0} s, progress {lastPct}%)" : u.Error ?? "");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Check("self-test crashed", false, e.ToString());
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        report.Add(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
        var text = string.Join(Environment.NewLine, report);
        var i = Array.IndexOf(args, "--report");
        if (i >= 0 && i + 1 < args.Length) File.WriteAllText(args[i + 1], text, new UTF8Encoding(false));
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Records ~1 s of the region and returns one frame as RGB bytes.</summary>
    static byte[] FrameOf(Settings s, PxRect rect)
    {
        var rec = new Recorder(rect, s);
        rec.Start();
        Thread.Sleep(1200);
        var video = rec.StopAsync().Result;
        var raw = Path.Combine(Path.GetDirectoryName(video)!, "frame.rgb");
        Run(AppPaths.FfmpegExe, ["-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf", "select=eq(n\\,5)", "-frames:v", "1",
            "-f", "rawvideo", "-pix_fmt", "rgb24", raw]);
        var bytes = File.Exists(raw) ? File.ReadAllBytes(raw) : [];
        rec.Cleanup();
        return bytes;
    }

    /// <summary>Pixels (in a w×h box at x,y) whose colour differs clearly between two RGB frames.</summary>
    static int Diff(byte[] a, byte[] b, int width, int x, int y, int w, int h)
    {
        if (a.Length == 0 || a.Length != b.Length) return -1;
        int n = 0;
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
            {
                int i = (yy * width + xx) * 3;
                if (i + 2 >= a.Length) continue;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 60) n++;
            }
        return n;
    }

    static string FirstLine(string text, string needle) =>
        text.Split('\n').FirstOrDefault(l => l.Contains(needle))?.Trim() ?? "";

    static string Run(string exe, string[] args, bool stdoutOnly = false)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        return stdoutOnly ? o.Result : o.Result + e.Result;
    }
}
