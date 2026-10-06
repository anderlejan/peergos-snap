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
        app.SettingsChanged += FollowChanges;
        Closed += (_, _) => app.SettingsChanged -= FollowChanges;
        UpdateAccountPanels();
        ShowHotkeyErrors();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) { e.Handled = true; OpenHelp(); } };
    }

    static readonly string[] TabHelp =
        ["settings-peergos", "settings-capture", "settings-overlay", "settings-output", "settings-files", "settings-direct", "settings-hotkeys", "settings-appearance", "settings-general"];

    void OpenHelp() => app.ShowHelp(Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < TabHelp.Length ? TabHelp[Tabs.SelectedIndex] : "settings");

    Settings S => app.Settings;

    /// <summary>Changes made elsewhere while this window is open (tray menu, hotkeys, the history's "Don't ask
    /// again"): the boxes follow, so a click here never just "confirms" a value that is no longer set.</summary>
    void FollowChanges(Settings before)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => FollowChanges(before)); return; }
        loading = true;
        try
        {
            ConfirmDelete.IsChecked = S.ConfirmHistoryDelete;
            AskPicture.IsChecked = S.AskBeforePictureUpload;
            AnnotateAfter.IsChecked = S.AnnotateAfterPicture;
            RecordCursor.IsChecked = S.RecordCursor;
            RecordSound.IsChecked = S.RecordSound;
            KindPicture.IsChecked = S.Mode == TrayMode.Picture;
            KindVideo.IsChecked = S.Mode == TrayMode.Video;
            KindFiles.IsChecked = S.Mode == TrayMode.Files;
            KindFolders.IsChecked = S.Mode == TrayMode.Folders;
            OutUploadMedia.IsChecked = S.Output == OutputMode.UploadAndMedia;
            OutLink.IsChecked = S.Output == OutputMode.SecretLink;
            OutMedia.IsChecked = S.Output == OutputMode.DirectMedia;
            ShutterSoundBox.IsChecked = S.ShutterSound;
            DirectDrawFirst.IsChecked = S.DirectDrawFirst;
            if (!DelayBox.IsKeyboardFocusWithin) DelayBox.Text = S.DelaySeconds.ToString();
        }
        finally { loading = false; }
        if (before.PeergosConfigured != S.PeergosConfigured) UpdateAccountPanels();
    }

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

        KindPicture.IsChecked = S.Mode == TrayMode.Picture;
        KindVideo.IsChecked = S.Mode == TrayMode.Video;
        KindFiles.IsChecked = S.Mode == TrayMode.Files;
        KindFolders.IsChecked = S.Mode == TrayMode.Folders;
        ShutterSoundBox.IsChecked = S.ShutterSound;
        Select(ImageFormat, S.ImageFormat);
        Select(VideoFormat, S.VideoFormat);
        Select(FrameRate, S.FrameRate.ToString());
        Quality.Value = S.VideoQuality;
        QualityLabel.Text = $"Video quality (CRF {S.VideoQuality})";
        RecordCursor.IsChecked = S.RecordCursor;
        RecordSound.IsChecked = S.RecordSound;
        DelayBox.Text = S.DelaySeconds.ToString();
        foreach (ComboBoxItem i in SubfolderBox.Items)
            if ((string)i.Tag == S.Subfolders.ToString()) SubfolderBox.SelectedItem = i;
        RememberApp.IsChecked = S.RememberApp;
        foreach (ComboBoxItem i in HistoryDeleteBox.Items)
            if ((string)i.Tag == S.HistoryDeleteAction.ToString()) HistoryDeleteBox.SelectedItem = i;
        BothRemovesEntry.IsChecked = S.DeleteBothRemovesEntry;
        AskPicture.IsChecked = S.AskBeforePictureUpload;
        AnnotateAfter.IsChecked = S.AnnotateAfterPicture;
        ConfirmDelete.IsChecked = S.ConfirmHistoryDelete;

        Dim.Value = S.OverlayDimPercent;
        DimLabel.Text = $"Darken outside the selection ({S.OverlayDimPercent} %; 0 = fully transparent)";
        BorderColor.Text = S.BorderColor;
        ShowSize.IsChecked = S.ShowSizeLabel;
        PickWindow.IsChecked = S.PickWindowOnClick;
        Delay.Text = S.CaptureDelayMs.ToString();

        OutUploadMedia.IsChecked = S.Output == OutputMode.UploadAndMedia;
        OutLink.IsChecked = S.Output == OutputMode.SecretLink;
        OutMedia.IsChecked = S.Output == OutputMode.DirectMedia;
        Fallback.IsChecked = S.FallbackToClipboard;
        Notifications.IsChecked = S.Notifications;
        ToastBox.Items.Clear();
        foreach (var sec in ToastLogic.Choices.Append(S.ToastSeconds).Distinct())
            ToastBox.Items.Add(new ComboBoxItem { Content = ToastLogic.Describe(sec), Tag = sec });
        foreach (ComboBoxItem i in ToastBox.Items)
            if ((int)i.Tag == S.ToastSeconds) ToastBox.SelectedItem = i;
        MirrorOn.IsChecked = S.MirrorEnabled;
        MirrorFolder.Text = S.MirrorFolder;
        CachePath.Text = AppPaths.CacheDir;
        KeepDays.Text = S.CacheKeepDays.ToString();
        foreach (ComboBoxItem i in KeepCopiesBox.Items)
            if ((string)i.Tag == S.KeepLocalCopies.ToString()) KeepCopiesBox.SelectedItem = i;
        DiscardPerm.IsChecked = S.DiscardPermanently;

        DirectReceive.IsChecked = S.DirectReceive;
        DirectFront.IsChecked = S.DirectBringToFront;
        DirectKeepOnPc.IsChecked = S.DirectKeepOnPc;
        DirectFlash.IsChecked = S.DirectFlash;
        DirectSeconds.Text = S.DirectCheckSeconds.ToString();
        DirectDrawFirst.IsChecked = S.DirectDrawFirst;
        ExplorerMenuBox.IsChecked = S.ExplorerMenu;
        ExplorerTopBox.IsChecked = S.ExplorerMenuTop;
        ShowExplorerTop();
        DirectInfo.Text = S.DirectFriends.Count == 0 ? "No friends set up yet: open the direct window and click Friends…"
            : "Sharing directly with " + string.Join(", ", S.DirectFriends) + ".";

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
                     "Uses Peergos (AGPL-3.0), FFmpeg (GPL-3.0), OpenJDK runtime (GPL-2.0 with Classpath Exception), NAudio (MIT) and Microsoft WebView2 SDK (BSD-3-Clause).";
    }

    void Wire()
    {
        Server.LostFocus += (_, _) => Change(s => s.Server = Server.Text);
        Username.TextChanged += (_, _) => { if (!S.PeergosConfigured) Change(s => s.Username = Username.Text.Trim()); };
        AccountFolder.LostFocus += (_, _) =>
        {
            if (DirectFolderRefused(AccountFolder.Text)) { AccountFolder.Text = S.AccountFolder; return; }
            Change(s => s.AccountFolder = AccountFolder.Text);
        };
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

        KindPicture.Checked += (_, _) => Change(s => s.Mode = TrayMode.Picture);
        KindVideo.Checked += (_, _) => Change(s => s.Mode = TrayMode.Video);
        KindFiles.Checked += (_, _) => Change(s => s.Mode = TrayMode.Files);
        KindFolders.Checked += (_, _) => Change(s => s.Mode = TrayMode.Folders);
        ShutterSoundBox.Click += (_, _) => Change(s => s.ShutterSound = ShutterSoundBox.IsChecked == true);
        ShutterPlay.Click += (_, _) => PeergosSnap.Services.ShutterSound.Play();
        ImageFormat.SelectionChanged += (_, _) => Change(s => s.ImageFormat = Sel(ImageFormat));
        VideoFormat.SelectionChanged += (_, _) => Change(s => s.VideoFormat = Sel(VideoFormat));
        FrameRate.SelectionChanged += (_, _) => Change(s => s.FrameRate = int.Parse(Sel(FrameRate)));
        Quality.ValueChanged += (_, _) => { QualityLabel.Text = $"Video quality (CRF {(int)Quality.Value})"; Change(s => s.VideoQuality = (int)Quality.Value); };
        RecordCursor.Click += (_, _) => Change(s => s.RecordCursor = RecordCursor.IsChecked == true);
        RecordSound.Click += (_, _) => Change(s => s.RecordSound = RecordSound.IsChecked == true);
        DelayBox.TextChanged += (_, _) =>
        {
            bool ok = int.TryParse(DelayBox.Text.Trim(), out var sec) && sec is >= 0 and <= 60;
            DelayBoxErr.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) Change(s => s.DelaySeconds = sec);
        };
        SubfolderBox.SelectionChanged += (_, _) =>
        {
            if (SubfolderBox.SelectedItem is ComboBoxItem { Tag: string t } && Enum.TryParse<SubfolderScheme>(t, out var scheme))
                Change(s => s.Subfolders = scheme);
        };
        RememberApp.Click += (_, _) => Change(s => s.RememberApp = RememberApp.IsChecked == true);
        HistoryDeleteBox.SelectionChanged += (_, _) =>
        {
            if (HistoryDeleteBox.SelectedItem is ComboBoxItem { Tag: string t } && Enum.TryParse<HistoryDelete>(t, out var d))
                Change(s => s.HistoryDeleteAction = d);
        };
        BothRemovesEntry.Click += (_, _) => Change(s => s.DeleteBothRemovesEntry = BothRemovesEntry.IsChecked == true);
        OpenHistoryBtn.Click += (_, _) => app.ShowHistory();
        AskPicture.Click += (_, _) => Change(s => s.AskBeforePictureUpload = AskPicture.IsChecked == true);
        AnnotateAfter.Click += (_, _) => Change(s => s.AnnotateAfterPicture = AnnotateAfter.IsChecked == true);
        ConfirmDelete.Click += (_, _) => Change(s => s.ConfirmHistoryDelete = ConfirmDelete.IsChecked == true);
        WireBrowse();

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

        OutUploadMedia.Checked += (_, _) => Change(s => s.Output = OutputMode.UploadAndMedia);
        OutLink.Checked += (_, _) => Change(s => s.Output = OutputMode.SecretLink);
        OutMedia.Checked += (_, _) => Change(s => s.Output = OutputMode.DirectMedia);
        Fallback.Click += (_, _) => Change(s => s.FallbackToClipboard = Fallback.IsChecked == true);
        Notifications.Click += (_, _) => Change(s => s.Notifications = Notifications.IsChecked == true);
        ToastBox.SelectionChanged += (_, _) => { if (ToastBox.SelectedItem is ComboBoxItem { Tag: int sec }) Change(s => s.ToastSeconds = sec); };
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
        KeepCopiesBox.SelectionChanged += (_, _) =>
        {
            if (KeepCopiesBox.SelectedItem is ComboBoxItem { Tag: string t } && Enum.TryParse<LocalCopies>(t, out var k))
                Change(s => s.KeepLocalCopies = k);
        };
        DiscardPerm.Click += (_, _) => Change(s => s.DiscardPermanently = DiscardPerm.IsChecked == true);
        KeepDays.TextChanged += (_, _) => { if (int.TryParse(KeepDays.Text, out var d) && d >= 0) Change(s => s.CacheKeepDays = d); };

        OpenDirectBtn.Click += (_, _) => app.Direct.ShowWindow(null, null);
        DirectReceive.Click += (_, _) => Change(s => s.DirectReceive = DirectReceive.IsChecked == true);
        DirectFront.Click += (_, _) => Change(s => s.DirectBringToFront = DirectFront.IsChecked == true);
        DirectKeepOnPc.Click += (_, _) => Change(s => s.DirectKeepOnPc = DirectKeepOnPc.IsChecked == true);
        DirectFlash.Click += (_, _) => Change(s => s.DirectFlash = DirectFlash.IsChecked == true);
        DirectDrawFirst.Click += (_, _) => Change(s => s.DirectDrawFirst = DirectDrawFirst.IsChecked == true);
        DirectSeconds.TextChanged += (_, _) =>
        {
            bool ok = int.TryParse(DirectSeconds.Text.Trim(), out var sec) && sec is >= 2 and <= 60;
            DirectSecondsErr.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (ok) Change(s => s.DirectCheckSeconds = sec);
        };

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
        ExplorerMenuBox.Click += (_, _) => { Change(s => s.ExplorerMenu = ExplorerMenuBox.IsChecked == true); ShowExplorerTop(); };
        ExplorerTopBox.Click += (_, _) => { Change(s => s.ExplorerMenuTop = ExplorerTopBox.IsChecked == true); ShowExplorerTop(); };
        ExplorerSetupBtn.Click += async (_, _) =>
        {
            // Registering an unsigned package needs administrator rights: Windows asks once.
            ExplorerSetupBtn.IsEnabled = false;
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"{Services.ExplorerPackage.Argument} install")
                    { UseShellExecute = true, Verb = "runas" });
                if (p != null) await p.WaitForExitAsync();
            }
            catch (System.ComponentModel.Win32Exception) { } // the administrator question was declined
            finally { ExplorerSetupBtn.IsEnabled = true; }
            app.RefreshExplorerMenu();
            ShowExplorerTop();
        };
        TrayIconBtn.Click += (_, _) => Shell("ms-settings:taskbar");
        ExportBtn.Click += (_, _) => new SettingsTransferWindow(app, null, null) { Owner = this }.ShowDialog();
        ImportBtn.Click += (_, _) => ImportSettings();
        OpenNotes.Click += (_, _) => app.ShowNotes();
        OpenLog.Click += (_, _) => { if (File.Exists(AppPaths.LogFile)) Shell(AppPaths.LogFile); };
        OpenLicenses.Click += (_, _) =>
        {
            var f = Path.Combine(AppPaths.AppDir, "licenses", "THIRD-PARTY-NOTICES.md");
            Shell(File.Exists(f) ? f : Path.Combine(AppPaths.AppDir, "licenses"));
        };
    }

    /// <summary>Settings → General → Import settings…: choose a file, then the groups; the page shows the new values.</summary>
    void ImportSettings()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import settings", Filter = "Settings file (*.json)|*.json|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (d.ShowDialog(this) != true) return;
        SettingsTransfer.Imported file;
        try { file = SettingsTransfer.Read(File.ReadAllText(d.FileName)); }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Peergos Snap", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (file.Groups.Count == 0)
        {
            MessageBox.Show(this, "The file has no settings Peergos Snap knows.", "Peergos Snap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new SettingsTransferWindow(app, file, Path.GetFileName(d.FileName)) { Owner = this };
        w.ShowDialog();
        if (!w.Imported) return;
        loading = true;
        try { Load(); }
        finally { loading = false; }
        UpdateAccountPanels();
        ShowHotkeyErrors();
    }

    // ---------- choosing the Peergos folder ----------

    string browseAt = "";

    void WireBrowse()
    {
        BrowseFolderBtn.Click += async (_, _) =>
        {
            if (!S.PeergosConfigured) { BrowsePanel.Visibility = Visibility.Visible; BrowseState.Text = "Sign in above first: browsing needs your Peergos account."; return; }
            BrowsePanel.Visibility = Visibility.Visible;
            // Start where the current folder is (its parent, when it does not exist yet).
            await BrowseTo(S.AccountFolder.Trim('/'));
        };
        BrowseClose.Click += (_, _) => BrowsePanel.Visibility = Visibility.Collapsed;
        BrowseUp.Click += async (_, _) => await BrowseTo(PeergosFolders.Parent(browseAt));
        BrowseList.MouseDoubleClick += async (_, _) => { if (BrowseList.SelectedItem is string f) await BrowseTo(PeergosFolders.Join(browseAt, f)); };
        BrowseList.KeyDown += async (_, e) => { if (e.Key == Key.Enter && BrowseList.SelectedItem is string f) await BrowseTo(PeergosFolders.Join(browseAt, f)); };
        BrowseChoose.Click += (_, _) =>
        {
            // A selected folder in the list is the choice; otherwise the folder being shown.
            var pick = BrowseList.SelectedItem is string f ? PeergosFolders.Join(browseAt, f) : browseAt;
            if (pick.Length == 0) { BrowseState.Text = "Choose a folder: captures cannot go directly into your home folder."; return; }
            UseFolder(pick);
        };
        BrowseNew.Click += (_, _) =>
        {
            var name = PeergosFolders.CleanName(BrowseNewName.Text);
            if (name == null) { BrowseState.Text = "Type a folder name first (no / or \\)."; return; }
            // Created in Peergos with the first upload into it.
            UseFolder(PeergosFolders.Join(browseAt, name));
        };
    }

    /// <summary>The folders of direct sharing are never the capture folder: says so and returns true.</summary>
    bool DirectFolderRefused(string rel)
    {
        if (!PeergosFolders.IsDirectFolder(rel)) return false;
        TestResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        TestResult.Text = "PeergosSnap/Direct holds what you share directly with friends; captures cannot go there. Choose another folder.";
        return true;
    }

    void UseFolder(string rel)
    {
        if (DirectFolderRefused(rel)) return;
        AccountFolder.Text = rel;
        Change(s => s.AccountFolder = rel);
        BrowsePanel.Visibility = Visibility.Collapsed;
        TestResult.Foreground = (System.Windows.Media.Brush)FindResource("Acc");
        TestResult.Text = $"Captures now go to /{S.Username}/{S.AccountFolder}. Earlier captures stay where they are; History shows the files of this folder.";
    }

    async Task BrowseTo(string rel)
    {
        BrowseState.Text = "Looking in your Peergos…";
        BrowseList.IsEnabled = BrowseUp.IsEnabled = BrowseChoose.IsEnabled = false;
        try
        {
            var (r, path, exists, folders) = await Uploader.FoldersAsync(S.Clone(), rel);
            if (!r.Ok)
            {
                BrowseState.Text = "✗ " + r.Error;
                if (Uploader.NeedsSignIn(r.Error)) { Change(s => s.Session = ""); UpdateAccountPanels(); }
                return;
            }
            if (!exists && rel.Length > 0) { await BrowseTo(PeergosFolders.Parent(rel)); return; } // not there (yet): show its parent
            browseAt = rel;
            BrowsePath.Text = path;
            BrowseList.ItemsSource = folders;
            BrowseState.Text = folders.Count == 0 ? "No folders here. Use this folder, or make a new one." : "Double-click a folder to open it, or select it and click 'Use this folder'.";
        }
        finally { BrowseList.IsEnabled = BrowseUp.IsEnabled = BrowseChoose.IsEnabled = true; BrowseUp.IsEnabled = browseAt.Length > 0; }
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

    /// <summary>Windows 11's menu: whether the package is there, and what to do when it is not.</summary>
    void ShowExplorerTop()
    {
        bool supported = Services.ExplorerPackage.Supported, ready = supported && Services.ExplorerPackage.Registered();
        ExplorerTopBox.IsEnabled = S.ExplorerMenu && supported;
        ExplorerSetupBtn.Visibility = S.ExplorerMenu && S.ExplorerMenuTop && supported && !ready ? Visibility.Visible : Visibility.Collapsed;
        ExplorerTopInfo.Text = !supported ? "Only Windows 11 has this; on Windows 10 Peergos Snap is in the classic menu."
            : ready && !S.ExplorerMenuTop ? "Switched off: Peergos Snap is under 'Show more options' (or Shift + right-click)."
            : ready ? "Ready: right-click a file or folder – Peergos Snap is at the top of the menu."
            : "This needs a small Windows package naming Peergos Snap as the menu's handler. The installer adds it when Peergos Snap is installed for all users; Set it up… adds it now (Windows asks for administrator rights once). Until then the menu is under 'Show more options' (or Shift + right-click).";
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
