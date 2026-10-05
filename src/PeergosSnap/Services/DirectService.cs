using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>An error the bridge reported for a direct-mode command, in its own words.</summary>
public sealed class DirectException(string message) : Exception(message);

/// <summary>
/// The long-running bridge session of the direct mode ("serve"): one Java process that stays signed in, answers
/// commands (send, list, label …) and reports changes in the friends' folders as they happen. The session goes in on
/// stdin, like for every other bridge command. Events arrive on the UI thread.
/// </summary>
public sealed class DirectService : IDisposable
{
    readonly Func<Settings> settings;
    readonly System.Windows.Threading.Dispatcher ui;
    readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = [];
    readonly object gate = new();
    readonly Queue<string> errTail = new();
    Process? process;
    TaskCompletionSource<bool>? ready;
    int nextId;
    string runningAs = "";

    /// <summary>friend, month, all items of that month (both sides).</summary>
    public event Action<string, string, List<DirectItem>>? Changed;
    /// <summary>A problem while checking a friend's folder (said once, until it changes).</summary>
    public event Action<string, string>? Problem;
    /// <summary>The session ended without being asked to (crash, network); with the reason.</summary>
    public event Action<string>? Ended;

    public DirectService(Func<Settings> settings, System.Windows.Threading.Dispatcher ui)
    {
        this.settings = settings;
        this.ui = ui;
    }

    public bool Running => process is { HasExited: false } && ready?.Task.IsCompletedSuccessfully == true && ready.Task.Result;
    public string User => runningAs;

    /// <summary>Starts the session when it is not running (signed in only). Returns an error text, or null when ready.</summary>
    public async Task<string?> EnsureStartedAsync()
    {
        var s = settings();
        if (!s.PeergosConfigured) return "Not signed in to Peergos";
        if (!Uploader.BridgeAvailable) return "The Peergos uploader is missing from the installation";
        Task<bool> wait;
        Process? old = null;
        lock (gate)
        {
            if (process is { HasExited: false } && ready != null && runningAs == s.Username.Trim().ToLowerInvariant())
                wait = ready.Task;
            else
            {
                old = Detach();
                wait = StartLocked(s);
            }
        }
        Shutdown(old);
        var done = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMinutes(3)));
        if (done != wait || !wait.Result)
        {
            string tail;
            lock (errTail) tail = string.Join("\n", errTail.TakeLast(6));
            Log.Error("direct: session did not start\n" + tail);
            Stop();
            return "Could not connect to Peergos" + (LastError.Length > 0 ? ": " + LastError : "");
        }
        return null;
    }

    public string LastError { get; private set; } = "";

    Task<bool> StartLocked(Settings s)
    {
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
            StandardInputEncoding = new UTF8Encoding(false), // no BOM in front of the session
            WorkingDirectory = AppPaths.BridgeDir,
        };
        foreach (var a in new[] { "-Xmx768m", "--enable-native-access=ALL-UNNAMED", "-Djava.awt.headless=true", "-cp", cp,
                     "snap.bridge.PeergosBridge", "serve", "--server", s.Server, "--user", s.Username.Trim() })
            psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var r = ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        runningAs = s.Username.Trim().ToLowerInvariant();
        LastError = "";
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data) || e.Data.StartsWith("@progress ")) return;
            lock (errTail) { errTail.Enqueue(e.Data); while (errTail.Count > 60) errTail.Dequeue(); }
        };
        p.Exited += (_, _) => OnExit(p);
        p.Start();
        p.BeginErrorReadLine();
        process = p;
        _ = ReadLoop(p, r);
        p.StandardInput.WriteLine(s.Session);
        p.StandardInput.Flush();
        Log.Info("direct: session started");
        return r.Task;
    }

    async Task ReadLoop(Process p, TaskCompletionSource<bool> r)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith('{')) continue;
                JsonElement m;
                try { using var doc = JsonDocument.Parse(line); m = doc.RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    TaskCompletionSource<JsonElement>? t;
                    lock (gate) { pending.Remove(id.GetString()!, out t); }
                    t?.TrySetResult(m);
                    continue;
                }
                var ev = m.TryGetProperty("event", out var e) ? e.GetString() : null;
                switch (ev)
                {
                    case "ready": r.TrySetResult(true); break;
                    case "changed":
                        var friend = m.GetProperty("friend").GetString() ?? "";
                        var month = m.GetProperty("month").GetString() ?? "";
                        var items = DirectLogic.ParseItems(m.GetProperty("items"));
                        _ = ui.BeginInvoke(() => Changed?.Invoke(friend, month, items));
                        break;
                    case "problem":
                        var f2 = m.TryGetProperty("friend", out var fe) ? fe.GetString() ?? "" : "";
                        var err = Uploader.Friendly(m.TryGetProperty("error", out var ee) ? ee.GetString() : null);
                        Log.Error($"direct: {f2}: {err}");
                        _ = ui.BeginInvoke(() => Problem?.Invoke(f2, err));
                        break;
                    default:
                        // The final line of a session that ended by itself ({"ok":false,"error":…} before ready).
                        if (m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                            LastError = Uploader.Friendly(m.TryGetProperty("error", out var er) ? er.GetString() : null);
                        break;
                }
            }
        }
        catch (Exception e) { Log.Error("direct: read", e); }
        r.TrySetResult(false);
    }

    void OnExit(Process p)
    {
        List<TaskCompletionSource<JsonElement>> waiting;
        bool expected;
        lock (gate)
        {
            expected = process != p;
            if (process == p) process = null;
            waiting = pending.Values.ToList();
            pending.Clear();
        }
        foreach (var t in waiting) t.TrySetException(new DirectException("The connection to Peergos ended"));
        if (!expected)
        {
            string tail;
            lock (errTail) tail = string.Join("\n", errTail.TakeLast(10));
            Log.Error("direct: session ended\n" + tail);
            var why = LastError.Length > 0 ? LastError : "The connection to Peergos ended";
            _ = ui.BeginInvoke(() => Ended?.Invoke(why));
        }
    }

    /// <summary>Runs one command; throws <see cref="DirectException"/> with the bridge's error.</summary>
    public async Task<JsonElement> CallAsync(string cmd, Dictionary<string, object?>? args = null, TimeSpan? timeout = null)
    {
        var err = await EnsureStartedAsync();
        if (err != null) throw new DirectException(err);
        var msg = new Dictionary<string, object?>(args ?? []);
        TaskCompletionSource<JsonElement> t = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string id;
        Process p;
        lock (gate)
        {
            id = (++nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            msg["id"] = id;
            msg["cmd"] = cmd;
            pending[id] = t;
            p = process ?? throw new DirectException("The connection to Peergos ended");
            p.StandardInput.WriteLine(JsonSerializer.Serialize(msg));
            p.StandardInput.Flush();
        }
        var limit = timeout ?? TimeSpan.FromMinutes(3);
        if (await Task.WhenAny(t.Task, Task.Delay(limit)) != t.Task)
        {
            lock (gate) pending.Remove(id);
            throw new DirectException("Peergos took too long");
        }
        var r = await t.Task;
        if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return r;
        var e = r.TryGetProperty("error", out var ee) ? ee.GetString() : null;
        throw new DirectException(Uploader.Friendly(e));
    }

    public void Stop()
    {
        Process? old;
        lock (gate) old = Detach();
        Shutdown(old);
    }

    /// <summary>Takes the bridge out of the session (under <see cref="gate"/>): an exit from here on is expected.</summary>
    Process? Detach()
    {
        var p = process;
        process = null;
        return p;
    }

    /// <summary>
    /// Ends a detached bridge - never while holding <see cref="gate"/>. Waiting for the exit can run the Exited handler
    /// (<see cref="OnExit"/>, which takes the gate) on this thread while a pool thread already inside that handler holds
    /// the process object's lock and waits for the gate: signing out while direct sharing ran froze the app that way.
    /// </summary>
    static void Shutdown(Process? p)
    {
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                p.StandardInput.WriteLine("{\"id\":\"quit\",\"cmd\":\"quit\"}");
                p.StandardInput.Close();
                if (!p.WaitForExit(3000)) p.Kill(true);
            }
        }
        catch { try { p.Kill(true); } catch { } }
        Log.Info("direct: session stopped");
    }

    public void Dispose() => Stop();
}
