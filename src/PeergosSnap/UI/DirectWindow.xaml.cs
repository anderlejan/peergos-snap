using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap.UI;

/// <summary>One picture in the direct window's list.</summary>
public sealed class DirectRow(DirectItem item, string me, string? localFile)
{
    ImageSource? thumb;
    string? thumbOf;
    public DirectItem Item { get; } = item;
    public string? LocalFile { get; set; } = localFile;
    /// <summary>Loaded once per file (the list is refreshed often).</summary>
    public ImageSource? Thumb
    {
        get
        {
            if (LocalFile == null || !Item.IsImage) return null;
            if (thumbOf != LocalFile) { thumb = ThumbFiles.Load(LocalFile, 180); thumbOf = LocalFile; }
            return thumb;
        }
    }
    public string Glyph => Item.IsImage ? "" : "";
    public string Title => Item.Label.Trim().Length > 0 ? Item.Label.Trim() : Item.Name;
    public string Line2 => (string.Equals(Item.From, me, StringComparison.OrdinalIgnoreCase) ? "You" : Item.From) + " · "
                           + Item.Modified.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                           + (Item.Label.Trim().Length > 0 ? " · " + Item.Name : "");
    public string Badges => (Item.Pinned ? "📌 " : "") + (Item.Stars.Count > 0 ? "★ " + DirectLogic.StarredBy(Item.Stars, me) : "");
}

/// <summary>
/// The direct window: send pictures to a friend by dropping, pasting or choosing them, and see what they send at once.
/// Both sides can zoom, save, label, pin, star and delete every picture.
/// </summary>
public partial class DirectWindow : Window
{
    readonly DirectHub hub;
    readonly TrayController app;
    readonly DispatcherTimer labelSave = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly List<DirectRow> rows = [];
    string? friend;
    string month = DirectLogic.MonthOf(DateTime.Now);
    List<string> months = [];
    List<DirectItem>? otherMonth; // the list of a month that is not watched (no live updates)
    string? wantedPath;
    bool loadingFriend, showing, sending;
    double fitScale = 1;
    string? shownFile; // the picture in the viewer (kept, with its zoom, when only its label or stars change)
    Point? dragFrom;
    Point dragOffset;

    string Me => hub.Me;
    DirectRow? Current => List.SelectedItem as DirectRow;

    public DirectWindow(DirectHub hub, TrayController app)
    {
        this.hub = hub;
        this.app = app;
        InitializeComponent();
        Theme.Attach(this);
        hub.Updated += HubUpdated;
        Closed += (_, _) => hub.Updated -= HubUpdated;

        FriendBox.SelectionChanged += (_, _) => { if (!loadingFriend && FriendBox.SelectedItem is string f) SelectFriend(f); };
        MonthBox.SelectionChanged += async (_, _) => { if (!loadingFriend && MonthBox.SelectedItem is string m && m != month) await SelectMonth(m); };
        RefreshBtn.Click += async (_, _) => await Reload();
        FriendsBtn.Click += async (_, _) => await ToggleFriends(true);
        FriendsClose.Click += async (_, _) => await ToggleFriends(false);
        AddBtn.Click += async (_, _) => await AddFriend();
        AddBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await AddFriend(); };
        BringFront.Click += (_, _) => app.UpdateSettings(s => s.DirectBringToFront = BringFront.IsChecked == true);

        ChooseBtn.Click += async (_, _) => await Choose();
        PasteBtn.Click += async (_, _) => await Paste();
        CaptureBtn.Click += async (_, _) =>
        {
            if (friend == null) return;
            WindowState = WindowState.Minimized; // out of the way while selecting
            await Task.Delay(250);
            await app.CapturePicture(toFriend: friend);
            WindowState = WindowState.Normal;
        };
        DragEnter += (_, e) => { e.Effects = CanTake(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; DropFrame.StrokeThickness = 4; e.Handled = true; };
        DragOver += (_, e) => { e.Effects = CanTake(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragLeave += (_, _) => DropFrame.StrokeThickness = 2;
        Drop += async (_, e) => { DropFrame.StrokeThickness = 2; await Take(e.Data); };
        PreviewKeyDown += async (_, e) => await Keys(e);

        List.SelectionChanged += async (_, _) => await ShowCurrent();
        List.MouseDoubleClick += (_, _) => OpenCurrent();
        ZoomInBtn.Click += (_, _) => SetZoom(Zoom.ScaleX * 1.25);
        ZoomOutBtn.Click += (_, _) => SetZoom(Zoom.ScaleX / 1.25);
        FitBtn.Click += (_, _) => SetZoom(fitScale = FitScale(allowLarger: true));
        ActualBtn.Click += (_, _) => SetZoom(1);
        Viewer.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(Zoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15));
            e.Handled = true;
        };
        // Drag the picture to move around when it is larger than the window.
        Picture.MouseLeftButtonDown += (_, e) => { dragFrom = e.GetPosition(Viewer); dragOffset = new Point(Viewer.HorizontalOffset, Viewer.VerticalOffset); Picture.CaptureMouse(); };
        Picture.MouseMove += (_, e) =>
        {
            if (dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(Viewer);
            Viewer.ScrollToHorizontalOffset(dragOffset.X - (p.X - from.X));
            Viewer.ScrollToVerticalOffset(dragOffset.Y - (p.Y - from.Y));
        };
        Picture.MouseLeftButtonUp += (_, _) => { dragFrom = null; Picture.ReleaseMouseCapture(); };
        Viewer.SizeChanged += (_, _) => { if (Math.Abs(Zoom.ScaleX - fitScale) < 0.001) SetZoom(fitScale = FitScale(allowLarger: false)); };

        StarBtn.Click += async (_, _) => await Meta(star: StarBtn.IsChecked == true);
        PinBtn.Click += async (_, _) => await Meta(pin: PinBtn.IsChecked == true);
        LabelBox.TextChanged += (_, _) => { if (!showing) { labelSave.Stop(); labelSave.Start(); } };
        labelSave.Tick += async (_, _) => { labelSave.Stop(); await Meta(label: LabelBox.Text.Trim()); };
        SaveBtn.Click += (_, _) => SaveAs();
        CopyBtn.Click += (_, _) =>
        {
            if (Current?.LocalFile is not { } f) return;
            try { ClipboardService.MediaToClipboard(f); Status("Copied to the clipboard"); }
            catch (Exception e) { Status("Could not copy: " + e.Message); }
        };
        OpenBtn.Click += (_, _) => OpenCurrent();
        DeleteBtn.Click += (_, _) => AskDelete();
        ConfirmNo.Click += (_, _) => ConfirmBar.Visibility = Visibility.Collapsed;
        ConfirmYes.Click += async (_, _) => await Delete();

        SetZoom(1);
        BringFront.IsChecked = app.Settings.DirectBringToFront;
    }

    // ---------- showing ----------

    /// <summary>Shows the window for a friend (and a picture). Without <paramref name="activate"/> it comes to the front
    /// but does not take the keyboard from the app the user is typing in.</summary>
    public void ShowFor(string? who, string? path, bool activate)
    {
        wantedPath = path;
        if (!IsVisible)
        {
            ShowActivated = activate;
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
        else
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | 0x2 /*NOMOVE*/);
            Native.SetWindowPos(h, new IntPtr(-2) /*NOTOPMOST*/, 0, 0, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | 0x2);
        }
        FillFriends(who);
        if (who != null && who != friend) SelectFriend(who);
        else Rebuild();
        if (hub.Friends.Count == 0) _ = ToggleFriends(true);
    }

    void HubUpdated()
    {
        FillFriends(friend);
        if (otherMonth == null) Rebuild();
        if (FriendsPanel.Visibility == Visibility.Visible) FillFriendsPanel();
        Status(hub.Status);
    }

    void FillFriends(string? select)
    {
        loadingFriend = true;
        var list = hub.Friends.ToList();
        if (!FriendBox.Items.Cast<string>().SequenceEqual(list))
        {
            FriendBox.Items.Clear();
            foreach (var f in list) FriendBox.Items.Add(f);
        }
        var want = select ?? friend;
        if (want != null && list.Contains(want)) FriendBox.SelectedItem = want;
        else if (FriendBox.SelectedItem == null && list.Count > 0) FriendBox.SelectedIndex = 0;
        loadingFriend = false;
        if (friend == null && FriendBox.SelectedItem is string f0) SelectFriend(f0);
        UpdateDropZone();
    }

    void SelectFriend(string f)
    {
        friend = f;
        loadingFriend = true;
        FriendBox.SelectedItem = f;
        loadingFriend = false;
        Title = $"Peergos Snap – Direct with {f}";
        otherMonth = null;
        month = hub.WatchMonth;
        UpdateDropZone();
        Rebuild();
        _ = LoadMonths();
    }

    async Task LoadMonths()
    {
        if (friend == null) return;
        try
        {
            var (items, ms) = await hub.ListAsync(friend, hub.WatchMonth);
            months = ms;
            if (!months.Contains(hub.WatchMonth)) months.Insert(0, hub.WatchMonth);
            loadingFriend = true;
            MonthBox.Items.Clear();
            foreach (var m in months) MonthBox.Items.Add(m);
            MonthBox.SelectedItem = month;
            loadingFriend = false;
            Rebuild();
        }
        catch (DirectException e) { Status(e.Message); }
    }

    async Task SelectMonth(string m)
    {
        if (friend == null) return;
        month = m;
        if (m == hub.WatchMonth) { otherMonth = null; Rebuild(); return; }
        Status($"Loading {m}…");
        try
        {
            otherMonth = (await hub.ListAsync(friend, m)).Items;
            Rebuild();
            Status($"{m}: this month is not watched for changes – use Refresh");
        }
        catch (DirectException e) { Status(e.Message); }
    }

    async Task Reload()
    {
        if (friend == null) { await hub.StartAsync(); return; }
        try
        {
            var (items, ms) = await hub.ListAsync(friend, month);
            if (month != hub.WatchMonth) otherMonth = items;
            Rebuild();
            Status("Up to date");
        }
        catch (DirectException e) { Status(e.Message); }
    }

    void UpdateDropZone()
    {
        bool can = friend != null && !sending;
        DropTitle.Text = friend == null ? "Add a friend to share pictures directly" : $"Send a picture to {friend}";
        ChooseBtn.IsEnabled = PasteBtn.IsEnabled = CaptureBtn.IsEnabled = can;
    }

    /// <summary>Rebuilds the list (pinned first, then newest), keeping the selection.</summary>
    void Rebuild()
    {
        var keep = wantedPath ?? Current?.Item.Path;
        var source = friend == null ? [] : otherMonth ?? hub.Items(friend).ToList();
        rows.Clear();
        foreach (var it in DirectLogic.Sort(source))
        {
            var local = DirectLogic.CacheFile(AppPaths.DirectDir, friend!, it);
            rows.Add(new DirectRow(it, Me, File.Exists(local) ? local : null));
        }
        List.ItemsSource = null;
        List.ItemsSource = rows;
        // The same picture stays selected; if it is gone (deleted), the first one.
        var sel = rows.FirstOrDefault(r => r.Item.Path == keep) ?? rows.FirstOrDefault();
        if (sel != null) { List.SelectedItem = sel; List.ScrollIntoView(sel); }
        wantedPath = null;
        Empty.Text = friend == null ? "No friend yet. Click Friends… to add one." : $"Nothing shared with {friend} in {month} yet. Drop a picture above.";
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = rows.Count == 0 ? "" : $"{rows.Count} in {month} · {rows.Count(r => r.Item.From != Me)} from {friend}";
        _ = FetchThumbs();
        if (rows.Count == 0) _ = ShowCurrent();
    }

    bool fetching;

    /// <summary>Downloads the pictures of the list that are not on this PC yet (one after the other).</summary>
    async Task FetchThumbs()
    {
        if (friend == null || fetching) return;
        fetching = true;
        try { await FetchThumbsOnce(friend); }
        finally { fetching = false; }
    }

    async Task FetchThumbsOnce(string f)
    {
        foreach (var r in rows.Where(r => r.LocalFile == null && r.Item.IsImage && r.Item.Size < 40_000_000).ToList())
        {
            try
            {
                r.LocalFile = await hub.LocalFileAsync(f, r.Item);
                if (f != friend) return;
                List.Items.Refresh();
                if (Current == r) await ShowCurrent();
            }
            catch (DirectException) { }
        }
    }

    async Task ShowCurrent()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        var r = Current;
        showing = true;
        try
        {
            bool any = r != null;
            foreach (var b in new UIElement[] { StarBtn, PinBtn, SaveBtn, CopyBtn, OpenBtn, DeleteBtn, LabelBox, ZoomInBtn, ZoomOutBtn, FitBtn, ActualBtn })
                b.IsEnabled = any;
            if (r == null)
            {
                Picture.Source = null;
                shownFile = null;
                ViewerNote.Text = friend == null ? "" : "Select a picture";
                if (!LabelBox.IsKeyboardFocused) LabelBox.Text = "";
                InfoText.Text = "";
                return;
            }
            var it = r.Item;
            StarBtn.IsChecked = it.Stars.Contains(Me);
            StarBtn.Content = StarBtn.IsChecked == true ? "★ Starred" : "☆ Star";
            PinBtn.IsChecked = it.Pinned;
            PinBtn.Content = it.Pinned ? "📌 Pinned" : "📌 Pin";
            if (!LabelBox.IsKeyboardFocused) LabelBox.Text = it.Label;
            InfoText.Text = $"{(it.From == Me ? "You" : it.From)} · {HistoryLogic.Size(it.Size)}"
                            + (it.Stars.Count > 0 ? " · ★ " + DirectLogic.StarredBy(it.Stars, Me) : "");
            if (!it.IsImage)
            {
                Picture.Source = null;
                shownFile = null;
                ViewerNote.Text = $"{it.Name}\nNo preview for this kind of file – use Open or Save as.";
                return;
            }
            if (r.LocalFile == null)
            {
                Picture.Source = null;
                ViewerNote.Text = "Loading the picture…";
                try { r.LocalFile = await hub.LocalFileAsync(friend!, it); }
                catch (DirectException e) { ViewerNote.Text = "Could not load it: " + e.Message; return; }
                if (Current != r) return;
            }
            if (shownFile == r.LocalFile && Picture.Source != null) return; // same picture: keep it and its zoom
            var img = await Task.Run(() => LoadFull(r.LocalFile!));
            if (Current != r) return;
            Picture.Source = img;
            shownFile = img == null ? null : r.LocalFile;
            ViewerNote.Text = img == null ? "This picture cannot be shown" : "";
            if (img != null)
            {
                Picture.Width = img.PixelWidth;
                Picture.Height = img.PixelHeight;
                UpdateLayout();
                SetZoom(fitScale = FitScale(allowLarger: false));
            }
        }
        finally { showing = false; }
    }

    static BitmapSource? LoadFull(string file)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(file);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // ---------- zoom ----------

    double FitScale(bool allowLarger)
    {
        if (Picture.Source is not BitmapSource b || b.PixelWidth == 0) return 1;
        double w = Math.Max(50, Viewer.ViewportWidth - 8), h = Math.Max(50, Viewer.ViewportHeight - 8);
        var s = Math.Min(w / b.PixelWidth, h / b.PixelHeight);
        return allowLarger ? s : Math.Min(1, s);
    }

    void SetZoom(double s)
    {
        s = Math.Clamp(s, 0.05, 16);
        Zoom.ScaleX = Zoom.ScaleY = s;
        ZoomText.Text = $"{s * 100:0} %";
    }

    // ---------- sending ----------

    static bool CanTake(IDataObject d) => d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.Bitmap);

    async Task Take(IDataObject d)
    {
        if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] files)
            await Send(files.Where(File.Exists).ToList());
        else if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            await Send([SavePng(bmp)]);
    }

    async Task Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                await Send(Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                await Send([SavePng(img)]);
            else Status("The clipboard has no picture or file");
        }
        catch (Exception e) { Status("Could not paste: " + e.Message); }
    }

    /// <summary>A pasted or dropped picture becomes a PNG named like a capture.</summary>
    static string SavePng(BitmapSource bmp)
    {
        var dir = Path.Combine(AppPaths.WorkDir, "direct-paste");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, FileNames.Unique(dir, FileNames.ForCapture(DateTime.Now, "png")));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }

    async Task Choose()
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, Title = $"Pictures for {friend}",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Videos|*.mp4;*.webm|All files|*.*",
        };
        if (d.ShowDialog(this) == true) await Send(d.FileNames.ToList());
    }

    async Task Send(List<string> files)
    {
        if (friend == null || files.Count == 0 || sending) return;
        sending = true;
        UpdateDropZone();
        try
        {
            var sent = await hub.SendAsync(friend, files, t => SendState.Text = t);
            SendState.Text = sent.Count == 1 ? $"Sent – {friend} sees it now." : $"{sent.Count} sent – {friend} sees them now.";
            if (month != hub.WatchMonth) await SelectMonthBack();
            wantedPath = sent.LastOrDefault()?.Path;
            Rebuild();
        }
        catch (DirectException e) { SendState.Text = "Not sent: " + e.Message; }
        finally
        {
            sending = false;
            UpdateDropZone();
        }
    }

    async Task SelectMonthBack()
    {
        otherMonth = null;
        month = hub.WatchMonth;
        loadingFriend = true;
        MonthBox.SelectedItem = month;
        loadingFriend = false;
        await Task.CompletedTask;
    }

    async Task Keys(KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !typing) { e.Handled = true; await Paste(); return; }
        if (typing) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: SetZoom(Zoom.ScaleX * 1.25); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract: SetZoom(Zoom.ScaleX / 1.25); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: SetZoom(1); e.Handled = true; break;
            case Key.F: SetZoom(fitScale = FitScale(allowLarger: true)); e.Handled = true; break;
            case Key.Delete: if (Current != null) { AskDelete(); e.Handled = true; } break;
            case Key.Escape:
                e.Handled = true;
                if (ConfirmBar.Visibility == Visibility.Visible) ConfirmBar.Visibility = Visibility.Collapsed;
                else if (FriendsPanel.Visibility == Visibility.Visible) await ToggleFriends(false);
                break;
        }
    }

    // ---------- labels, pins, stars, delete ----------

    async Task Meta(string? label = null, bool? pin = null, bool? star = null)
    {
        if (Current is not { } r || friend == null) return;
        if (label != null && label == r.Item.Label) return;
        try
        {
            await hub.SetMetaAsync(friend, r.Item, label, pin, star);
            Status(label != null ? "Label saved – your friend sees it" : pin != null ? (pin.Value ? "Pinned for both of you" : "Unpinned")
                : star == true ? "Starred" : "Star removed");
        }
        catch (DirectException e) { Status("Not saved: " + e.Message); }
    }

    void AskDelete()
    {
        if (Current is not { } r) return;
        ConfirmText.Text = r.Item.From == Me
            ? $"Delete “{r.Item.Name}” for both of you? It is removed from your Peergos, so {friend} no longer sees it; copies saved elsewhere stay."
            : $"Delete “{r.Item.Name}” on your side? It is removed from this PC and no longer shown here. {r.Item.From}'s original stays in their Peergos – only they can delete it.";
        ConfirmBar.Visibility = Visibility.Visible;
    }

    async Task Delete()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        if (Current is not { } r || friend == null) return;
        try
        {
            await hub.DeleteAsync(friend, r.Item);
            if (otherMonth != null) otherMonth = otherMonth.Where(i => i.Path != r.Item.Path).ToList();
            Rebuild();
            Status(r.Item.From == Me ? "Deleted for both of you" : "Deleted on your side");
        }
        catch (DirectException e) { Status("Not deleted: " + e.Message); }
    }

    void SaveAs()
    {
        if (Current is not { LocalFile: { } f } r) return;
        var ext = Path.GetExtension(r.Item.Name);
        var d = new Microsoft.Win32.SaveFileDialog { FileName = r.Item.Name, Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|All files|*.*" };
        if (d.ShowDialog(this) != true) return;
        try { File.Copy(f, d.FileName, true); Status("Saved: " + d.FileName); }
        catch (Exception e) { Status("Not saved: " + e.Message); }
    }

    void OpenCurrent()
    {
        if (Current?.LocalFile is not { } f) return;
        try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { }
    }

    // ---------- friends ----------

    async Task ToggleFriends(bool show)
    {
        FriendsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        FillFriendsPanel();
        AddBox.Focus();
        try { await hub.RefreshFriendsAsync(); FillFriendsPanel(); }
        catch (DirectException e) { AddResult.Text = e.Message; }
    }

    void FillFriendsPanel()
    {
        var f = hub.FriendsState;
        IncomingList.Children.Clear();
        var incoming = f?.Incoming ?? [];
        IncomingTitle.Visibility = incoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var who in incoming)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var accept = new Button { Content = "Accept", Margin = new Thickness(6, 0, 0, 0) };
            accept.SetResourceReference(StyleProperty, "AccentButtonStyle");
            var decline = new Button { Content = "Decline", Margin = new Thickness(6, 0, 0, 0) };
            accept.Click += async (_, _) => { await hub.AcceptAsync(who); FillFriendsPanel(); };
            decline.Click += async (_, _) =>
            {
                try { await hub.DeclineAsync(who); } catch (DirectException e) { AddResult.Text = e.Message; }
                FillFriendsPanel();
            };
            DockPanel.SetDock(decline, Dock.Right);
            DockPanel.SetDock(accept, Dock.Right);
            row.Children.Add(decline);
            row.Children.Add(accept);
            row.Children.Add(new TextBlock { Text = who, VerticalAlignment = VerticalAlignment.Center });
            IncomingList.Children.Add(row);
        }
        FriendList.Children.Clear();
        if (hub.Friends.Count == 0)
            FriendList.Children.Add(new TextBlock { Text = "Nobody yet.", Style = (Style)FindResource("Dim") });
        foreach (var who in hub.Friends)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Stop sharing directly in this app (the pictures and the Peergos friendship stay)" };
            remove.Click += async (_, _) => { await hub.RemoveFriendAsync(who); if (friend == who) { friend = null; FillFriends(null); Rebuild(); } FillFriendsPanel(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = who, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = f == null ? "…" : DirectLogic.Status(who, f), Style = (Style)FindResource("Dim") });
            row.Children.Add(text);
            FriendList.Children.Add(row);
        }
    }

    async Task AddFriend()
    {
        var user = DirectLogic.NormaliseUser(AddBox.Text);
        if (user == null) { AddResult.Text = "Enter a Peergos username (letters, digits, - and _)."; return; }
        if (user == Me) { AddResult.Text = "That is your own username."; return; }
        AddBtn.IsEnabled = false;
        AddResult.Text = "One moment…";
        try
        {
            AddResult.Text = await hub.AddFriendAsync(user);
            AddBox.Clear();
            FillFriends(user);
            SelectFriend(user);
        }
        catch (DirectException e) { AddResult.Text = e.Message; }
        finally { AddBtn.IsEnabled = true; FillFriendsPanel(); }
    }

    void Status(string text) => StatusText.Text = text;
}
