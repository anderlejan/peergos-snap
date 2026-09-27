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
    readonly Icon iconIdle, iconRec, iconPaused, iconBusy;
    readonly DispatcherTimer watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
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
    public Dictionary<string, string?> HotkeyErrors { get; } = [];

    public TrayController()
    {
        Settings = Settings.Load(AppPaths.SettingsFile);
        history = HistoryStore.Load(AppPaths.HistoryFile);
        iconIdle = MakeIcon(Color.FromArgb(43, 138, 110), null);
        iconRec = MakeIcon(Color.FromArgb(43, 138, 110), Color.Red);
        iconPaused = MakeIcon(Color.FromArgb(43, 138, 110), Color.Orange);
        iconBusy = MakeIcon(Color.FromArgb(40, 110, 200), null);

        tray.Icon = iconIdle;
        tray.Visible = true;
        tray.ContextMenuStrip = new WinForms.ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
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

        // Updates: once shortly after the start, then the hourly timer checks whether a day has passed.
        updateTimer.Tick += async (_, _) => await AutoUpdate(false);
        updateTimer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        first.Tick += async (_, _) => { first.Stop(); await AutoUpdate(true); };
        first.Start();
    }

    public HistoryStore History => history;

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

    bool Busy => recorder != null || uploads > 0 || selecting || stopping;

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
        var scheme = Settings.ColorScheme;
        change(Settings);
        Settings.Save(AppPaths.SettingsFile);
        ApplySettings();
        UpdateTip();
        if (scheme != Settings.ColorScheme) RebuildOpenWindows();
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

    void ApplySettings()
    {
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
        ThemedMenu.Apply(m);
        WinForms.ToolStripMenuItem Item(string text, Action a, string? keys = null, bool check = false, bool enabled = true)
        {
            var i = new WinForms.ToolStripMenuItem(text) { Checked = check, Enabled = enabled, ShortcutKeyDisplayString = keys };
            i.Click += (_, _) => a();
            return i;
        }
        bool rec = recorder != null;
        var delaySuffix = Settings.DelaySeconds > 0 ? $" (after {Settings.DelaySeconds} s)" : "";
        if (countdownCts != null) m.Items.Add(Item("Cancel the countdown", () => countdownCts?.Cancel()));
        m.Items.Add(Item("Take picture" + delaySuffix, () => _ = CapturePicture(), Settings.HotkeyPicture, enabled: !rec));
        m.Items.Add(Item(rec ? "Stop recording" : "Record video", () => _ = ToggleRecording(), Settings.HotkeyVideo));
        if (rec) m.Items.Add(Item(recorder!.Paused ? "Resume recording" : "Pause recording", () => _ = TogglePause(), Settings.HotkeyPause));
        if (rec) m.Items.Add(Item("Cancel recording", () => _ = CancelRecording()));
        // Delay before capturing: quick choices here, any length in Settings.
        var delay = new WinForms.ToolStripMenuItem(Settings.DelaySeconds > 0 ? $"Delay: {Settings.DelaySeconds} s" : "Delay: none");
        void D(int sec, string text)
        {
            var i = new WinForms.ToolStripMenuItem(text) { Checked = Settings.DelaySeconds == sec };
            i.Click += (_, _) => UpdateSettings(x => x.DelaySeconds = sec);
            delay.DropDownItems.Add(i);
        }
        D(0, "No delay");
        for (int sec = 1; sec <= 5; sec++) D(sec, sec == 1 ? "1 second" : $"{sec} seconds");
        if (Settings.DelaySeconds > 5) D(Settings.DelaySeconds, $"{Settings.DelaySeconds} seconds (from Settings)");
        delay.DropDownItems.Add(new WinForms.ToolStripSeparator());
        var other = new WinForms.ToolStripMenuItem("Other length…");
        other.Click += (_, _) => ShowSettings(1);
        delay.DropDownItems.Add(other);
        ThemedMenu.Apply(delay.DropDown as WinForms.ToolStripDropDownMenu ?? new WinForms.ToolStripDropDownMenu());
        m.Items.Add(delay);
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(new WinForms.ToolStripLabel("Tray click takes"));
        m.Items.Add(Item("    Picture", () => UpdateSettings(s => s.DefaultKind = CaptureKind.Picture), check: Settings.DefaultKind == CaptureKind.Picture));
        m.Items.Add(Item("    Video (click again to stop)", () => UpdateSettings(s => s.DefaultKind = CaptureKind.Video), check: Settings.DefaultKind == CaptureKind.Video));
        m.Items.Add(new WinForms.ToolStripLabel("Output"));
        m.Items.Add(Item("    Secret link (upload to Peergos)", () => UpdateSettings(s => s.Output = OutputMode.SecretLink), Settings.HotkeyToggleOutput, Settings.Output == OutputMode.SecretLink));
        m.Items.Add(Item("    Media to clipboard (no upload)", () => UpdateSettings(s => s.Output = OutputMode.DirectMedia), check: Settings.Output == OutputMode.DirectMedia));
        m.Items.Add(new WinForms.ToolStripLabel("Videos"));
        m.Items.Add(Item("    Show mouse pointer", () => UpdateSettings(s => s.RecordCursor = !s.RecordCursor), check: Settings.RecordCursor, enabled: !rec));
        m.Items.Add(new WinForms.ToolStripSeparator());
        if (lastLink != null) m.Items.Add(Item("Copy last link", () => ClipboardService.SetText(lastLink)));
        m.Items.Add(Item("History…", ShowHistory));
        m.Items.Add(Item("Open captures folder", () => Open(AppPaths.CacheDir)));
        m.Items.Add(Item("Settings…", ShowSettings));
        if (Settings.ShowUserNotes) m.Items.Add(Item("User notes…", ShowNotes));
        m.Items.Add(Item("Help", () => ShowHelp("")));
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(Item("Quit", () => System.Windows.Application.Current.Shutdown()));
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

    public void ShowHistory()
    {
        if (historyWindow is { IsLoaded: true })
        {
            if (historyWindow.WindowState == System.Windows.WindowState.Minimized) historyWindow.WindowState = System.Windows.WindowState.Normal;
            historyWindow.Activate();
            return;
        }
        historyWindow = new HistoryWindow(this);
        historyWindow.Show();
        historyWindow.Activate();
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
        BuildMenu();
        var p = WinForms.Cursor.Position;
        m.Show(p);
    }

    public void ShowHelp(string section)
    {
        helpWindow ??= new HelpWindow();
        helpWindow.ShowHelp(section);
    }

    void ToggleOutput()
    {
        UpdateSettings(s => s.Output = s.Output == OutputMode.SecretLink ? OutputMode.DirectMedia : OutputMode.SecretLink);
        Notify(ToastKind.Ok, "Output: " + (Settings.Output == OutputMode.SecretLink ? "secret link (upload to Peergos)" : "media to clipboard (no upload)"), "Changed with the hotkey.");
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
            case "--quit": System.Windows.Application.Current.Shutdown(); break;
        }
    }

    async Task PrimaryClick()
    {
        if (countdownCts != null) { countdownCts.Cancel(); return; }
        if (recorder != null) await StopRecording();
        else if (Settings.DefaultKind == CaptureKind.Video) await StartRecording();
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

    public async Task CapturePicture()
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
                ScreenCapture.Save(bmp, file, Settings.ImageFormat);
            Log.Info($"picture {rect}{(frozen != null ? " (frozen)" : "")} -> {file}");
            var record = history.Add(new HistoryRecord
            {
                Created = now, Kind = "picture", File = file, Width = rect.Width, Height = rect.Height,
                Bytes = new FileInfo(file).Length, App = source.App, WindowTitle = source.Title,
            });
            if (Settings.Output == OutputMode.SecretLink && Settings.AskBeforePictureUpload &&
                System.Windows.MessageBox.Show("Upload this picture to Peergos?", "Peergos Snap", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            {
                record.MirrorFile = Mirror(file, now);
                history.Save();
                if (TryClipboard(file, "Picture")) Notify(ToastKind.Ok, "Picture copied to the clipboard (not uploaded)", "Paste it with Ctrl+V.", null, file);
                return;
            }
            await Deliver(file, Settings.Output, record);
        }
        catch (Exception e)
        {
            Log.Error("picture", e);
            Notify(ToastKind.Error, "Capture failed", e.Message);
        }
        finally { frozen?.Dispose(); }
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
            tray.Icon = recorder != null ? iconRec : uploads > 0 ? iconBusy : iconIdle;
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

    public async Task StartRecording()
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
        tray.Icon = uploads > 0 ? iconBusy : iconIdle;
    }

    public async Task CancelRecording()
    {
        if (recorder == null || stopping) return;
        stopping = true;
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

            var dlg = new FinishRecordingDialog(cached, rec.Elapsed, Settings);
            dlg.ShowDialog();
            Log.Info("video " + cached + " -> " + dlg.Choice);
            switch (dlg.Choice)
            {
                case FinishChoice.Upload: await Deliver(cached, OutputMode.SecretLink, record); break;
                case FinishChoice.Clipboard: await Deliver(cached, OutputMode.DirectMedia, record); break;
                case FinishChoice.SaveAs:
                    var sfd = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(cached), Filter = "Video|*." + Settings.VideoFormat };
                    if (sfd.ShowDialog() == true) { File.Copy(cached, sfd.FileName, true); Notify(ToastKind.Ok, "Video saved", sfd.FileName, null, sfd.FileName); }
                    record.MirrorFile = Mirror(cached, record.Created);
                    history.Save();
                    break;
                case FinishChoice.Discard:
                    File.Delete(cached);
                    history.Remove([record.Id]);
                    Notify(ToastKind.Ok, "Recording discarded", "The video was deleted.");
                    break;
                default:
                    record.MirrorFile = Mirror(cached, record.Created);
                    history.Save();
                    Notify(ToastKind.Ok, "Video kept on this PC only", "Nothing was uploaded or copied.", null, cached);
                    break;
            }
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
    /// The notification card always says which of the three outcomes happened: link copied, media copied, or failed.</summary>
    async Task<BridgeResult?> Deliver(string file, OutputMode mode, HistoryRecord? record = null, bool mirror = true)
    {
        var mirrored = mirror ? Mirror(file, record?.Created ?? DateTime.Now) : null;
        if (record != null && mirrored != null) { record.MirrorFile = mirrored; history.Save(); }
        bool video = !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        string what = video ? "Video" : "Picture";
        string pasteHint = video ? "The video file is on the clipboard – paste it with Ctrl+V into a chat, mail or folder."
                                 : "The picture is on the clipboard – paste it with Ctrl+V.";
        if (mode == OutputMode.DirectMedia)
        {
            if (TryClipboard(file, what)) Notify(ToastKind.Ok, $"{what} copied to the clipboard", pasteHint, null, file);
            return null;
        }
        if (!Settings.PeergosConfigured)
        {
            if (TryClipboard(file, what))
                Notify(ToastKind.Warn, $"{what} copied to the clipboard (not uploaded)",
                    "You are not signed in to Peergos: right-click the tray icon → Settings → Peergos.", null, file,
                    extra: ("Sign in", ShowSettings));
            return new BridgeResult(false, null, null, "Not signed in to Peergos");
        }
        uploads++;
        tray.Icon = recorder == null ? iconBusy : tray.Icon;
        tray.Text = "Peergos Snap – uploading…";
        Notify(ToastKind.Busy, $"Uploading the {what.ToLowerInvariant()} to Peergos…", Path.GetFileName(file), null, file);
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        BridgeResult r;
        try
        {
            r = await Uploader.UploadAsync(Settings.Clone(), file, pct => dispatcher.BeginInvoke(() =>
            {
                tray.Text = $"Peergos Snap – uploading {pct}%";
                Notify(ToastKind.Busy, $"Uploading the {what.ToLowerInvariant()} to Peergos… {pct}%", Path.GetFileName(file), null, file, pct);
            }));
        }
        catch (Exception e) { r = new BridgeResult(false, null, null, e.Message); }
        finally
        {
            uploads--;
            if (recorder == null) tray.Icon = uploads > 0 ? iconBusy : iconIdle;
            UpdateTip();
        }
        if (r.Ok && r.Link != null)
        {
            lastLink = r.Link;
            if (record != null)
            {
                record.Link = r.Link;
                record.PeergosPath = r.PeergosPath;
                record.Uploaded = DateTime.Now;
                history.Save();
            }
            try
            {
                ClipboardService.OnUi(() => ClipboardService.SetText(r.Link));
                Notify(ToastKind.Ok, "Link copied to the clipboard", $"{what} uploaded to Peergos ({r.PeergosPath}). Paste the link with Ctrl+V.", r.Link, file);
            }
            catch (Exception e)
            {
                Log.Error("clipboard link", e);
                Notify(ToastKind.Warn, $"{what} uploaded, but the clipboard was busy", "Use \"Open link\" or the tray menu → Copy last link.", r.Link, file);
            }
            return r;
        }
        var why = r.Error ?? "Unknown error";
        (string, Action)? signIn = null;
        if (Uploader.NeedsSignIn(why))
        {
            UpdateSettings(s => s.Session = "");
            signIn = ("Sign in", ShowSettings);
        }
        if (Settings.FallbackToClipboard && TryClipboard(file, what))
            Notify(ToastKind.Warn, $"Upload failed – the {what.ToLowerInvariant()} is on the clipboard instead", why + "\nA copy is kept in the captures folder.", null, file, extra: signIn);
        else if (!Settings.FallbackToClipboard)
            Notify(ToastKind.Error, "Upload failed – nothing was copied", why + "\nA copy is kept in the captures folder.", null, file, extra: signIn);
        return r;
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
    void Notify(ToastKind kind, string title, string text, string? link = null, string? file = null, int? percent = null,
        (string, Action)? extra = null)
    {
        if (kind != ToastKind.Busy) Log.Info($"notify: {title} – {text}");
        if (!Settings.Notifications && kind is ToastKind.Ok or ToastKind.Busy && extra == null) return;
        try { ToastWindow.Show(kind, title, text, link, file, percent, extra); }
        catch (Exception e) { Log.Error("toast", e); }
    }

    void UpdateTip()
    {
        string t = recorder != null
            ? (recorder.Paused ? "Peergos Snap – recording paused" : "Peergos Snap – recording… click to stop")
            : $"Peergos Snap – click: {(Settings.DefaultKind == CaptureKind.Video ? "record video" : "take picture")}"
              + (Settings.DelaySeconds > 0 ? $" after {Settings.DelaySeconds} s" : "")
              + $" · {(Settings.Output == OutputMode.SecretLink ? "secret link" : "clipboard")}";
        tray.Text = t.Length > 127 ? t[..127] : t;
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
            var old = CaptureFiles.Scan(AppPaths.CacheDir).Where(f => File.GetLastWriteTime(f) < limit).ToList();
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
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }
}
