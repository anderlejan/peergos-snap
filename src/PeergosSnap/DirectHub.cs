using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using PeergosSnap.Core;
using PeergosSnap.Services;
using PeergosSnap.UI;

namespace PeergosSnap;

/// <summary>
/// The direct mode: pictures go straight to a friend and arrive on their screen within seconds. Each side has one
/// Peergos folder per friend (one subfolder per month) that only that friend can see and change. This class keeps the
/// bridge session running while there are friends to share with, notices what arrives, downloads it and shows it in
/// the direct window, and carries out sending, labels, pins, stars and deletes.
/// </summary>
public sealed class DirectHub : IDisposable
{
    readonly TrayController app;
    readonly DirectService service;
    readonly Dictionary<string, List<DirectItem>> items = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    HashSet<string> seen = [];
    HashSet<string> introduced = new(StringComparer.OrdinalIgnoreCase); // friends whose existing pictures were marked as seen
    string watchMonth = "";
    DateTime lastDiscover = DateTime.MinValue;
    int failures;
    DateTime retryAt = DateTime.MaxValue;
    DirectWindow? window;
    bool starting;

    public event Action? Updated;
    public DirectFriends? FriendsState { get; private set; }
    public string Status { get; private set; } = "";
    public string Me => app.Settings.Username.Trim().ToLowerInvariant();
    public IReadOnlyList<string> Friends => app.Settings.DirectFriends;
    public bool Connected => service.Running;
    public string WatchMonth => watchMonth.Length > 0 ? watchMonth : DirectLogic.MonthOf(DateTime.Now);

    public DirectHub(TrayController app)
    {
        this.app = app;
        service = new DirectService(() => app.Settings, System.Windows.Application.Current.Dispatcher);
        service.Changed += OnChanged;
        service.Problem += (friend, err) => { Status = $"{friend}: {err}"; Updated?.Invoke(); };
        service.Ended += why =>
        {
            failures++;
            Status = "Disconnected from Peergos – trying again soon";
            retryAt = DateTime.Now.AddSeconds(Math.Min(600, 20 * Math.Pow(2, Math.Min(failures, 5))));
            Log.Error("direct: " + why + $"; retry at {retryAt:HH:mm:ss}");
            Updated?.Invoke();
        };
        LoadState();
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
    }

    /// <summary>Is there anything to keep a session open for?</summary>
    bool Wanted => app.Settings.PeergosConfigured && (app.Settings.DirectReceive || window is { IsLoaded: true });

    async Task Tick()
    {
        if (!Wanted) { if (service.Running) service.Stop(); return; }
        if (retryAt <= DateTime.Now) { retryAt = DateTime.MaxValue; await StartAsync(); return; }
        if (!service.Running) return;
        if (DirectLogic.MonthOf(DateTime.Now) != watchMonth) await Watch();
        if (DateTime.Now - lastDiscover > TimeSpan.FromMinutes(5)) await Discover();
    }

    /// <summary>Connects (when wanted), finds friends who share directly, and starts watching their folders.</summary>
    public async Task StartAsync()
    {
        if (starting) return;
        if (!Wanted)
        {
            service.Stop();
            Status = app.Settings.PeergosConfigured ? "Receiving is off (Settings → Direct)" : "Sign in to Peergos first (Settings → Peergos)";
            Updated?.Invoke();
            return;
        }
        starting = true;
        try
        {
            Status = "Connecting to Peergos…";
            Updated?.Invoke();
            if (!await Discover()) return;
            if (Friends.Count == 0 && window is not { IsLoaded: true })
            {
                // Nobody to share with yet: no need to keep the session (it is checked again in a while).
                service.Stop();
                Status = "No friends set up for direct sharing";
                lastDiscover = DateTime.Now;
                retryAt = DateTime.Now.AddMinutes(15);
                Updated?.Invoke();
                return;
            }
            await Watch();
            failures = 0;
        }
        finally { starting = false; }
    }

    async Task<bool> Discover()
    {
        try
        {
            var f = DirectLogic.ParseFriends(await service.CallAsync("discover"));
            lastDiscover = DateTime.Now;
            var before = FriendsState;
            FriendsState = f;
            // Friends who opened direct sharing with this user are added by themselves: nothing to do on this side.
            var added = f.Direct.Where(d => !Friends.Contains(d)).ToList();
            if (added.Count > 0)
            {
                app.UpdateSettings(s => s.DirectFriends = s.DirectFriends.Concat(added).Distinct().ToList());
                foreach (var a in added)
                    app.Notify(ToastKind.Ok, $"{a} shares pictures with you directly", "Pictures from them now appear here at once.",
                        extra: ("Open", () => ShowWindow(a, null)));
            }
            foreach (var who in f.Incoming.Where(u => before == null || !before.Incoming.Contains(u)))
                app.Notify(ToastKind.Ok, $"{who} wants to be your friend in Peergos", "Accept it to share pictures directly.",
                    extra: ("Accept", () => _ = AcceptAsync(who)));
            // My folder for each friend must exist and be shared, so their app finds it.
            foreach (var friend in Friends.Where(x => f.Friends.Contains(x)))
                try { await service.CallAsync("open", new() { ["friend"] = friend }); }
                catch (DirectException e) { Log.Error($"direct: open {friend}: {e.Message}"); }
            Updated?.Invoke();
            return true;
        }
        catch (DirectException e)
        {
            Status = e.Message;
            failures++;
            if (Uploader.NeedsSignIn(e.Message))
            {
                // Retrying cannot help: the user has to sign in again (then the settings change restarts this).
                service.Stop();
                retryAt = DateTime.MaxValue;
                Status = "Sign in to Peergos again (Settings → Peergos) to share directly";
            }
            else retryAt = DateTime.Now.AddSeconds(Math.Min(600, 30 * failures));
            Updated?.Invoke();
            return false;
        }
    }

    async Task Watch()
    {
        watchMonth = DirectLogic.MonthOf(DateTime.Now);
        try
        {
            await service.CallAsync("watch", new()
            {
                ["friends"] = string.Join(",", Friends), ["month"] = watchMonth, ["interval"] = app.Settings.DirectCheckSeconds * 1000,
            });
            Status = Friends.Count == 0 ? "Connected – add a friend to start" : $"Connected – checking every {app.Settings.DirectCheckSeconds} s";
        }
        catch (DirectException e) { Status = e.Message; }
        Updated?.Invoke();
    }

    /// <summary>Settings changed: connect, disconnect or watch other friends as needed.</summary>
    public void SettingsChanged(Settings before)
    {
        var s = app.Settings;
        bool accountChanged = before.Username != s.Username || before.SessionProtected != s.SessionProtected || before.Server != s.Server;
        if (accountChanged) { service.Stop(); items.Clear(); FriendsState = null; }
        if (accountChanged || before.DirectReceive != s.DirectReceive) _ = StartAsync();
        else if (service.Running && (!before.DirectFriends.SequenceEqual(s.DirectFriends) || before.DirectCheckSeconds != s.DirectCheckSeconds))
            _ = Watch();
    }

    // ---------- arriving pictures ----------

    void OnChanged(string friend, string month, List<DirectItem> list)
    {
        if (month != watchMonth) return;
        items[friend] = list;
        if (!introduced.Contains(friend))
        {
            // The first look at a friend's folder: what is there already is not "new".
            introduced.Add(friend);
            foreach (var i in list) seen.Add(i.Path);
            SaveState();
            Updated?.Invoke();
            return;
        }
        var arrived = DirectLogic.Arrived(list, Me, seen);
        foreach (var a in arrived) seen.Add(a.Path);
        if (arrived.Count > 0) SaveState();
        Updated?.Invoke();
        if (arrived.Count > 0) _ = Receive(friend, arrived);
    }

    async Task Receive(string friend, List<DirectItem> arrived)
    {
        string? thumb = null;
        foreach (var a in arrived.Where(a => a.IsImage))
        {
            try { thumb ??= await LocalFileAsync(friend, a); }
            catch (DirectException e) { Log.Error($"direct: download {a.Path}: {e.Message}"); }
        }
        var newest = DirectLogic.Sort(arrived).First();
        var what = arrived.Count == 1 ? (newest.IsImage ? "a picture" : "a file") : $"{arrived.Count} pictures";
        app.Notify(ToastKind.Ok, $"{friend} sent {what}", arrived.Count == 1 ? newest.Name : string.Join(", ", arrived.Select(a => a.Name).Take(3)),
            null, thumb, extra: ("Open", () => ShowWindow(friend, newest.Path)));
        if (app.Settings.DirectBringToFront) ShowWindow(friend, newest.Path, activate: false);
    }

    readonly Dictionary<string, Task<string>> downloading = [];

    /// <summary>The picture on this PC (downloaded once into the direct folder; one download per file at a time).</summary>
    public Task<string> LocalFileAsync(string friend, DirectItem item)
    {
        var file = DirectLogic.CacheFile(AppPaths.DirectDir, friend, item);
        if (File.Exists(file) && new FileInfo(file).Length == item.Size) return Task.FromResult(file);
        if (downloading.TryGetValue(file, out var running)) return running;
        var t = Download(item, file);
        downloading[file] = t;
        return t;
    }

    async Task<string> Download(DirectItem item, string file)
    {
        try
        {
            await service.CallAsync("get", new() { ["path"] = item.Path, ["to"] = file }, TimeSpan.FromMinutes(30));
            return file;
        }
        finally { downloading.Remove(file); }
    }

    // ---------- what the window and the tray menu do ----------

    public IReadOnlyList<DirectItem> Items(string friend) => items.TryGetValue(friend, out var l) ? l : [];

    public async Task<(List<DirectItem> Items, List<string> Months)> ListAsync(string friend, string month)
    {
        var r = await service.CallAsync("list", new() { ["friend"] = friend, ["month"] = month });
        var list = DirectLogic.ParseItems(r.GetProperty("items"));
        var months = r.TryGetProperty("months", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];
        if (month == watchMonth) { items[friend] = list; foreach (var i in list) seen.Add(i.Path); }
        return (list, months);
    }

    /// <summary>Sends files to a friend; returns what was sent (errors are reported per file).</summary>
    public async Task<List<DirectItem>> SendAsync(string friend, IEnumerable<string> files, Action<string>? progress = null)
    {
        var sent = new List<DirectItem>();
        sending++;
        try
        {
        foreach (var f in files)
        {
            progress?.Invoke($"Sending {Path.GetFileName(f)} to {friend}…");
            var r = await service.CallAsync("send", new() { ["friend"] = friend, ["file"] = f, ["month"] = DirectLogic.MonthOf(DateTime.Now) },
                TimeSpan.FromMinutes(30));
            var item = DirectLogic.ParseItems(JsonDocument.Parse("[" + r.GetProperty("item").GetRawText() + "]").RootElement)[0];
            // Keep a copy for the preview, so it does not have to come back from Peergos.
            try
            {
                var local = DirectLogic.CacheFile(AppPaths.DirectDir, friend, item);
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                File.Copy(f, local, true);
            }
            catch (Exception e) { Log.Error("direct: keep sent copy", e); }
            seen.Add(item.Path);
            if (item.Month == watchMonth)
            {
                var l = Items(friend).Where(i => i.Path != item.Path).ToList();
                l.Add(item);
                items[friend] = l;
            }
            sent.Add(item);
        }
        }
        finally { sending--; }
        SaveState();
        app.UpdateSettings(s => s.DirectLastFriend = friend);
        Updated?.Invoke();
        return sent;
    }

    public async Task SetMetaAsync(string friend, DirectItem item, string? label = null, bool? pin = null, bool? star = null)
    {
        var args = new Dictionary<string, object?> { ["path"] = item.Path };
        if (label != null) args["label"] = label;
        if (pin != null) args["pin"] = pin;
        if (star != null) args["star"] = star;
        await service.CallAsync("meta", args);
        // Shown at once; the next check confirms it with the other side's changes merged in.
        var stars = item.Stars.ToList();
        if (star == true && !stars.Contains(Me)) stars.Add(Me);
        if (star == false) stars.Remove(Me);
        Replace(friend, item with { Label = label ?? item.Label, Pinned = pin ?? item.Pinned, Stars = stars });
    }

    public async Task DeleteAsync(string friend, DirectItem item)
    {
        await service.CallAsync("delete", new() { ["path"] = item.Path });
        items[friend] = Items(friend).Where(i => i.Path != item.Path).ToList();
        try { File.Delete(DirectLogic.CacheFile(AppPaths.DirectDir, friend, item)); } catch { }
        Updated?.Invoke();
    }

    void Replace(string friend, DirectItem item)
    {
        items[friend] = Items(friend).Select(i => i.Path == item.Path ? item : i).ToList();
        Updated?.Invoke();
    }

    public async Task<DirectFriends?> RefreshFriendsAsync()
    {
        await Discover();
        return FriendsState;
    }

    /// <summary>Adds a friend: sends a Peergos friend request when they are not a friend yet.</summary>
    public async Task<string> AddFriendAsync(string user)
    {
        // Always the current state: the one from the last check can be minutes old (e.g. the friendship ended since).
        var f = DirectLogic.ParseFriends(await service.CallAsync("friends"));
        string result;
        if (f.Friends.Contains(user)) result = $"{user} is your friend: pictures can go both ways now.";
        else if (f.Incoming.Contains(user)) { await service.CallAsync("accept", new() { ["user"] = user }); result = $"You accepted {user}'s friend request."; }
        else { await service.CallAsync("add", new() { ["user"] = user }); result = $"Friend request sent. When {user} accepts it, you can share pictures."; }
        if (!Friends.Contains(user)) app.UpdateSettings(s => s.DirectFriends = [.. s.DirectFriends, user]);
        await Discover();
        await Watch();
        return result;
    }

    public async Task AcceptAsync(string user)
    {
        try
        {
            await service.CallAsync("accept", new() { ["user"] = user });
            if (!Friends.Contains(user)) app.UpdateSettings(s => s.DirectFriends = [.. s.DirectFriends, user]);
            await Discover();
            await Watch();
            app.Notify(ToastKind.Ok, $"You and {user} are friends now", "Pictures can go both ways directly.", extra: ("Open", () => ShowWindow(user, null)));
        }
        catch (DirectException e) { app.Notify(ToastKind.Warn, "Could not accept the friend request", e.Message); }
    }

    public async Task DeclineAsync(string user)
    {
        await service.CallAsync("decline", new() { ["user"] = user });
        await Discover();
    }

    /// <summary>Stops sharing directly with a friend in this app (the Peergos friendship and the pictures stay).</summary>
    public async Task RemoveFriendAsync(string user)
    {
        app.UpdateSettings(s => s.DirectFriends = s.DirectFriends.Where(x => x != user).ToList());
        items.Remove(user);
        if (service.Running) await Watch();
        Updated?.Invoke();
    }

    // ---------- window ----------

    public void ShowWindow(string? friend, string? path, bool activate = true)
    {
        if (window is not { IsLoaded: true })
        {
            window = new DirectWindow(this, app);
            window.Closed += (_, _) => { window = null; if (!Wanted) service.Stop(); };
        }
        window.ShowFor(friend ?? (app.Settings.DirectLastFriend.Length > 0 ? app.Settings.DirectLastFriend : Friends.FirstOrDefault()), path, activate);
        if (!service.Running && !starting) _ = StartAsync();
    }

    // ---------- seen pictures (so only new ones pop up) ----------

    void LoadState()
    {
        try
        {
            if (!File.Exists(AppPaths.DirectStateFile)) return;
            var j = JsonNode.Parse(File.ReadAllText(AppPaths.DirectStateFile));
            seen = (j?["seen"]?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToHashSet();
            introduced = new HashSet<string>((j?["introduced"]?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? ""), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) { Log.Error("direct: state", e); }
    }

    void SaveState()
    {
        try
        {
            seen = DirectLogic.Prune(seen, DateTime.Now);
            var j = new JsonObject
            {
                ["seen"] = new JsonArray(seen.Order().Select(x => (JsonNode?)x).ToArray()),
                ["introduced"] = new JsonArray(introduced.Order().Select(x => (JsonNode?)x).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DirectStateFile)!);
            var tmp = AppPaths.DirectStateFile + ".tmp";
            File.WriteAllText(tmp, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, AppPaths.DirectStateFile, true);
        }
        catch (Exception e) { Log.Error("direct: save state", e); }
    }

    int sending;
    /// <summary>Sending right now (an automatic update waits for it).</summary>
    public bool Busy => sending > 0;

    public void Dispose()
    {
        timer.Stop();
        service.Dispose();
    }
}
