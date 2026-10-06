using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Threading;
using PeergosSnap.Core;
using PeergosSnap.Services;
using PeergosSnap.UI;
using WinForms = System.Windows.Forms;

namespace PeergosSnap;

/// <summary>The tray icon and everything it starts: pictures, recordings, uploads and the clipboard.</summary>
public sealed class TrayController : IDisposable
{
    public Settings Settings { get; private set; }
    readonly WinForms.NotifyIcon tray = new();
    readonly HotkeyManager hotkeys = new();
    readonly Icon iconIdle, iconRec, iconPaused, iconBusy, iconNew;
    readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    /// <summary>What a friend sent that was not looked at yet: the icon shows a dot (flashing, Settings → Direct) and its
    /// tooltip says what came, until the direct window shows it.</summary>
    (string Friend, string What, string Path, DateTime At)? unseen;
    readonly DispatcherTimer flash = new() { Interval = TimeSpan.FromMilliseconds(600) };
    bool flashOn;
    /// <summary>The friend a video is recorded for (Direct to a friend in Videos): it goes to them when it stops.</summary>
    string? recordingFor;
    Recorder? recorder;
    RecordingControls? controls;
    RegionFrame? frame;
    bool selecting, stopping;
    int uploads;
    string? lastLink;
    SettingsWindow? settingsWindow;
    NotesWindow? notesWindow;
    HelpWindow? helpWindow;
    HistoryWindow? historyWindow;
    readonly HistoryStore history;
    CancellationTokenSource? countdownCts;
    (string? App, string? Title) recordingSource;
    DateTime recordingStarted;
    readonly Dictionary<int, Icon> numberIcons = [];
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    bool installingUpdate;
    UpdateInfo? pendingUpdate;
    /// <summary>Set by a mode entry of the tray menu: the menu stays open and shows that mode's actions.</summary>
    bool keepMenuOpen;
    /// <summary>Where the pointer was when the tray menu opened (a changed menu keeps its edge there).</summary>
    Point menuOpenedAt;
    public Dictionary<string, string?> HotkeyErrors { get; } = [];
    readonly DirectHub direct;
    public DirectHub Direct => direct;

    public TrayController()
    {
        Settings = Settings.Load(AppPaths.SettingsFile);
        history = HistoryStore.Load(AppPaths.HistoryFile);
        iconIdle = MakeIcon(Color.FromArgb(43, 138, 110), null);
        iconRec = MakeIcon(Color.FromArgb(43, 138, 110), Color.Red);
        iconPaused = MakeIcon(Color.FromArgb(43, 138, 110), Color.Orange);
        iconBusy = MakeIcon(Color.FromArgb(40, 110, 200), null);
        iconNew = MakeIcon(Color.FromArgb(43, 138, 110), Color.FromArgb(255, 179, 0));
        flash.Tick += (_, _) => { flashOn = !flashOn; ShowIdleIcon(); };

        tray.Icon = iconIdle;
        tray.Visible = true;
        tray.ContextMenuStrip = new WinForms.ContextMenuStrip();
        // The menu is built when it opens. WinForms decides before this event to cancel opening a menu that has no
        // items, so the very first right click showed nothing: opening is allowed again once it is built.
        tray.ContextMenuStrip.Opening += (_, e) => { menuOpenedAt = WinForms.Cursor.Position; BuildMenu(); e.Cancel = false; };
        // A mode entry keeps the menu open. The menu's ItemClicked comes before it closes; the entry's own Click
        // comes too late to stop that.
        tray.ContextMenuStrip.ItemClicked += (_, e) => { if (e.ClickedItem?.Tag is TrayMode) keepMenuOpen = true; };
        tray.ContextMenuStrip.Closing += (_, e) =>
        {
            if (keepMenuOpen && e.CloseReason == WinForms.ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            keepMenuOpen = false;
        };
        tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) _ = PrimaryClick(); };
        watch.Tick += async (_, _) => await WatchRecorder();
        var firstRun = !File.Exists(AppPaths.SettingsFile);
        ApplySettings();
        if (!firstRun) MigrateKeepDays();
        UpdateTip();
        CleanCache();
        TidyCaptures();
        DiscoverLocalCaptures();

        if (firstRun)
        {
            Settings.LastRunVersion = Updater.Current.ToString();
            Settings.Save(AppPaths.SettingsFile);
            Notify(ToastKind.Ok, "Peergos Snap is running", "Click the tray icon to capture. Right-click it → Settings → Peergos to sign in.",
                extra: ("Quick start", () => ShowHelp("quick-start")));
        }
        else StartupNotices();
        _ = MigrateLegacyPassword();

        // Direct sharing with friends: connect a little after the start (the tray must not wait for Java).
        direct = new DirectHub(this);
        var directStart = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        directStart.Tick += async (_, _) => { directStart.Stop(); await direct.StartAsync(); };
        directStart.Start();

        // Updates: once shortly after the start, then the hourly timer checks whether a day has passed.
        updateTimer.Tick += async (_, _) => await AutoUpdate(false);
        updateTimer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        first.Tick += async (_, _) => { first.Stop(); await AutoUpdate(true); };
        first.Start();
    }

    public HistoryStore History => history;

    /// <summary>Raised after every settings change (the History window follows sign-in and sign-out with it).</summary>
    public event Action<Settings>? SettingsChanged;

    /// <summary>Before 2.1 local copies were deleted after 30 days by default; now the default is to keep them.</summary>
    void MigrateKeepDays()
    {
        if (Settings.IsOldKeepDaysDefault(Settings.LastRunVersion, Settings.CacheKeepDays))
        {
            UpdateSettings(s => s.CacheKeepDays = 0);
            Log.Info("local copies: the old 30-day default changed to keep forever");
        }
    }

    /// <summary>Once: captures lying directly in the captures folder (2.0) move into their month (or chosen) subfolders.</summary>
    void TidyCaptures()
    {
        if (Settings.CapturesTidied) return;
        try
        {
            var root = AppPaths.CacheDir;
            var plan = Directory.Exists(root)
                ? CaptureFiles.PlanTidy(Directory.GetFiles(root), root, Settings.Subfolders)
                : [];
            int moved = 0;
            foreach (var (from, to) in plan)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to);
                    foreach (var r in history.Records.Where(r => string.Equals(r.File, from, StringComparison.OrdinalIgnoreCase)))
                        r.File = to;
                    moved++;
                }
                catch (Exception e) { Log.Error("tidy " + from, e); }
            }
            if (moved > 0) history.Save();
            Log.Info($"captures folder sorted into subfolders: {moved} files moved");
            UpdateSettings(s => s.CapturesTidied = true);
        }
        catch (Exception e) { Log.Error("tidy", e); }
    }

    /// <summary>Captures on this PC that the history does not know yet (older versions, copied files) join it.</summary>
    public void DiscoverLocalCaptures()
    {
        try
        {
            var added = HistoryLogic.Discover(history.Records, CaptureFiles.Scan(AppPaths.CacheDir), null, File.GetLastWriteTime, history.Dismissed);
            foreach (var r in added)
                if (r.File != null) r.Bytes = new FileInfo(r.File).Length;
            if (added.Count > 0)
            {
                history.AddRange(added);
                Log.Info($"history: {added.Count} earlier captures added");
            }
        }
        catch (Exception e) { Log.Error("history discover", e); }
    }

    /// <summary>One-time notices after an update.</summary>
    void StartupNotices()
    {
        var now = Updater.Current.ToString();
        if (Settings.LastRunVersion != now)
        {
            bool updated = Settings.LastRunVersion.Length > 0 || Settings.HadFolderLink || Settings.Username.Length > 0;
            UpdateSettings(s => s.LastRunVersion = now);
            if (updated)
                Notify(ToastKind.Ok, $"Peergos Snap was updated to {now}", "See Help for everything it can do.", extra: ("Help", () => ShowHelp("")));
        }
        if (Settings.HadFolderLink && !Settings.PeergosConfigured && !Settings.FolderRemovalNoticeShown && Settings.AccountPasswordProtected.Length == 0)
        {
            UpdateSettings(s => s.FolderRemovalNoticeShown = true);
            Notify(ToastKind.Warn, "Sharing through a folder link was removed",
                "Sign in with your Peergos account (Settings → Peergos) to get secret links again. Until then captures are copied to the clipboard.",
                extra: ("Sign in", ShowSettings));
        }
    }

    /// <summary>Version 1.x kept the account password; sign in with it once, keep only the session, forget the password.</summary>
    async Task MigrateLegacyPassword()
    {
        if (Settings.PeergosConfigured || Settings.AccountPasswordProtected.Length == 0) return;
        var pw = Settings.LegacyAccountPassword;
        if (pw.Length == 0 || Settings.Username.Trim().Length == 0) { UpdateSettings(s => s.AccountPasswordProtected = ""); return; }
        var r = await Uploader.SignInAsync(Settings.Server, Settings.Username, pw, () => Task.FromResult<string?>(null));
        if (r.Ok && !string.IsNullOrEmpty(r.Session))
        {
            UpdateSettings(s => { s.Session = r.Session!; s.AccountPasswordProtected = ""; });
            Notify(ToastKind.Ok, $"Signed in to Peergos as {Settings.Username}",
                "Your password is no longer stored: Peergos Snap now keeps only the sign-in, until you sign out.");
        }
        else if (r.Error != null && !r.Error.StartsWith("Cannot reach"))
        {
            UpdateSettings(s => s.AccountPasswordProtected = "");
            Notify(ToastKind.Warn, "Please sign in to Peergos", r.Error + "\nSettings → Peergos.", extra: ("Sign in", ShowSettings));
        }
    }

    // ---------- updates ----------

    bool Busy => recorder != null || uploads > 0 || selecting || stopping || direct?.Busy == true;

    async Task AutoUpdate(bool atStart)
    {
        if (installingUpdate) return;
        if (pendingUpdate != null && !Busy) { var u = pendingUpdate; pendingUpdate = null; await InstallUpdateAsync(u); return; }
        if (!Settings.CheckForUpdates) return;
        if (!atStart && Settings.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(23)) return;
        UpdateInfo? found;
        try { found = await Updater.CheckAsync(); }
        catch (Exception e) { Log.Error("update check", e); return; }
        UpdateSettings(s => s.LastUpdateCheck = DateTime.Now);
        if (found == null) return;
        if (!Settings.InstallUpdatesAutomatically)
        {
            Notify(ToastKind.Ok, $"Peergos Snap {found.Version} is available", $"You have {Updater.Current}.",
                extra: ("Install now", () => _ = InstallUpdateAsync(found)));
            return;
        }
        if (Busy) { pendingUpdate = found; return; }
        await InstallUpdateAsync(found);
    }

    /// <summary>Downloads, verifies and starts the installer, then quits (the installer starts the new version).
    /// Returns an error text, or null when the installer was started.</summary>
    public async Task<string?> InstallUpdateAsync(UpdateInfo u, Action<int>? progress = null)
    {
        if (Busy) return "Finish the recording or upload first.";
        if (installingUpdate) return "The update is already being installed.";
        installingUpdate = true;
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            Notify(ToastKind.Busy, $"Downloading Peergos Snap {u.Version}…", "It installs and starts by itself.");
            var file = await Updater.DownloadAsync(u, pct =>
            {
                progress?.Invoke(pct);
                dispatcher.BeginInvoke(() => Notify(ToastKind.Busy, $"Downloading Peergos Snap {u.Version}…", "It installs and starts by itself.",
                    percent: pct >= 0 ? pct : null));
            });
            Notify(ToastKind.Busy, $"Installing Peergos Snap {u.Version}…", "Peergos Snap restarts in a moment.");
            Updater.StartInstaller(file);
            await Task.Delay(800);
            System.Windows.Application.Current.Shutdown();
            return null;
        }
        catch (Exception e)
        {
            Log.Error("update", e);
            installingUpdate = false;
            var msg = e is System.Net.Http.HttpRequestException ? "No connection to GitHub" : e.Message;
            Notify(ToastKind.Error, "The update failed", msg + "\nNothing was changed.");
            return msg;
        }
    }

    // ---------- settings ----------

    public void UpdateSettings(Action<Settings> change)
    {
        var before = Settings.Clone();
        change(Settings);
        Settings.Save(AppPaths.SettingsFile);
        ApplySettings();
        UpdateTip();
        if (before.ColorScheme != Settings.ColorScheme) RebuildOpenWindows();
        direct?.SettingsChanged(before);
        SettingsChanged?.Invoke(before);
    }

    /// <summary>WPF's Fluent style cannot fully restyle an open window, so a new colour scheme re-creates the open
    /// Settings window in place (same position, size and tab). User notes follow through CSS variables.</summary>
    void RebuildOpenWindows()
    {
        if (settingsWindow is not { IsLoaded: true } old) return;
        var d = System.Windows.Application.Current.Dispatcher;
        d.BeginInvoke(() =>
        {
            var w = new SettingsWindow(this, old.Tabs.SelectedIndex)
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = old.Left, Top = old.Top, Width = old.Width, Height = old.Height,
            };
            settingsWindow = w;
            w.Show();
            old.Close();
            w.Activate();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    string appliedHotkeys = "\0";
    string appliedTheme = "\0";

    bool? appliedExplorerMenu;

    /// <summary>The menu package was added or removed (Settings → General): the classic entries follow.</summary>
    public void RefreshExplorerMenu()
    {
        appliedExplorerMenu = null;
        ApplySettings();
    }

    void ApplySettings()
    {
        ToastWindow.SuccessSeconds = Settings.ToastSeconds;
        if (unseen != null)
        {
            if (Settings.DirectFlash) flash.Start(); else flash.Stop();
            ShowIdleIcon();
        }
        // On Windows 11 with the menu package the packaged entry (at the top) is the menu: then no classic entries, or
        // Windows would list Peergos Snap twice under "Show more options".
        bool classic = Settings.ExplorerMenu && !(Settings.ExplorerMenuTop && ExplorerPackage.Registered());
        if (appliedExplorerMenu != classic)
        {
            appliedExplorerMenu = classic;
            try { ExplorerMenu.Sync(classic, Environment.ProcessPath ?? Path.Combine(AppPaths.AppDir, "PeergosSnap.exe")); }
            catch (Exception e)
            {
                Log.Error("explorer menu", e);
                Notify(ToastKind.Warn, "Explorer's right-click menu could not be changed", e.Message);
            }
        }
        var theme = Settings.ColorScheme + "|" + Settings.FontPercent;
        if (theme != appliedTheme)
        {
            appliedTheme = theme;
            Theme.Apply(Settings.ColorScheme, Settings.FontPercent);
        }
        var keys = string.Join("|", Settings.HotkeyPicture, Settings.HotkeyVideo, Settings.HotkeyPause, Settings.HotkeyToggleOutput);
        if (keys == appliedHotkeys) return;
        appliedHotkeys = keys;
        hotkeys.Clear();
        HotkeyErrors.Clear();
        HotkeyErrors["Picture"] = hotkeys.Register(Settings.HotkeyPicture, () => _ = CapturePicture());
        HotkeyErrors["Video"] = hotkeys.Register(Settings.HotkeyVideo, () => _ = ToggleRecording());
        HotkeyErrors["Pause"] = hotkeys.Register(Settings.HotkeyPause, () => _ = TogglePause());
        HotkeyErrors["Output"] = hotkeys.Register(Settings.HotkeyToggleOutput, ToggleOutput);
        foreach (var (k, v) in HotkeyErrors.Where(x => x.Value != null))
            Log.Error($"hotkey {k}: {v}");
    }

    // ---------- menu ----------

    void BuildMenu()
    {
        var m = tray.ContextMenuStrip!;
        m.Items.Clear();
        m.ShowItemToolTips = true;
        ThemedMenu.Apply(m);
        bool rec = recorder != null;
        var mode = Settings.Mode;
        var delaySuffix = Settings.DelaySeconds > 0 ? $" (after {Settings.DelaySeconds} s)" : "";

        // What a friend sent and nobody looked at yet comes first.
        if (unseen is { } u)
        {
            m.Items.Add(MenuEntry($"New from {u.Friend}: {u.What} – show", ShowUnseen, tip: "Opens the direct window at it"));
            m.Items.Add(new WinForms.ToolStripSeparator());
        }
        // Top: what to do now – a countdown or a recording first, then the actions of the mode chosen below.
        if (countdownCts != null) m.Items.Add(MenuEntry("Cancel the countdown", () => countdownCts?.Cancel()));
        if (rec)
        {
            m.Items.Add(MenuEntry("Stop recording" + (recordingFor != null ? $" (it goes to {recordingFor})" : ""), () => _ = StopRecording(), Settings.HotkeyVideo));
            m.Items.Add(MenuEntry(recorder!.Paused ? "Resume recording" : "Pause recording", () => _ = TogglePause(), Settings.HotkeyPause));
            m.Items.Add(MenuEntry("Cancel recording", () => _ = CancelRecording()));
            if (mode != TrayMode.Video) m.Items.Add(new WinForms.ToolStripSeparator());
        }
        switch (mode)
        {
            case TrayMode.Video:
                if (!rec) m.Items.Add(MenuEntry("Record video" + delaySuffix, () => _ = ToggleRecording(), Settings.HotkeyVideo));
                m.Items.Add(MenuEntry("Show mouse pointer", () => UpdateSettings(s => s.RecordCursor = !s.RecordCursor), check: Settings.RecordCursor, enabled: !rec));
                m.Items.Add(MenuEntry("Record sound", () => UpdateSettings(s => s.RecordSound = !s.RecordSound), check: Settings.RecordSound, enabled: !rec));
                m.Items.Add(DirectMenu(rec, mode));
                m.Items.Add(OutputMenu("video"));
                break;
            case TrayMode.Files:
                m.Items.Add(MenuEntry("Upload files…", () => _ = ChooseAndUploadFiles(), enabled: uploads == 0,
                    tip: "Each file gets its own secret link; the links are copied."));
                m.Items.Add(DirectMenu(rec, mode));
                break;
            case TrayMode.Folders:
                m.Items.Add(MenuEntry("Upload a folder…", () => _ = ChooseAndUploadFolder(), enabled: uploads == 0,
                    tip: "The folder and its subfolders get one secret link, which is copied."));
                m.Items.Add(DirectMenu(rec, mode));
                break;
            default:
                m.Items.Add(MenuEntry("Take picture" + delaySuffix, () => _ = CapturePicture(), Settings.HotkeyPicture, enabled: !rec));
                if (!Settings.AnnotateAfterPicture)
                    m.Items.Add(MenuEntry("Take picture and draw on it" + delaySuffix, () => _ = CapturePicture(annotate: true), enabled: !rec));
                m.Items.Add(DirectMenu(rec, mode));
                m.Items.Add(OutputMenu("picture"));
                break;
        }

        // The modes: what the top shows and what a left click on the icon does. Choosing one keeps the menu open.
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(new WinForms.ToolStripLabel("Mode"));
        void AddMode(string text, TrayMode which, string tip)
        {
            var item = MenuEntry("    " + text, () =>
            {
                if (Settings.Mode != which) UpdateSettings(s => s.Mode = which);
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(RebuildOpenMenu));
            }, check: mode == which, tip: tip);
            item.Tag = which; // keeps the menu open (ItemClicked above)
            m.Items.Add(item);
        }
        AddMode("Pictures", TrayMode.Picture, "The menu shows the picture actions; a left click on the icon takes a picture.");
        AddMode("Videos", TrayMode.Video, "The menu shows the video actions; a left click on the icon starts or stops a video.");
        AddMode("Files", TrayMode.Files, "The menu shows the file actions; a left click on the icon asks which files to upload.");
        AddMode("Folders", TrayMode.Folders, "The menu shows the folder actions; a left click on the icon asks which folder to upload.");

        // Bottom: the windows and settings.
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(MenuEntry("History…", () => ShowHistory()));
        if (lastLink != null) m.Items.Add(MenuEntry("Copy last link", () => ClipboardService.SetText(lastLink)));
        m.Items.Add(MenuEntry("Settings…", ShowSettings));
        if (TrayMenuLogic.ShowsDelay(mode)) m.Items.Add(DelayMenu());
        var other = new List<WinForms.ToolStripItem> { FriendMenu(), MenuEntry("Open captures folder", () => Open(AppPaths.CacheDir)) };
        if (Settings.ShowUserNotes) other.Add(MenuEntry("User notes…", ShowNotes));
        other.Add(MenuEntry("Help", () => ShowHelp("")));
        m.Items.Add(SubMenu("Other", other.ToArray()));
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(MenuEntry("Quit", () => System.Windows.Application.Current.Shutdown()));
        m.Items.Add(new WinForms.ToolStripSeparator());
        // Clicking it closes the menu like any entry – without doing anything.
        m.Items.Add(MenuEntry("Cancel", () => { }, tip: "Closes this menu (Esc or a click elsewhere does the same)"));
    }

    static WinForms.ToolStripMenuItem MenuEntry(string text, Action a, string? keys = null, bool check = false, bool enabled = true, string? tip = null)
    {
        var i = new WinForms.ToolStripMenuItem(text) { Checked = check, Enabled = enabled, ShortcutKeyDisplayString = keys, ToolTipText = tip };
        i.Click += (_, _) => a();
        return i;
    }

    static WinForms.ToolStripMenuItem SubMenu(string text, params WinForms.ToolStripItem[] items)
    {
        var s = new WinForms.ToolStripMenuItem(text);
        s.DropDownItems.AddRange(items);
        ThemedMenu.Apply(s.DropDown as WinForms.ToolStripDropDownMenu ?? new WinForms.ToolStripDropDownMenu());
        return s;
    }

    /// <summary>After a picture / video ▸ : the three output choices (one setting for pictures and videos).</summary>
    WinForms.ToolStripMenuItem OutputMenu(string what)
    {
        var items = new[] { OutputMode.UploadAndMedia, OutputMode.SecretLink, OutputMode.DirectMedia }
            .Select(o => MenuEntry(OutputLogic.Describe(o, what), () => UpdateSettings(s => s.Output = o), check: Settings.Output == o))
            .ToArray();
        var menu = SubMenu($"After a {what}: {OutputLogic.Short(Settings.Output)}", items);
        if (Settings.HotkeyToggleOutput.Length > 0) menu.ToolTipText = $"{Settings.HotkeyToggleOutput} goes round these choices.";
        return menu;
    }

    /// <summary>Direct to a friend ▸ : the one action that fits the mode – a picture, a video, files or a folder straight
    /// to a friend's screen – for the friend chosen in Other → Friend (or the only one); with several friends and none
    /// chosen, a submenu of friends. In Pictures also the "draw first" switch; always the direct window.</summary>
    WinForms.ToolStripMenuItem DirectMenu(bool rec, TrayMode mode)
    {
        var items = new List<WinForms.ToolStripItem>();
        var friends = direct.Friends;
        var chosen = DirectLogic.ChosenFriend(friends, Settings.DirectFriend);
        bool enabled = !rec || TrayMenuLogic.DirectAllowedWhileRecording(mode);
        string? tip = mode switch
        {
            TrayMode.Video => "Record as usual; when you stop, the video goes straight to them.",
            TrayMode.Folders => "A folder travels as one ZIP file; your friend opens it as a folder with one click.",
            _ => null,
        };
        void Run(string who)
        {
            switch (mode)
            {
                case TrayMode.Video: _ = StartRecording(toFriend: who); break;
                case TrayMode.Files: _ = ChooseAndSend(who, folder: false); break;
                case TrayMode.Folders: _ = ChooseAndSend(who, folder: true); break;
                default: _ = CapturePicture(toFriend: who, annotate: Settings.DirectDrawFirst); break;
            }
        }
        if (chosen != null)
            items.Add(MenuEntry(TrayMenuLogic.DirectEntry(mode, chosen, Settings.DirectDrawFirst), () => Run(chosen), enabled: enabled, tip: tip));
        else if (friends.Count > 0)
        {
            var (verb, after) = TrayMenuLogic.DirectAction(mode);
            var each = friends.Select(f => (WinForms.ToolStripItem)MenuEntry(f + after, () => Run(f), enabled: enabled)).ToArray();
            var sub = SubMenu(verb, each);
            sub.ToolTipText = (tip != null ? tip + " " : "") + "Choose a friend in Other → Friend to skip this list.";
            items.Add(sub);
        }
        if (TrayMenuLogic.ShowsDrawSwitch(mode, friends.Count))
        {
            items.Add(new WinForms.ToolStripSeparator());
            items.Add(MenuEntry("Draw on pictures before sending them", () => UpdateSettings(s => s.DirectDrawFirst = !s.DirectDrawFirst),
                check: Settings.DirectDrawFirst, tip: "The drawing editor opens with the picture; Done sends it."));
        }
        if (items.Count > 0) items.Add(new WinForms.ToolStripSeparator());
        items.Add(MenuEntry(friends.Count == 0 ? "Add a friend…" : "Open the direct window", () => direct.ShowWindow(null, null)));
        return SubMenu("Direct to a friend", items.ToArray());
    }

    /// <summary>Other → Friend ▸ : whom Direct to a friend acts for – then each action is one click, without a list of
    /// friends.</summary>
    WinForms.ToolStripMenuItem FriendMenu()
    {
        var friends = direct.Friends;
        var chosen = DirectLogic.ChosenFriend(friends, Settings.DirectFriend);
        var items = new List<WinForms.ToolStripItem>();
        foreach (var f in friends)
        {
            var who = f;
            items.Add(MenuEntry(who, () => UpdateSettings(s => s.DirectFriend = who), check: string.Equals(who, chosen, StringComparison.OrdinalIgnoreCase)));
        }
        if (friends.Count > 1)
            items.Add(MenuEntry("Ask each time", () => UpdateSettings(s => s.DirectFriend = ""), check: chosen == null,
                tip: "Direct to a friend shows the list of friends for each action"));
        if (items.Count > 0) items.Add(new WinForms.ToolStripSeparator());
        items.Add(MenuEntry("Add a friend…", () => direct.ShowWindow(null, null, friendsPanel: true)));
        var menu = SubMenu(chosen != null ? $"Friend: {chosen}" : "Friend", items.ToArray());
        menu.ToolTipText = "Whom Direct to a friend sends to";
        return menu;
    }

    async Task ChooseAndSend(string friend, bool folder)
    {
        List<string> paths;
        if (folder)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder for {friend} (it travels as one ZIP file)", Multiselect = true };
            if (d.ShowDialog() != true) return;
            paths = [.. d.FolderNames];
        }
        else
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = $"Files for {friend}", Multiselect = true };
            if (d.ShowDialog() != true) return;
            paths = [.. d.FileNames];
        }
        await SendPathsAsync(friend, paths);
    }

    /// <summary>Files and folders straight to a friend (tray menu, Explorer): a folder travels as one ZIP file.</summary>
    public async Task SendPathsAsync(string friend, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        if (files.Count + folders.Count == 0) return;
        uploads++;
        tray.Icon = iconBusy;
        var packed = new List<string>();
        var ui = System.Windows.Application.Current.Dispatcher;
        try
        {
            foreach (var f in folders)
            {
                var name = Path.GetFileName(f.TrimEnd('\\'));
                Notify(ToastKind.Busy, $"Packing {name} for {friend}…", "A folder travels as one ZIP file.");
                int shown = -10;
                packed.Add(await Task.Run(() => FolderPack.Pack(f, Path.Combine(AppPaths.WorkDir, "direct-folders"), pc =>
                {
                    if (pc < shown + 10) return;
                    shown = pc;
                    ui.BeginInvoke(() => Notify(ToastKind.Busy, $"Packing {name} for {friend}…", "A folder travels as one ZIP file.", percent: pc));
                })));
            }
            var all = files.Concat(packed).ToList();
            var what = all.Count == 1 ? Path.GetFileName(all[0]) : $"{all.Count} files";
            Notify(ToastKind.Busy, $"Sending {what} to {friend}…", "");
            var sent = await direct.SendAsync(friend, all, t => Notify(ToastKind.Busy, t, ""));
            UpdateSettings(s => s.DirectLastFriend = friend);
            Notify(ToastKind.Ok, $"Sent to {friend}", (sent.Count == 1 ? sent[0].Name : $"{sent.Count} files") + $" – {friend} sees {(sent.Count == 1 ? "it" : "them")} now.",
                extra: ("Show", () => direct.ShowWindow(friend, sent.LastOrDefault()?.Path)));
        }
        catch (Exception e) when (e is DirectException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("send to " + friend, e);
            Notify(ToastKind.Error, $"Not sent to {friend}", e.Message, extra: ("Try again", () => _ = SendPathsAsync(friend, paths)));
        }
        finally
        {
            foreach (var z in packed)
                try { File.Delete(z); }
                catch { }
            uploads--;
            tray.Icon = recorder != null ? iconRec : uploads > 0 ? iconBusy : IdleIcon;
            UpdateTip();
        }
    }

    // ---------- Explorer's right-click menu ----------

    readonly List<(string Command, string Path)> fromExplorer = [];
    System.Windows.Threading.DispatcherTimer? explorerBatch;

    /// <summary>Explorer starts one copy per selected item: what arrives within a moment is asked about together.</summary>
    void FromExplorer(string command, string path)
    {
        fromExplorer.Add((command, path));
        if (explorerBatch == null)
        {
            explorerBatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            explorerBatch.Tick += async (_, _) =>
            {
                explorerBatch.Stop();
                var items = fromExplorer.ToList();
                fromExplorer.Clear();
                foreach (var g in items.GroupBy(i => i.Command))
                    await ConfirmShare(g.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), send: g.Key == ExplorerCommand.Send);
            };
        }
        explorerBatch.Stop();
        explorerBatch.Start();
    }

    /// <summary>Asks first (what, how much, to whom), then uploads or sends.</summary>
    async Task ConfirmShare(List<string> paths, bool send)
    {
        Log.Info($"explorer: {(send ? "send" : "upload")} {paths.Count} item(s)");
        var w = new ShareConfirmWindow(paths, send, direct.Friends, DirectLogic.ChosenFriend(direct.Friends, Settings.DirectFriend) ?? Settings.DirectLastFriend,
            Settings.PeergosConfigured, Settings.DirectDrawFirst);
        w.ShowDialog();
        if (w.OpenDirect) { direct.ShowWindow(null, null); return; }
        if (w.OpenSettings) { ShowSettings(0); return; }
        if (!w.Confirmed) { Log.Info("explorer: cancelled"); return; }
        if (send && w.Friend is { } friend)
        {
            var rest = paths.ToList();
            if (w.CanDraw && w.DrawFirst != Settings.DirectDrawFirst) UpdateSettings(s => s.DirectDrawFirst = w.DrawFirst);
            if (w.DrawFirst)
            {
                // Each picture opens in the drawing editor first ("Send to …" sends the drawn copy); a file that only
                // looks like a picture (a wrong extension) goes as it is.
                var notPictures = new List<string>();
                foreach (var p in paths.Where(p => File.Exists(p) && FileKinds.Of(p) == FileKind.Picture).ToList())
                {
                    if (!AnnotateWindow.CanOpen(p)) { notPictures.Add(Path.GetFileName(p)); continue; }
                    rest.Remove(p);
                    try { await DrawAndSendAsync(p, friend, Path.GetFileNameWithoutExtension(p)); }
                    catch (Exception e)
                    {
                        Log.Error("explorer: draw " + p, e);
                        Notify(ToastKind.Warn, "The drawing editor failed", $"{e.Message}\n{Path.GetFileName(p)} was not sent.");
                    }
                }
                if (notPictures.Count > 0)
                    Notify(ToastKind.Warn, "Not drawn on", $"{string.Join(", ", notPictures)} could not be opened as a picture – sent as it is.");
            }
            if (rest.Count > 0) await SendPathsAsync(friend, rest);
            return;
        }
        var files = paths.Where(File.Exists).ToList();
        if (files.Count > 0) await UploadFilesAsync(files);
        foreach (var d in paths.Where(Directory.Exists)) await UploadFolderAsync(d);
    }

    /// <summary>Delay ▸ : quick choices here, any length in Settings.</summary>
    WinForms.ToolStripMenuItem DelayMenu()
    {
        var items = new List<WinForms.ToolStripItem>();
        void D(int sec, string text) => items.Add(MenuEntry(text, () => UpdateSettings(x => x.DelaySeconds = sec), check: Settings.DelaySeconds == sec));
        D(0, "No delay");
        for (int sec = 1; sec <= 5; sec++) D(sec, sec == 1 ? "1 second" : $"{sec} seconds");
        if (Settings.DelaySeconds > 5) D(Settings.DelaySeconds, $"{Settings.DelaySeconds} seconds (from Settings)");
        items.Add(new WinForms.ToolStripSeparator());
        items.Add(MenuEntry("Other length…", () => ShowSettings(1)));
        return SubMenu(Settings.DelaySeconds > 0 ? $"Delay: {Settings.DelaySeconds} s" : "Delay: none", items.ToArray());
    }

    /// <summary>After a mode was chosen, the open menu shows that mode's actions. It keeps the edges that touch the
    /// pointer where they were (the tray menu usually opens above and to the left of it).</summary>
    void RebuildOpenMenu()
    {
        var m = tray.ContextMenuStrip!;
        if (!m.Visible) return;
        var before = m.Bounds;
        bool above = before.Bottom <= menuOpenedAt.Y + 4, left = before.Right <= menuOpenedAt.X + 4;
        BuildMenu();
        var size = m.GetPreferredSize(Size.Empty);
        var area = WinForms.Screen.FromPoint(menuOpenedAt).WorkingArea;
        int x = left ? before.Right - size.Width : before.X;
        int y = above ? before.Bottom - size.Height : before.Y;
        x = Math.Max(area.Left, Math.Min(x, area.Right - size.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - size.Height));
        m.Bounds = new Rectangle(x, y, size.Width, size.Height);
    }

    public void ShowSettings() => ShowSettings(-1);

    /// <summary>Opens Settings, optionally at a page (index in the left column).</summary>
    public void ShowSettings(int page)
    {
        if (settingsWindow is { IsLoaded: true })
        {
            if (page >= 0) settingsWindow.Tabs.SelectedIndex = page;
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(this, Math.Max(0, page));
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    public void ShowHistory() => ShowHistory(null);

    /// <summary>Opens the history; with a record id (the History button of a capture's card) that entry is selected and
    /// scrolled to, and a filter or search that hides it is cleared.</summary>
    public void ShowHistory(string? recordId)
    {
        if (historyWindow is { IsLoaded: true })
        {
            if (historyWindow.WindowState == System.Windows.WindowState.Minimized) historyWindow.WindowState = System.Windows.WindowState.Normal;
            historyWindow.Activate();
        }
        else
        {
            historyWindow = new HistoryWindow(this);
            historyWindow.Show();
            historyWindow.Activate();
        }
        if (recordId != null) historyWindow.Reveal(recordId);
    }

    public void ShowNotes()
    {
        notesWindow ??= new NotesWindow(() => Settings);
        notesWindow.ShowNotes();
    }

    public bool NotesBusy => notesWindow?.RecentlyUsed ?? false;

    /// <summary>Opens the tray menu at the mouse (also for keyboard users: "PeergosSnap.exe --menu").</summary>
    void ShowMenu()
    {
        var m = tray.ContextMenuStrip!;
        var p = WinForms.Cursor.Position;
        m.Show(p); // builds the menu as it opens
    }

    public void ShowHelp(string section)
    {
        helpWindow ??= new HelpWindow();
        helpWindow.ShowHelp(section);
    }

    void ToggleOutput()
    {
        UpdateSettings(s => s.Output = OutputLogic.Next(s.Output));
        Notify(ToastKind.Ok, "After a capture: " + OutputLogic.Describe(Settings.Output, "picture or video").ToLowerInvariant(),
            "Changed with the hotkey. Press it again for the next choice.");
    }

    // ---------- commands ----------

    public void Command(string cmd)
    {
        switch (cmd)
        {
            case "--picture": _ = CapturePicture(); break;
            case "--video": _ = ToggleRecording(); break;
            case "--settings": ShowSettings(); break;
            case "--notes": ShowNotes(); break;
            case "--help": ShowHelp(""); break;
            case "--menu": ShowMenu(); break;
            case "--history": ShowHistory(); break;
            case "--direct": direct.ShowWindow(null, null); break;
            case "--upload-files": _ = ChooseAndUploadFiles(); break;
            case "--upload-folder": _ = ChooseAndUploadFolder(); break;
            case "--quit": System.Windows.Application.Current.Shutdown(); break;
            default:
                if (ExplorerCommand.Parse(cmd) is { } ex) FromExplorer(ex.Command, ex.Path);
                break;
        }
    }

    async Task PrimaryClick()
    {
        if (countdownCts != null) { countdownCts.Cancel(); return; }
        if (recorder != null) await StopRecording();
        else if (unseen != null) ShowUnseen(); // the dot on the icon: a click shows what came
        else if (Settings.Mode == TrayMode.Video) await StartRecording();
        else if (Settings.Mode == TrayMode.Files) await ChooseAndUploadFiles();
        else if (Settings.Mode == TrayMode.Folders) await ChooseAndUploadFolder();
        else await CapturePicture();
    }

    async Task<PxRect?> Select(CaptureKind kind, System.Drawing.Bitmap? frozen = null, IReadOnlyList<Native.WinInfo>? windows = null)
    {
        if (selecting) return null;
        selecting = true;
        try
        {
            var o = new SelectionOverlay(Settings, kind, frozen, windows);
            o.Show();
            return await o.Result;
        }
        finally { selecting = false; }
    }

    /// <summary>Takes a picture; with <paramref name="toFriend"/> it goes straight to that friend (direct mode). With
    /// <paramref name="annotate"/> (or Settings → Capture → draw after every picture) the drawing editor opens first.</summary>
    public async Task CapturePicture(string? toFriend = null, bool annotate = false)
    {
        if (countdownCts != null) { countdownCts.Cancel(); return; } // pressing again cancels the countdown
        if (recorder != null || selecting) return;
        System.Drawing.Bitmap? frozen = null;
        IReadOnlyList<Native.WinInfo>? windows = null;
        var screen = Native.VirtualScreen();
        try
        {
            if (Settings.DelaySeconds > 0)
            {
                // Countdown, then freeze the whole screen: menus opened meanwhile stay in the picture.
                if (!await Countdown(Settings.DelaySeconds, "Picture in",
                        "Open the menu or window you want now. Then the screen freezes and you select the area.")) return;
                windows = Native.VisibleWindows();
                Native.DwmFlush();
                screen = Native.VirtualScreen();
                frozen = ScreenCapture.Grab(screen);
            }
            var r = await Select(CaptureKind.Picture, frozen, windows);
            if (r is not { } rect) return;
            var source = Settings.RememberApp
                ? Native.Describe(Native.WindowInfoAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, windows))
                : (null, null);
            if (frozen == null)
            {
                if (Settings.CaptureDelayMs > 0) await Task.Delay(Settings.CaptureDelayMs);
                // Let the compositor draw a frame without the overlay before copying the screen.
                Native.DwmFlush();
                Native.DwmFlush();
            }
            var now = DateTime.Now;
            var folder = CaptureFiles.Folder(AppPaths.CacheDir, now, Settings.Subfolders);
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, FileNames.Unique(folder, FileNames.ForCapture(now, Settings.ImageFormat)));
            using (var bmp = frozen != null ? ScreenCapture.Crop(frozen, screen, rect) : ScreenCapture.Grab(rect))
            {
                if (Settings.ShutterSound) ShutterSound.Play(); // Settings → Capture; it does not wait for the sound
                ScreenCapture.Save(bmp, file, Settings.ImageFormat);
            }
            Log.Info($"picture {rect}{(frozen != null ? " (frozen)" : "")} -> {file}");
            var record = history.Add(new HistoryRecord
            {
                Created = now, Kind = "picture", File = file, Width = rect.Width, Height = rect.Height,
                Bytes = new FileInfo(file).Length, App = source.App, WindowTitle = source.Title,
            });
            if ((annotate || Settings.AnnotateAfterPicture) && !DrawOnCapture(file, record, toFriend))
            {
                // "Draw first" for a friend, closed with "Don't send": nothing leaves the PC.
                Notify(ToastKind.Ok, $"Not sent to {toFriend}", "The picture is in your captures and the history.", null, file,
                    history: () => ShowHistory(record.Id));
                return;
            }
            if (toFriend != null)
            {
                await SendDirect(toFriend, file, record);
                return;
            }
            if (Settings.AskBeforePictureUpload)
            {
                // Settings → Capture: decide for each picture, as after a recording (upload, copy, save as, discard).
                FinishChoice choice;
                while (true)
                {
                    var dlg = new FinishRecordingDialog(file, TimeSpan.Zero, Settings, video: false);
                    dlg.ShowDialog();
                    choice = dlg.Choice;
                    if (choice != FinishChoice.Annotate) break;
                    DrawOnCapture(file, record); // then the same choices again, with the drawing in the preview
                }
                Log.Info("picture " + file + " -> " + choice);
                await Finish(file, record, choice);
                return;
            }
            await Deliver(file, Settings.Output, record, fresh: true);
        }
        catch (Exception e)
        {
            Log.Error("picture", e);
            Notify(ToastKind.Error, "Capture failed", e.Message);
        }
        finally { frozen?.Dispose(); }
    }

    /// <summary>The drawing editor on a fresh capture (not shared yet): "Done" writes the drawing into the file,
    /// "Skip" leaves it as taken. For a friend (<paramref name="sendTo"/>, "Draw first") it returns false when the
    /// picture must not be sent (Don't send, or closed).</summary>
    bool DrawOnCapture(string file, HistoryRecord record, string? sendTo = null)
    {
        try
        {
            var w = new AnnotateWindow(file, AnnotateMode.Capture, this, sendTo);
            w.ShowDialog();
            if (w.Aborted) return false;
            if (w.Outcome == AnnotateOutcome.Cancelled) return true;
            record.Bytes = new FileInfo(file).Length;
            history.Save();
            Log.Info("picture drawn on: " + file);
        }
        catch (Exception e)
        {
            Log.Error("annotate", e);
            Notify(ToastKind.Warn, "The drawing editor failed", e.Message + "\nThe picture is used as it was taken.");
        }
        return true;
    }

    /// <summary>
    /// The drawing editor on a picture of the direct window (one received or sent): the drawn copy becomes a new
    /// capture in the history, and "Send to …" sends it to that friend. The picture itself stays as it is.
    /// </summary>
    public async Task DrawAndSendAsync(string picture, string friend, string title)
    {
        var w = new AnnotateWindow(picture, AnnotateMode.Send, this, friend);
        w.ShowDialog();
        if (w.Outcome == AnnotateOutcome.Cancelled || w.SavedFile == null) return;
        var record = history.Add(new HistoryRecord
        {
            Created = DateTime.Now, Kind = "picture", File = w.SavedFile, Width = w.SavedWidth, Height = w.SavedHeight,
            Bytes = new FileInfo(w.SavedFile).Length, Label = title + " – drawn on",
        });
        Log.Info($"drawn copy for {friend}: {w.SavedFile} ({w.Outcome})");
        if (w.Outcome == AnnotateOutcome.SavedSend) await SendDirect(friend, w.SavedFile, record);
        else Notify(ToastKind.Ok, "Drawn picture saved", "It is a new entry in the history; nothing was sent.", null, w.SavedFile,
            history: () => ShowHistory(record.Id));
    }

    /// <summary>
    /// The drawing editor on a capture from the history: the original stays as it is; the drawn picture becomes a new
    /// capture (in the captures folder and the history), and is then uploaded or copied when that was chosen.
    /// </summary>
    public async Task<HistoryRecord?> DrawOnRecordAsync(HistoryRecord original)
    {
        if (original.File == null || !File.Exists(original.File)) return null;
        var w = new AnnotateWindow(original.File, AnnotateMode.Copy, this);
        w.ShowDialog();
        if (w.Outcome == AnnotateOutcome.Cancelled || w.SavedFile == null) return null;
        var now = DateTime.Now;
        var record = history.Add(new HistoryRecord
        {
            Created = now, Kind = "picture", File = w.SavedFile, Width = w.SavedWidth, Height = w.SavedHeight,
            Bytes = new FileInfo(w.SavedFile).Length, App = original.App, WindowTitle = original.WindowTitle,
            Label = original.Title + " – drawn on",
        });
        Log.Info($"drawn copy of {original.File} -> {w.SavedFile} ({w.Outcome})");
        switch (w.Outcome)
        {
            case AnnotateOutcome.SavedUpload:
                await Deliver(w.SavedFile, Settings.Output == OutputMode.UploadAndMedia ? OutputMode.UploadAndMedia : OutputMode.SecretLink, record);
                break;
            case AnnotateOutcome.SavedCopy: await Deliver(w.SavedFile, OutputMode.DirectMedia, record); break;
            default:
                record.MirrorFile = Mirror(w.SavedFile, now);
                history.Save();
                Notify(ToastKind.Ok, "Drawn picture saved", "It is a new entry in the history; the original is unchanged.", null, w.SavedFile,
                    history: () => ShowHistory(record.Id));
                break;
        }
        return record;
    }

    // ---------- uploading files and folders (tray menu) ----------

    async Task ChooseAndUploadFiles()
    {
        if (!SignedInForUpload()) return;
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Files to upload to Peergos (each gets its own secret link)" };
        if (d.ShowDialog() != true || d.FileNames.Length == 0) return;
        await UploadFilesAsync(d.FileNames);
    }

    async Task ChooseAndUploadFolder()
    {
        if (!SignedInForUpload()) return;
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to upload to Peergos (one secret link for the whole folder)" };
        if (d.ShowDialog() != true || string.IsNullOrEmpty(d.FolderName)) return;
        await UploadFolderAsync(d.FolderName);
    }

    bool SignedInForUpload()
    {
        if (Settings.PeergosConfigured) return true;
        Notify(ToastKind.Warn, "Sign in to Peergos first", "Uploading files needs your Peergos account: Settings → Peergos.", extra: ("Sign in", ShowSettings));
        return false;
    }

    /// <summary>Uploads files into the Peergos capture folder; each gets its own secret link (all links are copied).</summary>
    public async Task UploadFilesAsync(IReadOnlyList<string> files)
    {
        var list = files.Where(File.Exists).Select(f => (f, Path.GetFileName(f))).ToList();
        if (list.Count == 0) return;
        var what = list.Count == 1 ? Path.GetFileName(list[0].f) : $"{list.Count} files";
        var put = await PutAsync(list, null, what, () => UploadFilesAsync(files));
        if (put == null) return;
        var links = new List<string>();
        foreach (var f in put.Files)
        {
            history.Add(new HistoryRecord
            {
                Kind = "file", Source = f.Local, PeergosPath = f.Path, Link = f.Link, Bytes = f.Size, Uploaded = DateTime.Now,
            });
            if (f.Link != null) links.Add(f.Link);
        }
        UploadDone(put, links, put.Files.Count == 1 ? "File" : $"{put.Files.Count} files", list.Count, list.Count == 1 ? list[0].f : null);
    }

    /// <summary>Uploads a folder (with its subfolders) as a new folder in the Peergos capture folder, with one secret
    /// link to the whole folder.</summary>
    public async Task UploadFolderAsync(string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
        List<string> files;
        try { files = UploadLogic.FolderFiles(root); }
        catch (Exception e) { Notify(ToastKind.Error, "The folder cannot be read", e.Message); return; }
        if (files.Count == 0) { Notify(ToastKind.Warn, "Nothing to upload", "The folder has no files."); return; }
        long bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        if (UploadLogic.NeedsConfirm(files.Count, bytes)
            && System.Windows.MessageBox.Show($"Upload {files.Count} files ({HistoryLogic.Size(bytes)}) from {root} to Peergos?",
                "Peergos Snap", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            return;
        var name = Path.GetFileName(root);
        var list = files.Select(f => (f, Path.GetRelativePath(root, f))).ToList();
        var put = await PutAsync(list, name.Length > 0 ? name : "Folder", $"the folder {name}", () => UploadFolderAsync(folder));
        if (put == null) return;
        if (put.Folder != null)
            history.Add(new HistoryRecord
            {
                Kind = "folder", Source = root, PeergosPath = put.Folder, Link = put.FolderLink,
                Bytes = put.Files.Sum(f => f.Size), Uploaded = DateTime.Now,
            });
        UploadDone(put, put.FolderLink != null ? [put.FolderLink] : [], $"Folder {name}", files.Count, null);
    }

    async Task<PutResult?> PutAsync(List<(string, string)> files, string? folderName, string what, Func<Task> again)
    {
        uploads++;
        tray.Icon = recorder == null ? iconBusy : tray.Icon;
        tray.Text = "Peergos Snap – uploading…";
        Notify(ToastKind.Busy, $"Uploading {what} to Peergos…", files.Count == 1 ? Path.GetFileName(files[0].Item1) : $"{files.Count} files");
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            var (r, put) = await Uploader.PutAsync(Settings.Clone(), files, folderName, pct => dispatcher.BeginInvoke(() =>
            {
                tray.Text = $"Peergos Snap – uploading {pct}%";
                Notify(ToastKind.Busy, $"Uploading {what} to Peergos… {pct}%", files.Count == 1 ? Path.GetFileName(files[0].Item1) : $"{files.Count} files", percent: pct);
            }));
            if (put.Files.Count == 0)
            {
                var why = r.Error ?? put.Failed.FirstOrDefault() ?? "Unknown error";
                (string, Action)? signIn = null;
                if (Uploader.NeedsSignIn(why)) { UpdateSettings(s => s.Session = ""); signIn = ("Sign in", ShowSettings); }
                Notify(ToastKind.Error, "Upload failed – nothing was uploaded", why, extra: signIn ?? ("Try again", () => _ = again()));
                return null;
            }
            return put;
        }
        catch (Exception e)
        {
            Log.Error("upload files", e);
            Notify(ToastKind.Error, "Upload failed", e.Message, extra: ("Try again", () => _ = again()));
            return null;
        }
        finally
        {
            uploads--;
            if (recorder == null) tray.Icon = uploads > 0 ? iconBusy : IdleIcon;
            UpdateTip();
        }
    }

    void UploadDone(PutResult put, List<string> links, string what, int asked, string? file)
    {
        string copied = "";
        bool onClipboard = false;
        if (links.Count > 0)
        {
            lastLink = links[^1]; // "Copy last link" offers it even when the clipboard was busy
            try
            {
                ClipboardService.OnUi(() => ClipboardService.SetText(string.Join(Environment.NewLine, links)));
                onClipboard = true;
                copied = links.Count == 1 ? " The link is on the clipboard." : $" {links.Count} links are on the clipboard, one per line.";
            }
            catch (Exception e) { Log.Error("clipboard links", e); copied = " The clipboard was busy: find the links in History…"; }
        }
        if (put.Failed.Count > 0)
            Notify(ToastKind.Warn, $"{put.Files.Count} of {asked} files uploaded", $"Not uploaded: {put.Failed[0]}" + (put.Failed.Count > 1 ? $" (and {put.Failed.Count - 1} more)" : "") + "." + copied,
                links.Count == 1 ? links[0] : null, extra: ("History", ShowHistory));
        else
            Notify(ToastKind.Ok, links.Count == 1 && onClipboard ? "Link copied to the clipboard" : $"{what} uploaded", $"{what} uploaded to Peergos.{copied}",
                links.Count == 1 ? links[0] : null, file, extra: ("History", ShowHistory));
    }

    /// <summary>The delay countdown: a card at the bottom right and the seconds in the tray icon. False = cancelled
    /// (Cancel on the card, the tray icon or the same hotkey again).</summary>
    async Task<bool> Countdown(int seconds, string what, string hint)
    {
        var cts = countdownCts = new CancellationTokenSource();
        var card = new CountdownWindow(hint);
        card.CancelClicked += () => cts.Cancel();
        try
        {
            card.SetSeconds(seconds);
            card.Show();
            for (int left = seconds; left > 0; left--)
            {
                card.SetSeconds(left);
                tray.Icon = NumberIcon(left);
                tray.Text = $"Peergos Snap – {what} {left} s (click to cancel)";
                await Task.Delay(1000, cts.Token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            Notify(ToastKind.Ok, "Countdown cancelled", "Nothing was captured.");
            return false;
        }
        finally
        {
            card.Close();
            countdownCts = null;
            tray.Icon = recorder != null ? iconRec : uploads > 0 ? iconBusy : IdleIcon;
            UpdateTip();
        }
    }

    Icon NumberIcon(int n)
    {
        if (numberIcons.TryGetValue(n, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var b = new SolidBrush(Color.FromArgb(43, 138, 110));
            g.FillEllipse(b, 0, 0, 31, 31);
            using var f = new Font("Segoe UI", n > 9 ? 13 : 17, FontStyle.Bold, GraphicsUnit.Pixel);
            var text = n.ToString();
            var size = g.MeasureString(text, f);
            g.DrawString(text, f, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return numberIcons[n] = Icon.FromHandle(bmp.GetHicon());
    }

    // ---------- recording ----------

    public async Task ToggleRecording()
    {
        if (recorder != null) await StopRecording();
        else await StartRecording();
    }

    /// <summary>Records a video; with <paramref name="toFriend"/> (Direct to a friend in Videos) it goes straight to
    /// that friend when it stops.</summary>
    public async Task StartRecording(string? toFriend = null)
    {
        if (countdownCts != null) { countdownCts.Cancel(); return; }
        if (recorder != null || stopping) return;
        var r = await Select(CaptureKind.Video);
        if (r is not { } rect) return;
        recordingSource = Settings.RememberApp
            ? Native.Describe(Native.WindowInfoAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2))
            : (null, null);
        if (Settings.DelaySeconds > 0)
        {
            // The area is marked; recording starts when the countdown ends (time to open a menu or get ready).
            frame = new RegionFrame(Geometry.EvenSize(rect), Settings.BorderColor);
            frame.Show();
            if (!await Countdown(Settings.DelaySeconds, "Recording starts in", "Get ready: the marked area is recorded when the countdown ends."))
            {
                frame?.Close();
                frame = null;
                return;
            }
        }
        if (Settings.CaptureDelayMs > 0) await Task.Delay(Settings.CaptureDelayMs);
        try
        {
            recorder = new Recorder(rect, Settings.Clone());
            recorder.Start();
            recordingFor = toFriend;
        }
        catch (Exception e)
        {
            recorder?.Cleanup();
            recorder = null;
            frame?.Close();
            frame = null;
            Log.Error("record start", e);
            Notify(ToastKind.Error, "Recording failed", e.Message);
            return;
        }
        recordingStarted = DateTime.Now;
        if (frame == null)
        {
            frame = new RegionFrame(recorder.Region, Settings.BorderColor);
            frame.Show();
        }
        controls = new RecordingControls(recorder.Region, () => recorder?.Elapsed ?? TimeSpan.Zero);
        controls.StopClicked += () => _ = StopRecording();
        controls.PauseClicked += () => _ = TogglePause();
        controls.CancelClicked += () => _ = CancelRecording();
        controls.Show();
        tray.Icon = iconRec;
        watch.Start();
        UpdateTip();
    }

    async Task WatchRecorder()
    {
        if (recorder is { HasFailed: true } && !stopping)
        {
            var err = recorder.LastError;
            await CancelRecording();
            Notify(ToastKind.Error, "Recording stopped unexpectedly", "FFmpeg stopped: " + err);
        }
    }

    public async Task TogglePause()
    {
        if (recorder == null || stopping) return;
        if (recorder.Paused) recorder.Resume();
        else await recorder.PauseAsync();
        controls?.SetPaused(recorder.Paused);
        tray.Icon = recorder.Paused ? iconPaused : iconRec;
        UpdateTip();
    }

    void CloseRecordingUi()
    {
        watch.Stop();
        controls?.Close(); controls = null;
        frame?.Close(); frame = null;
        tray.Icon = uploads > 0 ? iconBusy : IdleIcon;
    }

    public async Task CancelRecording()
    {
        if (recorder == null || stopping) return;
        stopping = true;
        recordingFor = null;
        try
        {
            CloseRecordingUi();
            await recorder.CancelAsync();
        }
        finally { recorder = null; stopping = false; UpdateTip(); }
    }

    public async Task StopRecording()
    {
        if (recorder == null || stopping) return;
        stopping = true;
        var rec = recorder;
        var forFriend = recordingFor;
        recordingFor = null;
        string? cached = null;
        try
        {
            CloseRecordingUi();
            tray.Text = "Peergos Snap – finishing the video…";
            var video = await rec.StopAsync();
            var folder = CaptureFiles.Folder(AppPaths.CacheDir, recordingStarted, Settings.Subfolders);
            Directory.CreateDirectory(folder);
            cached = Path.Combine(folder, FileNames.Unique(folder, Path.GetFileName(video)));
            File.Move(video, cached);
            var record = history.Add(new HistoryRecord
            {
                Created = recordingStarted, Kind = "video", File = cached, Width = rec.Region.Width, Height = rec.Region.Height,
                Seconds = Math.Round(rec.Elapsed.TotalSeconds, 1), Bytes = new FileInfo(cached).Length,
                App = recordingSource.App, WindowTitle = recordingSource.Title,
            });
            rec.Cleanup();
            recorder = null;
            stopping = false;
            UpdateTip();

            Log.Info("video sound: " + rec.SoundNote);
            if (forFriend != null)
            {
                // Recorded for a friend: it goes to them at once, like a picture taken for them.
                Log.Info($"video {cached} -> {forFriend}");
                await SendDirect(forFriend, cached, record);
                return;
            }
            var dlg = new FinishRecordingDialog(cached, rec.Elapsed, Settings, soundNote: Settings.RecordSound ? rec.SoundNote : "");
            dlg.ShowDialog();
            Log.Info("video " + cached + " -> " + dlg.Choice);
            await Finish(cached, record, dlg.Choice);
        }
        catch (Exception e)
        {
            Log.Error("record stop", e);
            rec.Cleanup();
            Notify(ToastKind.Error, "Recording failed", e.Message);
        }
        finally
        {
            recorder = null;
            stopping = false;
            UpdateTip();
        }
    }

    // ---------- output ----------

    /// <summary>Carries out the choice of the finish dialog (after a recording, or after a picture when asked for).</summary>
    async Task Finish(string file, HistoryRecord record, FinishChoice choice)
    {
        bool video = CaptureFiles.IsVideo(file);
        string What = video ? "Video" : "Picture", what = What.ToLowerInvariant();
        switch (choice)
        {
            case FinishChoice.Upload: await Deliver(file, OutputMode.SecretLink, record, fresh: true); break;
            case FinishChoice.UploadMedia: await Deliver(file, OutputMode.UploadAndMedia, record, fresh: true); break;
            case FinishChoice.Clipboard: await Deliver(file, OutputMode.DirectMedia, record, fresh: true); break;
            case FinishChoice.SaveAs:
                var ext = Path.GetExtension(file).TrimStart('.');
                var sfd = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(file), Filter = $"{What}|*.{ext}" };
                if (sfd.ShowDialog() == true)
                {
                    File.Copy(file, sfd.FileName, true);
                    Notify(ToastKind.Ok, $"{What} saved", sfd.FileName, null, sfd.FileName, history: () => ShowHistory(record.Id));
                }
                record.MirrorFile = Mirror(file, record.Created);
                history.Save();
                break;
            case FinishChoice.Discard:
                // Nothing was uploaded or mirrored yet. Deleted at once by default (Settings → Files & history),
                // otherwise into the Recycle Bin so a wrong click can be undone.
                bool perm = Settings.DiscardPermanently;
                if (Recycle.Remove([file], perm).Count == 0)
                {
                    history.Remove([record.Id], dismiss: false);
                    Notify(ToastKind.Ok, $"{What} discarded", perm ? $"The {what} was deleted; nothing was kept." : $"The {what} was moved to the Recycle Bin.");
                }
                else
                {
                    history.Save();
                    Notify(ToastKind.Warn, $"{What} not discarded", (perm ? "It could not be deleted" : "It could not be moved to the Recycle Bin") + " and stays in the captures folder.", null, file);
                }
                break;
            default:
                record.MirrorFile = Mirror(file, record.Created);
                history.Save();
                Notify(ToastKind.Ok, $"{What} kept on this PC only", "Nothing was uploaded or copied.", null, file, history: () => ShowHistory(record.Id));
                break;
        }
    }

    /// <summary>"Discard" on the card after a capture was delivered: the capture is removed everywhere it went –
    /// this PC (captures and mirror folder), Peergos (the link stops working), the clipboard if it still holds it,
    /// and the history.</summary>
    async Task DiscardDelivered(HistoryRecord record, string? clipboardCopy = null)
    {
        bool perm = Settings.DiscardPermanently;
        string What = record.IsVideo ? "Video" : "Picture";
        var problems = new List<string>();
        ClipboardService.ClearIfOurs(record.Link, record.File);
        if (clipboardCopy != null && clipboardCopy != record.File)
        {
            ClipboardService.ClearIfOurs(null, clipboardCopy);
            try { File.Delete(clipboardCopy); } catch { } // a temporary copy in the work folder
        }
        if (record.Link != null && lastLink == record.Link) lastLink = null;
        var files = new[] { record.File, record.MirrorFile }.Where(f => f != null && File.Exists(f)).Select(f => f!).ToList();
        if (Recycle.Remove(files, perm) is { Count: > 0 } failed)
            problems.Add((perm ? "could not delete " : "could not move to the Recycle Bin: ") + string.Join(", ", failed.Select(Path.GetFileName)));
        bool wasUploaded = record.PeergosPath != null;
        if (wasUploaded)
        {
            if (!Settings.PeergosConfigured) problems.Add("not signed in to Peergos, so it stays there");
            else
            {
                var (r, deleted, missing, f) = await Uploader.DeleteAsync(Settings.Clone(), [record.PeergosPath!]);
                if (deleted.Count + missing.Count == 0) problems.Add("Peergos: " + (f.FirstOrDefault() ?? r.Error ?? "not deleted"));
                else { record.PeergosPath = null; record.Link = null; }
            }
        }
        if (problems.Count == 0)
        {
            history.Remove([record.Id], dismiss: false);
            Notify(ToastKind.Ok, $"{What} discarded",
                (perm ? "Deleted from this PC" : "Moved to the Recycle Bin") + (wasUploaded ? " and from Peergos – the link no longer works." : "."));
        }
        else
        {
            history.Save();
            Notify(ToastKind.Warn, $"{What} not fully discarded", string.Join("\n", problems) + "\nSee History… for what is left.", extra: ("History", ShowHistory));
        }
    }

    /// <summary>A picture taken (or a video recorded) for a friend goes straight into the folder shared with them.</summary>
    async Task SendDirect(string friend, string file, HistoryRecord record)
    {
        bool video = CaptureFiles.IsVideo(file);
        string what = video ? "video" : "picture";
        uploads++;
        tray.Icon = iconBusy;
        Notify(ToastKind.Busy, $"Sending the {what} to {friend}…", Path.GetFileName(file), null, file);
        try
        {
            var sent = await direct.SendAsync(friend, [file]);
            record.Label = record.Label.Length > 0 ? record.Label : $"Sent to {friend}";
            history.Save();
            Notify(ToastKind.Ok, $"Sent to {friend}", $"{friend} sees it now.", null, file,
                extra: ("Show", () => direct.ShowWindow(friend, sent.LastOrDefault()?.Path)), history: () => ShowHistory(record.Id));
            if (!OutputLogic.KeepLocal(Settings.KeepLocalCopies, uploaded: true, uploadFailed: false)) DropLocalCopy(record, file);
        }
        catch (DirectException e)
        {
            (string, Action) again = ("Try again", () => _ = SendDirect(friend, file, record));
            if (Settings.FallbackToClipboard && TryClipboard(file, video ? "Video" : "Picture"))
                Notify(ToastKind.Warn, $"Not sent to {friend} – the {what} is on the clipboard instead", e.Message + "\nA copy is kept in the captures folder.", null, file,
                    extra: again, history: () => ShowHistory(record.Id));
            else
                Notify(ToastKind.Error, $"Not sent to {friend}", e.Message + "\nA copy is kept in the captures folder.", null, file,
                    extra: again, history: () => ShowHistory(record.Id));
        }
        finally
        {
            uploads--;
            tray.Icon = recorder != null ? iconRec : uploads > 0 ? iconBusy : IdleIcon;
            UpdateTip();
        }
    }

    /// <summary>Settings → Files & history → "Keep a copy on this PC: only if the upload fails" or "never": the copy in
    /// the captures folder is deleted once the capture is in Peergos (or, with "never", on the clipboard). The mirror
    /// folder is a separate choice and keeps its copy.</summary>
    void DropLocalCopy(HistoryRecord record, string file, bool again = true)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
            record.File = null;
            history.Save();
            Log.Info("local copy not kept (Settings → Files & history): " + file);
        }
        catch (IOException) when (again)
        {
            // Still in use for a moment (a virus scanner, a preview): once more a little later.
            var later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            later.Tick += (_, _) => { later.Stop(); DropLocalCopy(record, file, again: false); };
            later.Start();
        }
        catch (Exception e) { Log.Error("drop local copy " + file, e); }
    }

    /// <summary>Copies a capture into the mirror folder (same subfolders as the captures folder); returns the copy.</summary>
    string? Mirror(string file, DateTime taken)
    {
        if (!Settings.MirrorEnabled || string.IsNullOrWhiteSpace(Settings.MirrorFolder)) return null;
        try
        {
            var folder = CaptureFiles.Folder(Settings.MirrorFolder, taken, Settings.Subfolders);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, FileNames.Unique(folder, Path.GetFileName(file)));
            File.Copy(file, target);
            return target;
        }
        catch (Exception e)
        {
            Log.Error("mirror", e);
            Notify(ToastKind.Warn, "Could not copy to the mirror folder", e.Message);
            return null;
        }
    }

    /// <summary>Sends a finished capture where the output mode says; falls back to the clipboard if the upload fails.
    /// The notification card always says what happened: the picture or video copied (and uploaded), the link copied,
    /// or the upload failed. A fresh capture's card can discard it; every card with an entry opens it in the history.</summary>
    async Task<BridgeResult?> Deliver(string file, OutputMode mode, HistoryRecord? record = null, bool mirror = true, bool fresh = false)
    {
        string? clipFile = null;
        // A fresh capture (not one uploaded later from the history) can be discarded from its card.
        Action? discard = fresh && record != null ? () => _ = DiscardDelivered(record, clipFile) : null;
        Action? inHistory = record != null ? () => ShowHistory(record.Id) : null;
        var mirrored = mirror ? Mirror(file, record?.Created ?? DateTime.Now) : null;
        if (record != null && mirrored != null) { record.MirrorFile = mirrored; history.Save(); }
        bool video = !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        string what = video ? "Video" : "Picture", lower = what.ToLowerInvariant();
        string pasteHint = video ? "The video file is on the clipboard – paste it with Ctrl+V into a chat, mail or folder."
                                 : "The picture is on the clipboard – paste it with Ctrl+V.";
        // Settings → Files & history → Keep a copy on this PC. A capture the captures folder may not keep goes on the
        // clipboard as a temporary copy, so pasting the file still works once the copy here is gone.
        var policy = fresh && record != null ? Settings.KeepLocalCopies : LocalCopies.Always;
        string ClipFile() => clipFile ??= policy == LocalCopies.Always ? file : ClipboardCopy(file);

        if (mode == OutputMode.DirectMedia)
        {
            if (!TryClipboard(ClipFile(), what)) return null;
            if (!OutputLogic.KeepLocal(policy, uploaded: false, uploadFailed: false))
            {
                // "Never": the clipboard has it; nothing stays on this PC, not even in the history.
                var copy = clipFile;
                DropLocalCopy(record!, file);
                history.Remove([record!.Id], dismiss: false);
                Notify(ToastKind.Ok, $"{what} copied to the clipboard", pasteHint + " Nothing is kept on this PC.", discard: () =>
                {
                    ClipboardService.ClearIfOurs(null, copy);
                    try { if (copy != null) File.Delete(copy); } catch { }
                    Notify(ToastKind.Ok, $"{what} discarded", "It is no longer on the clipboard; nothing was kept.");
                });
                return null;
            }
            Notify(ToastKind.Ok, $"{what} copied to the clipboard", pasteHint, null, file, discard: discard, history: inHistory);
            return null;
        }
        // "Upload and copy the picture/video": it is on the clipboard at once and can be pasted while it uploads.
        bool mediaCopied = mode == OutputMode.UploadAndMedia && TryClipboard(ClipFile(), what);
        if (!Settings.PeergosConfigured)
        {
            if (mediaCopied || TryClipboard(ClipFile(), what))
                Notify(ToastKind.Warn, $"{what} copied to the clipboard (not uploaded)",
                    "You are not signed in to Peergos: right-click the tray icon → Settings → Peergos.", null, file,
                    extra: ("Sign in", ShowSettings), discard: discard, history: inHistory);
            return new BridgeResult(false, null, null, "Not signed in to Peergos");
        }
        uploads++;
        tray.Icon = recorder == null ? iconBusy : tray.Icon;
        tray.Text = "Peergos Snap – uploading…";
        Notify(ToastKind.Busy, $"Uploading the {lower} to Peergos…", Path.GetFileName(file), null, file);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        BridgeResult r;
        try
        {
            r = await Uploader.UploadAsync(Settings.Clone(), file, pct => dispatcher.BeginInvoke(() =>
            {
                tray.Text = $"Peergos Snap – uploading {pct}%";
                Notify(ToastKind.Busy, $"Uploading the {lower} to Peergos… {pct}%", Path.GetFileName(file), null, file, pct);
            }));
        }
        catch (Exception e) { r = new BridgeResult(false, null, null, e.Message); }
        finally
        {
            uploads--;
            if (recorder == null) tray.Icon = uploads > 0 ? iconBusy : IdleIcon;
            UpdateTip();
        }
        if (r.Ok && r.Link != null)
        {
            var link = r.Link;
            lastLink = link;
            if (record != null)
            {
                record.Link = link;
                record.PeergosPath = r.PeergosPath;
                record.Uploaded = DateTime.Now;
                history.Save();
            }
            if (mediaCopied)
                Notify(ToastKind.Ok, $"{what} copied and uploaded", $"{pasteHint} It is in Peergos too: Copy link puts its secret link on the clipboard.",
                    link, file, extra: ("Copy link", () => CopyLink(link)), discard: discard, history: inHistory);
            else
            {
                try
                {
                    ClipboardService.OnUi(() => ClipboardService.SetText(link));
                    Notify(ToastKind.Ok, "Link copied to the clipboard", $"{what} uploaded to Peergos ({r.PeergosPath}). Paste the link with Ctrl+V.",
                        link, file, discard: discard, history: inHistory);
                }
                catch (Exception e)
                {
                    Log.Error("clipboard link", e);
                    Notify(ToastKind.Warn, $"{what} uploaded, but the clipboard was busy", "Use \"Open link\" or the tray menu → Copy last link.",
                        link, file, discard: discard, history: inHistory);
                }
            }
            // The card has already shown the picture; the local copy goes now if the setting says so.
            if (!OutputLogic.KeepLocal(policy, uploaded: true, uploadFailed: false)) DropLocalCopy(record!, file);
            return r;
        }
        var why = r.Error ?? "Unknown error";
        (string, Action)? signIn = null;
        if (Uploader.NeedsSignIn(why))
        {
            UpdateSettings(s => s.Session = "");
            signIn = ("Sign in", ShowSettings);
        }
        // "Try again" uploads the same capture once more (signing in comes first when that was the problem).
        signIn ??= ("Try again", () => _ = Deliver(file, mode, record, mirror: false, fresh: fresh));
        if (mediaCopied)
            Notify(ToastKind.Warn, $"Upload failed – the {lower} is on the clipboard", why + "\nA copy is kept in the captures folder.",
                null, file, extra: signIn, discard: discard, history: inHistory);
        else if (Settings.FallbackToClipboard && TryClipboard(ClipFile(), what))
            Notify(ToastKind.Warn, $"Upload failed – the {lower} is on the clipboard instead", why + "\nA copy is kept in the captures folder.",
                null, file, extra: signIn, discard: discard, history: inHistory);
        else if (!Settings.FallbackToClipboard)
            Notify(ToastKind.Error, "Upload failed – nothing was copied", why + "\nA copy is kept in the captures folder.",
                null, file, extra: signIn, discard: discard, history: inHistory);
        return r;
    }

    /// <summary>Copy link on the card of a capture that was copied and uploaded.</summary>
    void CopyLink(string link)
    {
        try
        {
            ClipboardService.OnUi(() => ClipboardService.SetText(link));
            Notify(ToastKind.Ok, "Link copied to the clipboard", "Paste it with Ctrl+V.", link);
        }
        catch (Exception e)
        {
            Log.Error("clipboard link", e);
            Notify(ToastKind.Warn, "The clipboard was busy", "Try again, or use the tray menu → Copy last link.");
        }
    }

    /// <summary>A copy of a capture for the clipboard, in the work folder, for captures the captures folder may not keep:
    /// pasting the file keeps working once the copy there is gone. The newest few stay; all go at the next start.</summary>
    static string ClipboardCopy(string file)
    {
        try
        {
            var root = Path.Combine(AppPaths.WorkDir, "clipboard");
            Directory.CreateDirectory(root);
            foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(4))
                try { Directory.Delete(old, true); } catch { }
            var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Path.GetFileName(file));
            File.Copy(file, copy);
            return copy;
        }
        catch (Exception e)
        {
            Log.Error("clipboard copy " + file, e);
            return file;
        }
    }

    /// <summary>Uploads a capture from the history (Upload button there): card, clipboard and history as usual.</summary>
    public async Task<BridgeResult> UploadRecordAsync(HistoryRecord record)
    {
        if (record.File == null || !File.Exists(record.File)) return new BridgeResult(false, null, null, "The file is not on this PC");
        return await Deliver(record.File, OutputMode.SecretLink, record, mirror: false)
               ?? new BridgeResult(false, null, null, "Nothing was uploaded");
    }

    /// <summary>Puts the media on the clipboard; on failure shows the error card and returns false.</summary>
    bool TryClipboard(string file, string what)
    {
        try
        {
            ClipboardService.MediaToClipboard(file);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("clipboard media", e);
            Notify(ToastKind.Error, $"Could not copy the {what.ToLowerInvariant()} to the clipboard", e.Message + "\nThe file is kept in the captures folder.", null, file);
            return false;
        }
    }

    /// <summary>Shows the app's notification card. Busy and success cards follow the Notifications setting;
    /// warnings and errors are always shown.</summary>
    public void Notify(ToastKind kind, string title, string text, string? link = null, string? file = null, int? percent = null,
        (string, Action)? extra = null, Action? discard = null, Action? history = null, (string, Action)[]? more = null,
        System.Windows.Media.ImageSource? image = null)
    {
        if (kind != ToastKind.Busy) Log.Info($"notify: {title} – {text}");
        if (!Settings.Notifications && kind is ToastKind.Ok or ToastKind.Busy && extra == null) return;
        try { ToastWindow.Show(kind, title, text, link, file, percent, extra, discard, history, more, image); }
        catch (Exception e) { Log.Error("toast", e); }
    }

    // ---------- what friends sent ----------

    /// <summary>A friend sent something: remembered until it is looked at (the direct window shows it).</summary>
    public void Arrived(string friend, string what, string path)
    {
        unseen = (friend, what, path, DateTime.Now);
        flashOn = true;
        if (Settings.DirectFlash) flash.Start();
        ShowIdleIcon();
        UpdateTip();
    }

    /// <summary>What arrived was looked at: the icon is quiet again.</summary>
    public void Seen()
    {
        if (unseen == null) return;
        unseen = null;
        flash.Stop();
        ShowIdleIcon();
        UpdateTip();
    }

    /// <summary>The direct window at the newest file that was not looked at yet.</summary>
    void ShowUnseen()
    {
        if (unseen is not { } u) return;
        direct.ShowWindow(u.Friend, u.Path);
        Seen();
    }

    /// <summary>The icon while no recording, upload or countdown shows its own: with a dot while something new waits
    /// (flashing unless Settings → Direct says otherwise).</summary>
    Icon IdleIcon => unseen != null && (flashOn || !Settings.DirectFlash) ? iconNew : iconIdle;

    void ShowIdleIcon()
    {
        if (recorder == null && uploads == 0 && countdownCts == null) tray.Icon = IdleIcon;
    }

    void UpdateTip()
    {
        bool capture = TrayMenuLogic.ShowsDelay(Settings.Mode);
        string t = recorder != null
            ? (recorder.Paused ? "Peergos Snap – recording paused"
                : "Peergos Snap – recording" + (recordingFor != null ? $" for {recordingFor}" : "") + "… click to stop")
            : unseen is { } u ? TrayMenuLogic.ArrivedTip(u.Friend, u.What, u.At)
            : $"Peergos Snap – click: {TrayMenuLogic.Click(Settings.Mode)}"
              + (Settings.DelaySeconds > 0 && capture ? $" after {Settings.DelaySeconds} s" : "")
              + (capture ? $" · {OutputLogic.Short(Settings.Output)}" : "");
        tray.Text = TrayMenuLogic.Fit(t);
    }

    static void Open(string target)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) { Log.Error("open " + target, e); }
    }

    void CleanCache()
    {
        try
        {
            foreach (var d in Directory.Exists(AppPaths.WorkDir) ? Directory.GetDirectories(AppPaths.WorkDir) : [])
                try { Directory.Delete(d, true); } catch { }
            // 0 = keep forever (the default). Otherwise old captures go to the Recycle Bin, never deleted outright.
            if (Settings.CacheKeepDays <= 0 || !Directory.Exists(AppPaths.CacheDir)) return;
            var limit = DateTime.Now.AddDays(-Settings.CacheKeepDays);
            // Locked history entries keep their files, however old.
            var locked = history.Records.Where(r => r.Locked && r.File != null).Select(r => r.File!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var old = CaptureFiles.Scan(AppPaths.CacheDir).Where(f => File.GetLastWriteTime(f) < limit && !locked.Contains(f)).ToList();
            if (old.Count > 0)
            {
                Recycle.Delete(old);
                Log.Info($"local copies older than {Settings.CacheKeepDays} days moved to the Recycle Bin: {old.Count}");
            }
        }
        catch (Exception e) { Log.Error("cache cleanup", e); }
    }

    /// <summary>Tray icon drawn in code: a rounded square with a lens; a coloured dot while recording.</summary>
    public static Icon MakeIcon(Color body, Color? dot, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 32f;
            using var path = new GraphicsPath();
            var r = new RectangleF(2 * s, 6 * s, 28 * s, 22 * s);
            float rad = 6 * s;
            path.AddArc(r.X, r.Y, rad, rad, 180, 90);
            path.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            path.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            path.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
            path.CloseFigure();
            using (var b = new SolidBrush(body)) g.FillPath(b, path);
            using (var b = new SolidBrush(body)) g.FillRectangle(b, 10 * s, 3 * s, 12 * s, 5 * s);
            g.FillEllipse(Brushes.White, 9 * s, 10 * s, 14 * s, 14 * s);
            using (var b = new SolidBrush(body)) g.FillEllipse(b, 12.5f * s, 13.5f * s, 7 * s, 7 * s);
            if (dot is { } d)
            {
                using var b = new SolidBrush(d);
                g.FillEllipse(Brushes.White, 18 * s, 16 * s, 14 * s, 14 * s);
                g.FillEllipse(b, 20 * s, 18 * s, 10 * s, 10 * s);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (recorder != null)
        {
            try { recorder.CancelAsync().Wait(5000); } catch { }
        }
        updateTimer.Stop();
        direct.Dispose();
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }
}
