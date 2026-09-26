using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap.UI;

/// <summary>Settings apply and save at once (no Save/Cancel). Invalid values wait with a red note.</summary>
public partial class SettingsWindow : Window
{
    readonly TrayController app;
    bool loading = true;

    public SettingsWindow(TrayController app, int tab = 0)
    {
        this.app = app;
        InitializeComponent();
        Tabs.SelectedIndex = tab;
        Theme.Attach(this);
        Load();
        Wire();
        loading = false;
        UpdateModePanels();
        ShowHotkeyErrors();
    }

    Settings S => app.Settings;

    void Change(Action<Settings> a)
    {
        if (loading) return;
        app.UpdateSettings(a);
        ShowHotkeyErrors();
    }

    static void Select(ComboBox c, string v)
    {
        foreach (ComboBoxItem i in c.Items)
            if ((string)i.Content == v) { c.SelectedItem = i; return; }
    }

    static string Sel(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Content as string ?? "";

    void Load()
    {
        ModeFolder.IsChecked = S.Storage == StorageMode.SharedFolder;
        ModeAccount.IsChecked = S.Storage == StorageMode.Account;
        FolderLink.Text = S.FolderLink;
        FolderPassword.Password = S.FolderLinkPassword;
        Server.Text = S.Server;
        Username.Text = S.Username;
        AccountPassword.Password = S.AccountPassword;
        AccountFolder.Text = S.AccountFolder;

        KindPicture.IsChecked = S.DefaultKind == CaptureKind.Picture;
        KindVideo.IsChecked = S.DefaultKind == CaptureKind.Video;
        Select(ImageFormat, S.ImageFormat);
        Select(VideoFormat, S.VideoFormat);
        Select(FrameRate, S.FrameRate.ToString());
        Quality.Value = S.VideoQuality;
        QualityLabel.Text = $"Video quality (CRF {S.VideoQuality})";
        RecordCursor.IsChecked = S.RecordCursor;
        AskPicture.IsChecked = S.AskBeforePictureUpload;

        Dim.Value = S.OverlayDimPercent;
        DimLabel.Text = $"Darken outside the selection ({S.OverlayDimPercent} %; 0 = fully transparent)";
        BorderColor.Text = S.BorderColor;
        ShowSize.IsChecked = S.ShowSizeLabel;
        PickWindow.IsChecked = S.PickWindowOnClick;
        Delay.Text = S.CaptureDelayMs.ToString();

        OutLink.IsChecked = S.Output == OutputMode.SecretLink;
        OutMedia.IsChecked = S.Output == OutputMode.DirectMedia;
        Fallback.IsChecked = S.FallbackToClipboard;
        Notifications.IsChecked = S.Notifications;
        MirrorOn.IsChecked = S.MirrorEnabled;
        MirrorFolder.Text = S.MirrorFolder;
        CachePath.Text = AppPaths.CacheDir;
        KeepDays.Text = S.CacheKeepDays.ToString();

        HkPicture.Text = S.HotkeyPicture;
        HkVideo.Text = S.HotkeyVideo;
        HkPause.Text = S.HotkeyPause;
        HkOutput.Text = S.HotkeyToggleOutput;

        SchemeBox.ItemsSource = Theme.Schemes;
        SchemeBox.SelectedItem = Theme.Schemes.FirstOrDefault(x => x.Id == S.ColorScheme) ?? Theme.Schemes[0];
        FontSlider.Value = S.FontPercent;
        FontLabel.Text = $"Font size ({S.FontPercent} %)";
        PromptLocation.Text = S.PromptSourceLocation;

        Autostart.IsChecked = Services.Autostart.IsEnabled;
        ShowNotes.IsChecked = S.ShowUserNotes;
        var ver = typeof(App).Assembly.GetName().Version;
        About.Text = $"Peergos Snap {ver?.ToString(3)} · GPL-3.0-or-later · https://github.com/anderlejan/peergos-snap\n" +
                     "Uses Peergos (AGPL-3.0), FFmpeg (GPL-3.0), OpenJDK runtime (GPL-2.0 with Classpath Exception) and Microsoft WebView2 SDK (BSD-3-Clause).";
    }

    void Wire()
    {
        ModeFolder.Checked += (_, _) => { Change(s => s.Storage = StorageMode.SharedFolder); UpdateModePanels(); };
        ModeAccount.Checked += (_, _) => { Change(s => s.Storage = StorageMode.Account); UpdateModePanels(); };
        FolderLink.TextChanged += (_, _) =>
        {
            var ok = LinkCheck.LooksLikeSecretLink(FolderLink.Text, out var problem) || FolderLink.Text.Trim().Length == 0;
            FolderLinkErr.Text = problem ?? "";
            FolderLinkErr.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) Change(s => s.FolderLink = FolderLink.Text.Trim());
        };
        FolderPassword.PasswordChanged += (_, _) => Change(s => s.FolderLinkPassword = FolderPassword.Password);
        Server.LostFocus += (_, _) => Change(s => s.Server = Server.Text);
        Username.TextChanged += (_, _) => Change(s => s.Username = Username.Text.Trim());
        AccountPassword.PasswordChanged += (_, _) => Change(s => s.AccountPassword = AccountPassword.Password);
        AccountFolder.LostFocus += (_, _) => Change(s => s.AccountFolder = AccountFolder.Text);
        TestBtn.Click += async (_, _) => await Test();

        KindPicture.Checked += (_, _) => Change(s => s.DefaultKind = CaptureKind.Picture);
        KindVideo.Checked += (_, _) => Change(s => s.DefaultKind = CaptureKind.Video);
        ImageFormat.SelectionChanged += (_, _) => Change(s => s.ImageFormat = Sel(ImageFormat));
        VideoFormat.SelectionChanged += (_, _) => Change(s => s.VideoFormat = Sel(VideoFormat));
        FrameRate.SelectionChanged += (_, _) => Change(s => s.FrameRate = int.Parse(Sel(FrameRate)));
        Quality.ValueChanged += (_, _) => { QualityLabel.Text = $"Video quality (CRF {(int)Quality.Value})"; Change(s => s.VideoQuality = (int)Quality.Value); };
        RecordCursor.Click += (_, _) => Change(s => s.RecordCursor = RecordCursor.IsChecked == true);
        AskPicture.Click += (_, _) => Change(s => s.AskBeforePictureUpload = AskPicture.IsChecked == true);

        Dim.ValueChanged += (_, _) => { DimLabel.Text = $"Darken outside the selection ({(int)Dim.Value} %; 0 = fully transparent)"; Change(s => s.OverlayDimPercent = (int)Dim.Value); };
        BorderColor.TextChanged += (_, _) =>
        {
            try { System.Windows.Media.ColorConverter.ConvertFromString(BorderColor.Text); Change(s => s.BorderColor = BorderColor.Text.Trim()); BorderColor.ClearValue(BorderBrushProperty); }
            catch { BorderColor.BorderBrush = System.Windows.Media.Brushes.Red; }
        };
        ShowSize.Click += (_, _) => Change(s => s.ShowSizeLabel = ShowSize.IsChecked == true);
        PickWindow.Click += (_, _) => Change(s => s.PickWindowOnClick = PickWindow.IsChecked == true);
        Delay.TextChanged += (_, _) =>
        {
            bool ok = int.TryParse(Delay.Text, out var ms) && ms is >= 0 and <= 30000;
            DelayErr.Text = "A number from 0 to 30000";
            DelayErr.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) Change(s => s.CaptureDelayMs = ms);
        };

        OutLink.Checked += (_, _) => Change(s => s.Output = OutputMode.SecretLink);
        OutMedia.Checked += (_, _) => Change(s => s.Output = OutputMode.DirectMedia);
        Fallback.Click += (_, _) => Change(s => s.FallbackToClipboard = Fallback.IsChecked == true);
        Notifications.Click += (_, _) => Change(s => s.Notifications = Notifications.IsChecked == true);
        MirrorOn.Click += (_, _) => { ValidateMirror(); };
        MirrorFolder.TextChanged += (_, _) => ValidateMirror();
        MirrorBrowse.Click += (_, _) =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for copies of every capture" };
            if (Directory.Exists(MirrorFolder.Text)) d.InitialDirectory = MirrorFolder.Text;
            if (d.ShowDialog(this) == true) { MirrorFolder.Text = d.FolderName; MirrorOn.IsChecked = true; ValidateMirror(); }
        };
        MirrorOpen.Click += (_, _) => { if (Directory.Exists(MirrorFolder.Text)) Shell(MirrorFolder.Text); };
        CacheOpen.Click += (_, _) => { Directory.CreateDirectory(AppPaths.CacheDir); Shell(AppPaths.CacheDir); };
        KeepDays.TextChanged += (_, _) => { if (int.TryParse(KeepDays.Text, out var d) && d >= 0) Change(s => s.CacheKeepDays = d); };

        WireHotkey(HkPicture, (s, v) => s.HotkeyPicture = v);
        WireHotkey(HkVideo, (s, v) => s.HotkeyVideo = v);
        WireHotkey(HkPause, (s, v) => s.HotkeyPause = v);
        WireHotkey(HkOutput, (s, v) => s.HotkeyToggleOutput = v);

        SchemeBox.SelectionChanged += (_, _) => { if (SchemeBox.SelectedItem is Scheme sc) Change(s => s.ColorScheme = sc.Id); };
        FontSlider.ValueChanged += (_, _) => { FontLabel.Text = $"Font size ({(int)FontSlider.Value} %)"; Change(s => s.FontPercent = (int)FontSlider.Value); };
        FontReset.Click += (_, _) => FontSlider.Value = 100;
        PromptLocation.LostFocus += (_, _) => Change(s => s.PromptSourceLocation = PromptLocation.Text.Trim());

        Autostart.Click += (_, _) =>
        {
            try { Services.Autostart.Set(Autostart.IsChecked == true); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Peergos Snap"); }
        };
        ShowNotes.Click += (_, _) => Change(s => s.ShowUserNotes = ShowNotes.IsChecked == true);
        OpenNotes.Click += (_, _) => app.ShowNotes();
        OpenLog.Click += (_, _) => { if (File.Exists(AppPaths.LogFile)) Shell(AppPaths.LogFile); };
        OpenLicenses.Click += (_, _) =>
        {
            var f = Path.Combine(AppPaths.AppDir, "licenses", "THIRD-PARTY-NOTICES.md");
            Shell(File.Exists(f) ? f : Path.Combine(AppPaths.AppDir, "licenses"));
        };
    }

    void ValidateMirror()
    {
        var path = MirrorFolder.Text.Trim();
        bool on = MirrorOn.IsChecked == true;
        string? err = on && path.Length == 0 ? "Choose a folder" : on && !Directory.Exists(path) ? "This folder does not exist" : null;
        MirrorErr.Text = err ?? "";
        MirrorErr.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
        if (err == null) Change(s => { s.MirrorEnabled = on; s.MirrorFolder = path; });
        else if (!on) Change(s => s.MirrorEnabled = false);
    }

    void WireHotkey(TextBox box, Action<Settings, string> set)
    {
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { box.Text = ""; Change(s => set(s, "")); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab) return;
            var m = Keyboard.Modifiers;
            bool win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
            var name = new KeyConverter().ConvertToInvariantString(key) ?? key.ToString();
            var hk = new Hotkey(m.HasFlag(ModifierKeys.Control), m.HasFlag(ModifierKeys.Alt), m.HasFlag(ModifierKeys.Shift), win, name);
            if (!Hotkey.TryParse(hk.ToString(), out Hotkey _)) return;
            box.Text = hk.ToString();
            Change(s => set(s, hk.ToString()));
        };
    }

    void ShowHotkeyErrors()
    {
        void Show(TextBlock t, string k)
        {
            var err = app.HotkeyErrors.GetValueOrDefault(k);
            t.Text = err ?? "";
            t.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
        }
        Show(HkPictureErr, "Picture");
        Show(HkVideoErr, "Video");
        Show(HkPauseErr, "Pause");
        Show(HkOutputErr, "Output");
    }

    void UpdateModePanels()
    {
        FolderPanel.Visibility = ModeFolder.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = ModeAccount.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task Test()
    {
        TestBtn.IsEnabled = false;
        TestResult.Foreground = System.Windows.Media.Brushes.Gray;
        TestResult.Text = "Connecting…";
        try
        {
            if (!S.PeergosConfigured) { TestResult.Text = "Fill in the fields above first."; return; }
            var r = await Uploader.CheckAsync(S.Clone());
            TestResult.Foreground = r.Ok ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Firebrick;
            TestResult.Text = r.Ok ? $"✓ Works – uploads go to {r.PeergosPath}" : "✗ " + r.Error;
        }
        finally { TestBtn.IsEnabled = true; }
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
