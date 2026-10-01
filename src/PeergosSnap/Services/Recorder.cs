using System.Diagnostics;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Records a screen region with the bundled FFmpeg (gdigrab). Pause stops the current segment; resume starts a
/// new one; Stop joins the segments without re-encoding. Cancel throws everything away.
/// With "Record sound" each segment also gets the sound Windows plays (<see cref="LoopbackRecorder"/>); on Stop it is
/// added to the video – but only if there was any sound at all.
/// </summary>
public sealed class Recorder
{
    readonly PxRect region;
    readonly Settings s;
    readonly string workDir;
    readonly List<string> segments = [];
    /// <summary>Per segment: its sound recorder (null = none) and when the video began on that recorder's clock.</summary>
    readonly List<(LoopbackRecorder? Sound, double VideoStart)> sounds = [];
    LoopbackRecorder? sound;
    bool soundBroken;
    readonly Stopwatch clock = new();
    Process? ff;
    string lastError = "";

    public bool Paused { get; private set; }
    public TimeSpan Elapsed => clock.Elapsed;
    public PxRect Region => region;
    /// <summary>After <see cref="StopAsync"/>: whether the video got a sound track, and why not.</summary>
    public bool HasSound { get; private set; }
    public string SoundNote { get; private set; } = "";

    public Recorder(PxRect region, Settings s)
    {
        this.region = Geometry.EvenSize(region);
        this.s = s;
        workDir = Path.Combine(AppPaths.WorkDir, "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(workDir);
    }

    public static bool FfmpegAvailable => File.Exists(AppPaths.FfmpegExe);

    public void Start()
    {
        if (!FfmpegAvailable) throw new InvalidOperationException("FFmpeg is missing from the installation");
        StartSegment();
        clock.Start();
    }

    void StartSegment()
    {
        var seg = Path.Combine(workDir, $"seg{segments.Count:000}.{s.VideoFormat}");
        var psi = new ProcessStartInfo(AppPaths.FfmpegExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in FfmpegArgs.Record(region, s.FrameRate, s.RecordCursor, s.VideoQuality, s.VideoFormat, seg))
            psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // The sound starts just before FFmpeg; gdigrab's start line tells how much sound came before the first frame.
        LoopbackRecorder? snd = null;
        if (s.RecordSound && !soundBroken)
        {
            try
            {
                snd = new LoopbackRecorder(Path.Combine(workDir, $"seg{segments.Count:000}.wav"));
                snd.Start();
            }
            catch (Exception e)
            {
                Log.Error("sound capture", e);
                snd?.Dispose();
                snd = null;
                soundBroken = true; // all segments or none: a video cannot have sound in some parts only
                SoundNote = "No sound: Windows gave no access to its sound output (" + e.Message + ")";
            }
        }
        int index = sounds.Count;
        sounds.Add((snd, 0));
        p.ErrorDataReceived += (_, e) =>
        {
            if (AudioMath.IsVideoStartLine(e.Data))
            {
                if (snd != null) lock (sounds) sounds[index] = (snd, snd.Elapsed.TotalSeconds);
                return;
            }
            if (AudioMath.IsProblemLine(e.Data)) lastError = e.Data!;
        };
        p.Start();
        p.BeginErrorReadLine();
        ff = p;
        sound = snd;
        segments.Add(seg);
        Log.Info($"record: segment {segments.Count} {region}");
    }

    async Task StopSegmentAsync()
    {
        var p = ff;
        var snd = sound;
        ff = null;
        sound = null;
        if (p == null) { if (snd != null) await snd.StopAsync().ConfigureAwait(false); return; }
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync("q").ConfigureAwait(false);
                await p.StandardInput.FlushAsync().ConfigureAwait(false);
                p.StandardInput.Close();
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            try { p.Kill(true); } catch { }
        }
        finally
        {
            p.Dispose();
            if (snd != null) await snd.StopAsync().ConfigureAwait(false);
        }
    }

    public bool HasFailed => ff is { HasExited: true } && ff.ExitCode != 0;
    public string LastError => lastError;

    public async Task PauseAsync()
    {
        if (Paused) return;
        Paused = true;
        clock.Stop();
        await StopSegmentAsync().ConfigureAwait(false);
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        StartSegment();
        clock.Start();
    }

    /// <summary>Finishes the recording and returns the final video file in the work folder.</summary>
    public async Task<string> StopAsync()
    {
        clock.Stop();
        await StopSegmentAsync().ConfigureAwait(false);
        var done = segments.Where(f => File.Exists(f) && new FileInfo(f).Length > 0).ToList();
        if (done.Count == 0)
            throw new InvalidOperationException("Nothing was recorded" + (lastError.Length > 0 ? ": " + lastError : ""));
        var output = Path.Combine(workDir, FileNames.ForCapture(DateTime.Now, s.VideoFormat));
        done = await AddSound(done).ConfigureAwait(false);
        if (done.Count == 1)
        {
            if (s.VideoFormat == "mp4" && await RunFfmpeg(FfmpegArgs.Remux(done[0], output)).ConfigureAwait(false)) return output;
            File.Move(done[0], output, true);
            return output;
        }
        var list = Path.Combine(workDir, "segments.txt");
        await File.WriteAllTextAsync(list, FfmpegArgs.ConcatList(done), new UTF8Encoding(false)).ConfigureAwait(false);
        if (!await RunFfmpeg(FfmpegArgs.Concat(list, s.VideoFormat, output)).ConfigureAwait(false))
            throw new InvalidOperationException("Could not join the recorded parts: " + lastError);
        return output;
    }

    /// <summary>Adds each segment's sound to it – if the whole recording had any sound and every segment has its
    /// sound file. Returns the segments to join (with sound, or the original ones).</summary>
    async Task<List<string>> AddSound(List<string> done)
    {
        if (!s.RecordSound) return done;
        List<(LoopbackRecorder? Sound, double VideoStart)> all;
        lock (sounds) all = segments.Select((f, i) => i < sounds.Count ? sounds[i] : (null, 0)).ToList();
        var parts = segments.Select((f, i) => (Video: f, all[i].Sound, all[i].VideoStart)).Where(x => done.Contains(x.Video)).ToList();
        if (parts.Any(x => x.Sound == null || !File.Exists(x.Sound.File)))
        {
            if (SoundNote.Length == 0) SoundNote = "No sound: it could not be recorded";
            return done;
        }
        float peak = parts.Max(x => x.Sound!.Peak);
        Log.Info($"record: sound peak {peak:0.0000}");
        if (peak < AudioMath.SilenceThreshold)
        {
            SoundNote = "No sound: Windows played nothing while recording";
            return done;
        }
        var withSound = new List<string>();
        foreach (var (video, snd, start) in parts)
        {
            var target = Path.Combine(workDir, Path.GetFileNameWithoutExtension(video) + "-a." + s.VideoFormat);
            if (!await RunFfmpeg(AudioMath.MuxArgs(video, snd!.File, start, s.VideoFormat, target)).ConfigureAwait(false))
            {
                SoundNote = "No sound: it could not be added to the video";
                return done;
            }
            withSound.Add(target);
        }
        HasSound = true;
        SoundNote = "With sound";
        return withSound;
    }

    public async Task CancelAsync()
    {
        clock.Stop();
        await StopSegmentAsync().ConfigureAwait(false);
        Cleanup();
    }

    public void Cleanup()
    {
        try { Directory.Delete(workDir, true); } catch { }
    }

    async Task<bool> RunFfmpeg(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(AppPaths.FfmpegExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        if (p.ExitCode != 0) { lastError = err.Trim(); Log.Error("ffmpeg: " + lastError); }
        return p.ExitCode == 0;
    }
}
