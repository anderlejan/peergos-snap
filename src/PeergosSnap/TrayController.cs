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
    readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    bool installingUpdate;
    UpdateInfo? pendingUpdate;
    public Dictionary<string, string?> HotkeyErrors { get; } = [];

    public TrayController()
    {
        Settings = Settings.Load(AppPaths.SettingsFile);
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
        UpdateTip();
        CleanCache();

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
        m.Items.Add(Item("Take picture", () => _ = CapturePicture(), Settings.HotkeyPicture, enabled: !rec));
        m.Items.Add(Item(rec ? "Stop recording" : "Record video", () => _ = ToggleRecording(), Settings.HotkeyVideo));
        if (rec) m.Items.Add(Item(recorder!.Paused ? "Resume recording" : "Pause recording", () => _ = TogglePause(), Settings.HotkeyPause));
        if (rec) m.Items.Add(Item("Cancel recording", () => _ = CancelRecording()));
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
        m.Items.Add(Item("Open captures folder", () => Open(AppPaths.CacheDir)));
        m.Items.Add(Item("Settings…", ShowSettings));
        if (Settings.ShowUserNotes) m.Items.Add(Item("User notes…", ShowNotes));
        m.Items.Add(Item("Help", () => ShowHelp("")));
        m.Items.Add(new WinForms.ToolStripSeparator());
        m.Items.Add(Item("Quit", () => System.Windows.Application.Current.Shutdown()));
    }

    public void ShowSettings()
    {
        if (settingsWindow is { IsLoaded: true }) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Show();
        settingsWindow.Activate();
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
            case "--quit": System.Windows.Application.Current.Shutdown(); break;
        }
    }

    async Task PrimaryClick()
    {
        if (recorder != null) await StopRecording();
        else if (Settings.DefaultKind == CaptureKind.Video) await StartRecording();
        else await CapturePicture();
    }

    async Task<PxRect?> Select(CaptureKind kind)
    {
        if (selecting) return null;
        selecting = true;
        try
        {
            var o = new SelectionOverlay(Settings, kind);
            o.Show();
            return await o.Result;
        }
        finally { selecting = false; }
    }

    public async Task CapturePicture()
    {
        if (recorder != null) return;
        var r = await Select(CaptureKind.Picture);
        if (r is not { } rect) return;
        try
        {
            if (Settings.CaptureDelayMs > 0) await Task.Delay(Settings.CaptureDelayMs);
            // Let the compositor draw a frame without the overlay before copying the screen.
            Native.DwmFlush();
            Native.DwmFlush();
            var file = Path.Combine(AppPaths.CacheDir, FileNames.Unique(AppPaths.CacheDir, FileNames.ForCapture(DateTime.Now, Settings.ImageFormat)));
            using (var bmp = ScreenCapture.Grab(rect)) ScreenCapture.Save(bmp, file, Settings.ImageFormat);
            Log.Info($"picture {rect} -> {file}");
            if (Settings.Output == OutputMode.SecretLink && Settings.AskBeforePictureUpload &&
                System.Windows.MessageBox.Show("Upload this picture to Peergos?", "Peergos Snap", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            {
                if (TryClipboard(file, "Picture")) Notify(ToastKind.Ok, "Picture copied to the clipboard (not uploaded)", "Paste it with Ctrl+V.", null, file);
                return;
            }
            await Deliver(file, Settings.Output);
        }
        catch (Exception e)
        {
            Log.Error("picture", e);
            Notify(ToastKind.Error, "Capture failed", e.Message);
        }
    }

    // ---------- recording ----------

    public async Task ToggleRecording()
    {
        if (recorder != null) await StopRecording();
        else await StartRecording();
    }

    public async Task StartRecording()
    {
        if (recorder != null || stopping) return;
        var r = await Select(CaptureKind.Video);
        if (r is not { } rect) return;
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
            Log.Error("record start", e);
            Notify(ToastKind.Error, "Recording failed", e.Message);
            return;
        }
        frame = new RegionFrame(recorder.Region, Settings.BorderColor);
        frame.Show();
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
            cached = Path.Combine(AppPaths.CacheDir, FileNames.Unique(AppPaths.CacheDir, Path.GetFileName(video)));
            Directory.CreateDirectory(AppPaths.CacheDir);
            File.Move(video, cached);
            rec.Cleanup();
            recorder = null;
            stopping = false;
            UpdateTip();

            var dlg = new FinishRecordingDialog(cached, rec.Elapsed, Settings);
            dlg.ShowDialog();
            Log.Info("video " + cached + " -> " + dlg.Choice);
            switch (dlg.Choice)
            {
                case FinishChoice.Upload: await Deliver(cached, OutputMode.SecretLink); break;
                case FinishChoice.Clipboard: await Deliver(cached, OutputMode.DirectMedia); break;
                case FinishChoice.SaveAs:
                    var sfd = new Microsoft.Win32.SaveFileDialog { FileName = Path.GetFileName(cached), Filter = "Video|*." + Settings.VideoFormat };
                    if (sfd.ShowDialog() == true) { File.Copy(cached, sfd.FileName, true); Notify(ToastKind.Ok, "Video saved", sfd.FileName, null, sfd.FileName); }
                    Mirror(cached);
                    break;
                case FinishChoice.Discard: File.Delete(cached); Notify(ToastKind.Ok, "Recording discarded", "The video was deleted."); break;
                default: Mirror(cached); Notify(ToastKind.Ok, "Video kept on this PC only", "Nothing was uploaded or copied.", null, cached); break;
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

    void Mirror(string file)
    {
        if (!Settings.MirrorEnabled || string.IsNullOrWhiteSpace(Settings.MirrorFolder)) return;
        try
        {
            Directory.CreateDirectory(Settings.MirrorFolder);
            var target = Path.Combine(Settings.MirrorFolder, FileNames.Unique(Settings.MirrorFolder, Path.GetFileName(file)));
            File.Copy(file, target);
        }
        catch (Exception e)
        {
            Log.Error("mirror", e);
            Notify(ToastKind.Warn, "Could not copy to the mirror folder", e.Message);
        }
    }

    /// <summary>Sends a finished capture where the output mode says; falls back to the clipboard if the upload fails.
    /// The notification card always says which of the three outcomes happened: link copied, media copied, or failed.</summary>
    async Task Deliver(string file, OutputMode mode)
    {
        Mirror(file);
        bool video = !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
        string what = video ? "Video" : "Picture";
        string pasteHint = video ? "The video file is on the clipboard – paste it with Ctrl+V into a chat, mail or folder."
                                 : "The picture is on the clipboard – paste it with Ctrl+V.";
        if (mode == OutputMode.DirectMedia)
        {
            if (TryClipboard(file, what)) Notify(ToastKind.Ok, $"{what} copied to the clipboard", pasteHint, null, file);
            return;
        }
        if (!Settings.PeergosConfigured)
        {
            if (TryClipboard(file, what))
                Notify(ToastKind.Warn, $"{what} copied to the clipboard (not uploaded)",
                    "You are not signed in to Peergos: right-click the tray icon → Settings → Peergos.", null, file,
                    extra: ("Sign in", ShowSettings));
            return;
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
            return;
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
            : $"Peergos Snap – click: {(Settings.DefaultKind == CaptureKind.Video ? "record video" : "take picture")} · {(Settings.Output == OutputMode.SecretLink ? "secret link" : "clipboard")}";
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
            if (Settings.CacheKeepDays <= 0 || !Directory.Exists(AppPaths.CacheDir)) return;
            var limit = DateTime.Now.AddDays(-Settings.CacheKeepDays);
            foreach (var f in Directory.GetFiles(AppPaths.CacheDir))
                if (File.GetLastWriteTime(f) < limit) File.Delete(f);
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
