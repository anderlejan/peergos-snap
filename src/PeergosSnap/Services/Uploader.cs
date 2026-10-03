using System.Diagnostics;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Talks to Peergos through the bundled Java bridge (bridge\peergos-snap-bridge.jar + the official Peergos.jar on
/// the bundled runtime). Secrets (password, two-factor code, session) go to the bridge on stdin, never on the
/// command line. After signing in only the Peergos session is kept; uploads restore it.
/// </summary>
public sealed class Uploader
{
    static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static bool BridgeAvailable =>
        File.Exists(AppPaths.JavaExe) && File.Exists(Path.Combine(AppPaths.BridgeDir, "Peergos.jar"))
        && File.Exists(Path.Combine(AppPaths.BridgeDir, "peergos-snap-bridge.jar"));

    /// <summary>Signs in; <paramref name="askCode"/> is called if the account wants a two-factor code (null = cancel).
    /// On success <see cref="BridgeResult.Session"/> holds what later uploads need.</summary>
    public static async Task<BridgeResult> SignInAsync(string server, string user, string password, Func<Task<string?>> askCode)
    {
        await OneAtATime.WaitAsync();
        try
        {
            return await RunAsync(["signin", "--server", server, "--user", user.Trim()], password, null, askCode, TimeSpan.FromMinutes(3), default);
        }
        finally { OneAtATime.Release(); }
    }

    public static async Task<BridgeResult> UploadAsync(Settings s, string file, Action<int>? progress, CancellationToken ct = default)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            return await RunAsync(["upload", "--server", s.Server, "--user", s.Username.Trim(), "--folder", s.AccountFolder,
                    "--file", file, "--name", Path.GetFileName(file)],
                s.Session, progress, null, TimeSpan.FromMinutes(30), ct);
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>Uploads several files with one sign-in (from the tray menu). Without <paramref name="folderName"/> each
    /// file goes into the capture folder with its own link; with it, a new folder of that name gets the files at their
    /// relative paths and one link. <paramref name="files"/>: (file on this PC, relative path).</summary>
    public static async Task<(BridgeResult Result, PutResult Put)> PutAsync(Settings s, IReadOnlyList<(string Local, string Relative)> files,
        string? folderName, Action<int>? progress)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var args = new List<string> { "put", "--server", s.Server, "--user", s.Username.Trim(), "--folder", s.AccountFolder };
            if (!string.IsNullOrWhiteSpace(folderName)) { args.Add("--dir"); args.Add(folderName); }
            var input = s.Session + "\n" + string.Join("\n", files.Select(f => f.Local + "\t" + f.Relative.Replace('\\', '/'))) + "\n";
            var r = await RunAsync([.. args], input, progress, null, TimeSpan.FromHours(6), default);
            return (r, PeergosLinks.ParsePut(r.Raw));
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>The folders inside a folder of the Peergos home ("" = the home itself), to choose the capture folder.</summary>
    public static async Task<(BridgeResult Result, string Path, bool Exists, List<string> Folders)> FoldersAsync(Settings s, string relative)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var r = await RunAsync(["folders", "--server", s.Server, "--user", s.Username.Trim(), "--path", relative.Length == 0 ? "/" : relative],
                s.Session, null, null, TimeSpan.FromMinutes(2), default);
            var (p, e, f) = r.Ok ? PeergosLinks.ParseFolders(r.Raw) : ("", false, new List<string>());
            return (r, p, e, f);
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>The files in the Peergos capture folder with their secret links.</summary>
    public static async Task<(BridgeResult Result, List<RemoteFile> Files)> ListAsync(Settings s)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var r = await RunAsync(["list", "--server", s.Server, "--user", s.Username.Trim(), "--folder", s.AccountFolder],
                s.Session, null, null, TimeSpan.FromMinutes(3), default);
            return (r, r.Ok ? PeergosLinks.ParseList(r.Raw) : []);
        }
        finally { OneAtATime.Release(); }
    }

    /// <summary>Deletes files from Peergos (their secret links go with them).</summary>
    public static async Task<(BridgeResult Result, List<string> Deleted, List<string> Missing, List<string> Failed)> DeleteAsync(Settings s, IEnumerable<string> paths)
    {
        await OneAtATime.WaitAsync();
        try
        {
            var r = await RunAsync(["delete", "--server", s.Server, "--user", s.Username.Trim()],
                s.Session + "\n" + string.Join("\n", paths), null, null, TimeSpan.FromMinutes(10), default);
            var (d, m, f) = PeergosLinks.ParseDelete(r.Raw);
            return (r, d, m, f);
        }
        finally { OneAtATime.Release(); }
    }

    public static async Task<BridgeResult> CheckAsync(Settings s)
    {
        await OneAtATime.WaitAsync();
        try
        {
            return await RunAsync(["check", "--server", s.Server, "--user", s.Username.Trim()], s.Session, null, null, TimeSpan.FromMinutes(2), default);
        }
        finally { OneAtATime.Release(); }
    }

    static async Task<BridgeResult> RunAsync(string[] args, string secret, Action<int>? progress, Func<Task<string?>>? askCode,
        TimeSpan timeout, CancellationToken ct)
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
            // The bridge reads stdin as UTF-8: file and folder names with accents must arrive unchanged (no BOM).
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppPaths.BridgeDir,
        };
        foreach (var a in new[] { "-Xmx1g", "--enable-native-access=ALL-UNNAMED", "-Djava.awt.headless=true", "-cp", cp, "snap.bridge.PeergosBridge" }.Concat(args))
            psi.ArgumentList.Add(a);

        Log.Info("bridge: " + args[0]);
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var tail = new Queue<string>();
        var codeWanted = new TaskCompletionSource();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.StartsWith("@progress ") && int.TryParse(e.Data[10..], out var pct)) { progress?.Invoke(pct); return; }
            if (e.Data.StartsWith("@mfa")) { codeWanted.TrySetResult(); return; }
            lock (tail) { tail.Enqueue(e.Data); while (tail.Count > 40) tail.Dequeue(); }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.StandardInput.WriteLineAsync(secret);
        await p.StandardInput.FlushAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var exited = p.WaitForExitAsync(cts.Token);
            if (askCode != null)
            {
                // Sign-in may ask for a second factor once; answer it (empty = cancel), then wait for the end.
                if (await Task.WhenAny(exited, codeWanted.Task) == codeWanted.Task)
                {
                    var code = await askCode();
                    await p.StandardInput.WriteLineAsync(code ?? "");
                    await p.StandardInput.FlushAsync();
                }
            }
            p.StandardInput.Close();
            await exited;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new BridgeResult(false, null, null, ct.IsCancellationRequested ? "Cancelled" : "Peergos took too long and the attempt was stopped");
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
        else Log.Info("bridge ok: " + args[0] + " " + r.PeergosPath);
        return r;
    }

    /// <summary>True when the stored session no longer works and the user has to sign in again.</summary>
    public static bool NeedsSignIn(string? error) =>
        error != null && (error.StartsWith("Not signed in") || error.StartsWith("Your Peergos session has ended"));

    /// <summary>Turns Java/network errors into short plain words.</summary>
    public static string Friendly(string? error)
    {
        var e = (error ?? "Unknown error").Replace('+', ' ');
        if (e.Contains("UnknownHost", StringComparison.OrdinalIgnoreCase) || e.Contains("ConnectException") || e.Contains("Connection refused")
            || e.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "Cannot reach the Peergos server (offline?)";
        if (e.Contains("Unknown username")) return "Unknown Peergos username";
        if (e.Contains("has been deleted")) return "This Peergos account has been deleted";
        if (e.Contains("Incorrect password", StringComparison.OrdinalIgnoreCase)) return "Wrong password";
        if (e.Contains("Invalid TOTP", StringComparison.OrdinalIgnoreCase) || e.Contains("rejected second factor", StringComparison.OrdinalIgnoreCase))
            return "Wrong two-factor code";
        if (e.Contains("no two-factor code")) return "Sign-in cancelled";
        if (e.Contains("Session expired") || e.Contains("Legacy accounts"))
            return "Your Peergos session has ended – sign in again (Settings → Peergos)";
        if (e.StartsWith("Not signed in")) return "Not signed in to Peergos – sign in in Settings → Peergos";
        if (e.Contains("quota", StringComparison.OrdinalIgnoreCase) || e.Contains("space", StringComparison.OrdinalIgnoreCase))
            return "Not enough space in the Peergos account: " + e;
        return e.Length > 200 ? e[..200] + "…" : e;
    }
}
