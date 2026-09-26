using System.Diagnostics;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Records a screen region with the bundled FFmpeg (gdigrab). Pause stops the current segment; resume starts a
/// new one; Stop joins the segments without re-encoding. Cancel throws everything away.
/// </summary>
public sealed class Recorder
{
    readonly PxRect region;
    readonly Settings s;
    readonly string workDir;
    readonly List<string> segments = [];
    readonly Stopwatch clock = new();
    Process? ff;
    string lastError = "";

    public bool Paused { get; private set; }
    public TimeSpan Elapsed => clock.Elapsed;
    public PxRect Region => region;

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
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data; };
        p.Start();
        p.BeginErrorReadLine();
        ff = p;
        segments.Add(seg);
        Log.Info($"record: segment {segments.Count} {region}");
    }

    async Task StopSegmentAsync()
    {
        var p = ff;
        ff = null;
        if (p == null) return;
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
        finally { p.Dispose(); }
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
