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
        ApplySettings();
        UpdateTip();
        CleanCache();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            Settings.Save(AppPaths.SettingsFile);
            Notify(ToastKind.Ok, "Peergos Snap is running", "Click the tray icon to capture. Right-click it → Settings to connect Peergos.");
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

    void ApplySettings()
    {
        Theme.Apply(Settings.ColorScheme, Settings.FontPercent);
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
        m.Items.Add(new WinForms.ToolStripLabel("Tray click takes") { ForeColor = SystemColors.GrayText });
        m.Items.Add(Item("    Picture", () => UpdateSettings(s => s.DefaultKind = CaptureKind.Picture), check: Settings.DefaultKind == CaptureKind.Picture));
        m.Items.Add(Item("    Video (click again to stop)", () => UpdateSettings(s => s.DefaultKind = CaptureKind.Video), check: Settings.DefaultKind == CaptureKind.Video));
        m.Items.Add(new WinForms.ToolStripLabel("Output") { ForeColor = SystemColors.GrayText });
        m.Items.Add(Item("    Secret link (upload to Peergos)", () => UpdateSettings(s => s.Output = OutputMode.SecretLink), Settings.HotkeyToggleOutput, Settings.Output == OutputMode.SecretLink));
        m.Items.Add(Item("    Media to clipboard (no upload)", () => UpdateSettings(s => s.Output = OutputMode.DirectMedia), check: Settings.Output == OutputMode.DirectMedia));
        m.Items.Add(new WinForms.ToolStripSeparator());
        if (lastLink != null) m.Items.Add(Item("Copy last link", () => ClipboardService.SetText(lastLink)));
        m.Items.Add(Item("Open captures folder", () => Open(AppPaths.CacheDir)));
        m.Items.Add(Item("Settings…", ShowSettings));
        if (Settings.ShowUserNotes) m.Items.Add(Item("User notes…", ShowNotes));
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
                    "Peergos is not set up yet: right-click the tray icon → Settings → Peergos.", null, file);
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
        if (Settings.FallbackToClipboard && TryClipboard(file, what))
            Notify(ToastKind.Warn, $"Upload failed – the {what.ToLowerInvariant()} is on the clipboard instead", why + "\nA copy is kept in the captures folder.", null, file);
        else if (!Settings.FallbackToClipboard)
            Notify(ToastKind.Error, "Upload failed – nothing was copied", why + "\nA copy is kept in the captures folder.", null, file);
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
    void Notify(ToastKind kind, string title, string text, string? link = null, string? file = null, int? percent = null)
    {
        if (kind != ToastKind.Busy) Log.Info($"notify: {title} – {text}");
        if (!Settings.Notifications && kind is ToastKind.Ok or ToastKind.Busy) return;
        try { ToastWindow.Show(kind, title, text, link, file, percent); }
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
        hotkeys.Dispose();
        tray.Visible = false;
        tray.Dispose();
    }
}
