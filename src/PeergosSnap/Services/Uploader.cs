using System.Diagnostics;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Uploads a file to Peergos through the bundled Java bridge (bridge\peergos-snap-bridge.jar + the official
/// Peergos.jar on the bundled runtime). Secrets go to the bridge on stdin, never on the command line.
/// </summary>
public sealed class Uploader
{
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static bool BridgeAvailable =>
        File.Exists(AppPaths.JavaExe) && File.Exists(Path.Combine(AppPaths.BridgeDir, "Peergos.jar"))
        && File.Exists(Path.Combine(AppPaths.BridgeDir, "peergos-snap-bridge.jar"));

    public static async Task<BridgeResult> UploadAsync(Settings s, string file, Action<int>? progress, CancellationToken ct = default)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            if (s.Storage == StorageMode.SharedFolder)
                return await RunAsync(["folder", "--server", ServerFor(s), "--link", s.FolderLink.Trim(), "--file", file, "--name", Path.GetFileName(file)],
                    s.FolderLinkPassword + "\n", progress, TimeSpan.FromMinutes(30), ct);
            return await RunAsync(["account", "--server", ServerFor(s), "--user", s.Username.Trim(), "--folder", s.AccountFolder, "--file", file, "--name", Path.GetFileName(file)],
                s.AccountPassword + "\n" + "\n", progress, TimeSpan.FromMinutes(30), ct);
        }
        finally { OneAtATime.Release(); }
    }

    public static Task<BridgeResult> CheckAsync(Settings s, string totp = "") => s.Storage == StorageMode.SharedFolder
        ? RunAsync(["check", "--server", ServerFor(s), "--link", s.FolderLink.Trim()], s.FolderLinkPassword + "\n", null, TimeSpan.FromMinutes(2), default)
        : RunAsync(["account-check", "--server", ServerFor(s), "--user", s.Username.Trim()], s.AccountPassword + "\n" + totp + "\n", null, TimeSpan.FromMinutes(2), default);

    /// <summary>For a folder link the server is the link's own host; for an account it is the configured server.</summary>
    static string ServerFor(Settings s) =>
        s.Storage == StorageMode.SharedFolder ? LinkCheck.ServerOf(s.FolderLink) ?? s.Server : s.Server;

    static async Task<BridgeResult> RunAsync(string[] args, string stdin, Action<int>? progress, TimeSpan timeout, CancellationToken ct)
    {
        if (!BridgeAvailable)
            return new BridgeResult(false, null, null, "The Peergos uploader is missing from the installation");
        var cp = Path.Combine(AppPaths.BridgeDir, "peergos-snap-bridge.jar") + ";" + Path.Combine(AppPaths.BridgeDir, "Peergos.jar");
        var psi = new ProcessStartInfo(AppPaths.JavaExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppPaths.BridgeDir,
        };
        foreach (var a in new[] { "-Xmx1g", "--enable-native-access=ALL-UNNAMED", "-Djava.awt.headless=true", "-cp", cp, "snap.bridge.PeergosBridge" }.Concat(args))
            psi.ArgumentList.Add(a);

        Log.Info("bridge: " + args[0] + " " + (args.Length > 2 ? args[2] : ""));
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var tail = new Queue<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("@progress ") && int.TryParse(e.Data[10..], out var pct)) { progress?.Invoke(pct); return; }
            lock (tail) { tail.Enqueue(e.Data); while (tail.Count > 40) tail.Dequeue(); }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new BridgeResult(false, null, null, ct.IsCancellationRequested ? "Cancelled" : "The upload took too long and was stopped");
        }
        p.WaitForExit();
        string o;
        lock (stdout) o = stdout.ToString();
        var r = BridgeResult.Parse(o);
        if (!r.Ok)
        {
            string errTail;
            lock (tail) errTail = string.Join("\n", tail);
            Log.Error("bridge failed: " + r.Error + "\n" + errTail);
            r = r with { Error = Friendly(r.Error) };
        }
        else Log.Info("bridge ok: " + r.PeergosPath);
        return r;
    }

    /// <summary>Turns Java/network errors into short plain words.</summary>
    public static string Friendly(string? error)
    {
        var e = error ?? "Unknown error";
        if (e.Contains("UnknownHost", StringComparison.OrdinalIgnoreCase) || e.Contains("ConnectException") || e.Contains("Connection refused"))
            return "Cannot reach the Peergos server (offline?)";
        if (e.Contains("No secret link", StringComparison.OrdinalIgnoreCase)) return "The folder link no longer exists";
        if (e.Contains("expired", StringComparison.OrdinalIgnoreCase)) return "The folder link has expired";
        if (e.Contains("Maximum link retrievals")) return "The folder link was used too many times";
        if (e.Contains("Incorrect password", StringComparison.OrdinalIgnoreCase) || e.Contains("decrypt", StringComparison.OrdinalIgnoreCase))
            return "Wrong password for the link or account";
        if (e.Contains("quota", StringComparison.OrdinalIgnoreCase) || e.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "Not enough space in the Peergos account: " + e;
        return e.Length > 200 ? e[..200] + "…" : e;
    }
}
