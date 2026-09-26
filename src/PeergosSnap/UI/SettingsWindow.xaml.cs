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
        UpdateAccountPanels();
        ShowHotkeyErrors();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) { e.Handled = true; OpenHelp(); } };
    }

    static readonly string[] TabHelp =
        ["settings-peergos", "settings-capture", "settings-overlay", "settings-output", "settings-hotkeys", "settings-appearance", "settings-general"];

    void OpenHelp() => app.ShowHelp(Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < TabHelp.Length ? TabHelp[Tabs.SelectedIndex] : "settings");

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
        Server.Text = S.Server;
        Username.Text = S.Username;
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

        var schemes = new System.Windows.Data.ListCollectionView(Theme.Schemes);
        schemes.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Scheme.Group)));
        SchemeBox.ItemsSource = schemes;
        SchemeBox.SelectedItem = Theme.Schemes.FirstOrDefault(x => x.Id == S.ColorScheme) ?? Theme.Resolve(S.ColorScheme);
        FontSlider.Value = S.FontPercent;
        FontLabel.Text = $"Font size ({S.FontPercent} %)";
        PromptLocation.Text = S.PromptSourceLocation;

        Autostart.IsChecked = Services.Autostart.IsEnabled;
        AutoCheck.IsChecked = S.CheckForUpdates;
        AutoInstall.IsChecked = S.InstallUpdatesAutomatically;
        AutoInstall.IsEnabled = S.CheckForUpdates;
        UpdateResult.Text = $"You have version {Updater.Current}.";
        ShowNotes.IsChecked = S.ShowUserNotes;
        var ver = typeof(App).Assembly.GetName().Version;
        About.Text = $"Peergos Snap {ver?.ToString(3)} · GPL-3.0-or-later · https://github.com/anderlejan/peergos-snap\n" +
                     "Uses Peergos (AGPL-3.0), FFmpeg (GPL-3.0), OpenJDK runtime (GPL-2.0 with Classpath Exception) and Microsoft WebView2 SDK (BSD-3-Clause).";
    }

    void Wire()
    {
        Server.LostFocus += (_, _) => Change(s => s.Server = Server.Text);
        Username.TextChanged += (_, _) => { if (!S.PeergosConfigured) Change(s => s.Username = Username.Text.Trim()); };
        AccountFolder.LostFocus += (_, _) => Change(s => s.AccountFolder = AccountFolder.Text);
        TestBtn.Click += async (_, _) => await Test();
        SignInBtn.Click += async (_, _) => await SignIn();
        SignOutBtn.Click += (_, _) =>
        {
            Change(s => s.Session = "");
            TestResult.Text = "";
            SignInResult.Text = "Signed out. Captures are copied to the clipboard until you sign in again.";
            UpdateAccountPanels();
        };
        // Show / hide the password while typing it.
        ShowPassword.Checked += (_, _) =>
        {
            PasswordPlain.Text = Password.Password;
            PasswordPlain.Visibility = Visibility.Visible;
            Password.Visibility = Visibility.Collapsed;
            ShowPasswordGlyph.Text = "\uED1A";
            ShowPassword.ToolTip = "Hide the password";
            System.Windows.Automation.AutomationProperties.SetName(ShowPassword, "Hide password");
            PasswordPlain.Focus();
            PasswordPlain.CaretIndex = PasswordPlain.Text.Length;
        };
        ShowPassword.Unchecked += (_, _) =>
        {
            Password.Password = PasswordPlain.Text;
            PasswordPlain.Clear();
            Password.Visibility = Visibility.Visible;
            PasswordPlain.Visibility = Visibility.Collapsed;
            ShowPasswordGlyph.Text = "\uE7B3";
            ShowPassword.ToolTip = "Show the password";
            System.Windows.Automation.AutomationProperties.SetName(ShowPassword, "Show password");
            Password.Focus();
        };
        Password.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SignIn(); };
        PasswordPlain.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SignIn(); };
        HelpBtn.Click += (_, _) => OpenHelp();

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

        AutoCheck.Click += (_, _) => { Change(s => s.CheckForUpdates = AutoCheck.IsChecked == true); AutoInstall.IsEnabled = AutoCheck.IsChecked == true; };
        AutoInstall.Click += (_, _) => Change(s => s.InstallUpdatesAutomatically = AutoInstall.IsChecked == true);
        CheckUpdateBtn.Click += async (_, _) => await CheckUpdate();
        InstallUpdateBtn.Click += async (_, _) =>
        {
            if (found == null) return;
            var target = found;
            InstallUpdateBtn.IsEnabled = CheckUpdateBtn.IsEnabled = false;
            UpdateResult.Text = $"Downloading version {target.Version}…";
            var err = await app.InstallUpdateAsync(target, pct => Dispatcher.BeginInvoke(() =>
                UpdateResult.Text = pct >= 0 ? $"Downloading version {target.Version}… {pct} %" : $"Downloading version {target.Version}…"));
            if (err != null) { UpdateResult.Text = "✗ " + err; InstallUpdateBtn.IsEnabled = CheckUpdateBtn.IsEnabled = true; }
            else UpdateResult.Text = "Installing – Peergos Snap restarts in a moment.";
        };

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

    UpdateInfo? found;

    async Task CheckUpdate()
    {
        CheckUpdateBtn.IsEnabled = false;
        InstallUpdateBtn.Visibility = Visibility.Collapsed;
        UpdateResult.Text = "Checking…";
        try
        {
            found = await Updater.CheckAsync();
            if (found == null) UpdateResult.Text = $"✓ You have the newest version ({Updater.Current}).";
            else
            {
                UpdateResult.Text = $"Version {found.Version} is available (you have {Updater.Current}).";
                InstallUpdateBtn.Visibility = Visibility.Visible;
                InstallUpdateBtn.IsEnabled = true;
            }
        }
        catch (Exception e)
        {
            Log.Error("update check", e);
            UpdateResult.Text = "✗ Could not check: " + (e is System.Net.Http.HttpRequestException ? "no connection to GitHub" : e.Message);
        }
        finally { CheckUpdateBtn.IsEnabled = true; }
    }

    void UpdateAccountPanels()
    {
        bool signedIn = S.PeergosConfigured;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignInPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        var host = Uri.TryCreate(S.Server, UriKind.Absolute, out var u) ? u.Host : S.Server;
        SignedInText.Text = signedIn ? $"✓ Signed in as {S.Username} on {host}" : "";
    }

    string CurrentPassword() => ShowPassword.IsChecked == true ? PasswordPlain.Text : Password.Password;

    async Task SignIn()
    {
        var server = (Server.Text ?? "").Trim().TrimEnd('/');
        if (server.Length == 0) server = Settings.DefaultServer;
        if (!server.Contains("://")) server = "https://" + server;
        var user = Username.Text.Trim();
        var pw = CurrentPassword();
        SignInResult.Foreground = (System.Windows.Media.Brush)FindResource("Fg2");
        if (user.Length == 0) { SignInResult.Text = "Enter your Peergos username."; Username.Focus(); return; }
        if (pw.Length == 0) { SignInResult.Text = "Enter your password."; return; }
        if (!SignInBtn.IsEnabled) return;
        SignInBtn.IsEnabled = false;
        SignInResult.Text = "Signing in…";
        try
        {
            var r = await Uploader.SignInAsync(server, user, pw, () => Dispatcher.InvokeAsync(() => CodeDialog.Ask(this)).Task);
            if (r.Ok && !string.IsNullOrEmpty(r.Session))
            {
                Change(s => { s.Server = server; s.Username = user; s.Session = r.Session!; s.AccountPasswordProtected = ""; });
                Password.Clear();
                PasswordPlain.Clear();
                ShowPassword.IsChecked = false;
                SignInResult.Text = "";
                UpdateAccountPanels();
                TestResult.Foreground = (System.Windows.Media.Brush)FindResource("Acc");
                TestResult.Text = $"Captures go to /{user}/{S.AccountFolder.Trim('/')} and each gets its own secret link.";
            }
            else
            {
                SignInResult.Foreground = System.Windows.Media.Brushes.Firebrick;
                SignInResult.Text = "✗ " + (r.Error ?? "Sign-in failed");
            }
        }
        finally { SignInBtn.IsEnabled = true; }
    }

    async Task Test()
    {
        TestBtn.IsEnabled = false;
        TestResult.Foreground = (System.Windows.Media.Brush)FindResource("Fg2");
        TestResult.Text = "Connecting…";
        try
        {
            var r = await Uploader.CheckAsync(S.Clone());
            TestResult.Foreground = r.Ok ? (System.Windows.Media.Brush)FindResource("Acc") : System.Windows.Media.Brushes.Firebrick;
            TestResult.Text = r.Ok ? $"✓ Works – captures go to /{S.Username}/{S.AccountFolder.Trim('/')}" : "✗ " + r.Error;
            if (!r.Ok && Uploader.NeedsSignIn(r.Error)) { Change(s => s.Session = ""); UpdateAccountPanels(); SignInResult.Text = r.Error; }
        }
        finally { TestBtn.IsEnabled = true; }
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
