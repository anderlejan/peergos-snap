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
public sealed class DirectRow(DirectItem item, string friend, string me, string? localFile, bool showFriend)
{
    /// <summary>The friend this file is shared with (sender or receiver).</summary>
    public string Friend { get; } = friend;
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
    public string Line2 => (string.Equals(Item.From, me, StringComparison.OrdinalIgnoreCase) ? (showFriend ? "You → " + Friend : "You") : Item.From) + " · "
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
    const string AllFriends = "All friends", AllMonths = "All months"; // never usernames (those have no spaces)
    string? friend; // the friend shown; null = all friends
    string? month;  // the month shown; null = all months
    /// <summary>Months other than the watched one (no live updates): loaded once, again with Refresh.</summary>
    readonly Dictionary<(string Friend, string Month), List<DirectItem>> older = [];
    readonly Dictionary<string, List<string>> monthsOf = new(StringComparer.OrdinalIgnoreCase);
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

        FriendBox.SelectionChanged += (_, _) => { if (!loadingFriend && FriendBox.SelectedItem is string f) SelectFriend(f == AllFriends ? null : f); };
        MonthBox.SelectionChanged += (_, _) => { if (!loadingFriend && MonthBox.SelectedItem is string m) SelectMonth(m == AllMonths ? null : m); };
        SendToBox.SelectionChanged += (_, _) => { if (!loadingFriend) UpdateDropZone(); };
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
            if (SendTo is not { } to) return;
            WindowState = WindowState.Minimized; // out of the way while selecting
            await Task.Delay(250);
            await app.CapturePicture(toFriend: to);
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

    /// <summary>Shows the window: everything (all friends, all months) unless a friend is named. With a path, that file
    /// is selected (the filters open up when they would hide it). Without <paramref name="activate"/> it comes to the
    /// front but does not take the keyboard from the app the user is typing in.</summary>
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
        if (path != null)
        {
            // The file must be in view: a filter that would hide it opens up.
            var pf = DirectLogic.FriendOf(path, Me);
            if (friend != null && !string.Equals(friend, pf, StringComparison.OrdinalIgnoreCase)) friend = null;
            if (month != null && month != DirectLogic.MonthOf(path)) month = null;
        }
        else if (who != null) friend = who;
        FillFriends();
        FillMonths();
        Rebuild();
        _ = LoadOlder(false);
        if (hub.Friends.Count == 0) _ = ToggleFriends(true);
    }

    void HubUpdated()
    {
        FillFriends();
        Rebuild();
        if (FriendsPanel.Visibility == Visibility.Visible) FillFriendsPanel();
        Status(hub.Status);
    }

    /// <summary>The friends in the filter ("All friends" first) and in "Send to".</summary>
    void FillFriends()
    {
        loadingFriend = true;
        var list = hub.Friends.ToList();
        if (friend != null && !list.Contains(friend)) friend = null;
        var filter = new List<string> { AllFriends };
        filter.AddRange(list);
        if (!FriendBox.Items.Cast<string>().SequenceEqual(filter))
        {
            FriendBox.Items.Clear();
            foreach (var f in filter) FriendBox.Items.Add(f);
        }
        FriendBox.SelectedItem = friend ?? AllFriends;
        var keep = SendToBox.SelectedItem as string;
        if (!SendToBox.Items.Cast<string>().SequenceEqual(list))
        {
            SendToBox.Items.Clear();
            foreach (var f in list) SendToBox.Items.Add(f);
        }
        // Send to: the friend shown, else the one chosen before, else the last one sent to, else the first.
        SendToBox.SelectedItem = friend ?? (keep != null && list.Contains(keep) ? keep
            : list.Contains(app.Settings.DirectLastFriend) ? app.Settings.DirectLastFriend : list.FirstOrDefault());
        loadingFriend = false;
        Title = friend == null ? "Peergos Snap – Direct" : $"Peergos Snap – Direct with {friend}";
        UpdateDropZone();
    }

    void FillMonths()
    {
        loadingFriend = true;
        var ms = new SortedSet<string>(Comparer<string>.Create((a, b) => string.CompareOrdinal(b, a))) { hub.WatchMonth };
        foreach (var f in Scope()) if (monthsOf.TryGetValue(f, out var l)) ms.UnionWith(l);
        if (month != null) ms.Add(month);
        var items = new List<string> { AllMonths };
        items.AddRange(ms);
        if (!MonthBox.Items.Cast<string>().SequenceEqual(items))
        {
            MonthBox.Items.Clear();
            foreach (var m in items) MonthBox.Items.Add(m);
        }
        MonthBox.SelectedItem = month ?? AllMonths;
        loadingFriend = false;
    }

    /// <summary>The friends whose files are shown.</summary>
    IEnumerable<string> Scope() => friend == null ? hub.Friends : hub.Friends.Where(f => f == friend);

    string? SendTo => SendToBox.SelectedItem as string;

    void SelectFriend(string? f)
    {
        friend = f;
        FillFriends();
        FillMonths();
        Rebuild();
        _ = LoadOlder(false);
    }

    void SelectMonth(string? m)
    {
        month = m;
        Rebuild();
        _ = LoadOlder(false);
    }

    /// <summary>
    /// Loads the months that are not watched live (all months, or the one chosen) for the friends shown. Each month
    /// is loaded once; Refresh loads them again.
    /// </summary>
    async Task LoadOlder(bool again)
    {
        if (loadingOlder) return;
        loadingOlder = true;
        try
        {
            if (again) { older.Clear(); monthsOf.Clear(); }
            foreach (var f in Scope().ToList())
            {
                if (!monthsOf.ContainsKey(f) || again)
                {
                    var (_, ms) = await hub.ListAsync(f, hub.WatchMonth);
                    monthsOf[f] = ms;
                    FillMonths();
                }
                foreach (var m in monthsOf[f].Where(m => m != hub.WatchMonth && (month == null || m == month)))
                {
                    if (older.ContainsKey((f, m))) continue;
                    Status($"Loading {f}, {m}…");
                    older[(f, m)] = (await hub.ListAsync(f, m)).Items;
                    Rebuild();
                }
            }
            Status(month != null && month != hub.WatchMonth ? $"{month} is not watched for changes – use Refresh" : hub.Status);
        }
        catch (DirectException e) { Status(e.Message); }
        finally { loadingOlder = false; Rebuild(); }
    }

    bool loadingOlder;

    async Task Reload()
    {
        if (hub.Friends.Count == 0) { await hub.StartAsync(); return; }
        try
        {
            foreach (var f in Scope().ToList()) await hub.ListAsync(f, hub.WatchMonth);
            await LoadOlder(true);
            Status("Up to date");
        }
        catch (DirectException e) { Status(e.Message); }
    }

    void UpdateDropZone()
    {
        bool can = SendTo != null && !sending;
        SendToBox.Visibility = friend == null && hub.Friends.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DropTitle.Text = hub.Friends.Count == 0 ? "Add a friend to share pictures directly"
            : friend == null && hub.Friends.Count > 1 ? "Send to" : $"Send a picture to {SendTo}";
        ChooseBtn.IsEnabled = PasteBtn.IsEnabled = CaptureBtn.IsEnabled = can;
    }

    /// <summary>The files shown: of the friends and months chosen (pinned first, then newest), keeping the selection.</summary>
    void Rebuild()
    {
        var keep = wantedPath ?? Current?.Item.Path;
        rows.Clear();
        bool all = friend == null;
        foreach (var f in Scope())
        {
            var list = new List<DirectItem>();
            if (month == null || month == hub.WatchMonth) list.AddRange(hub.Items(f));
            foreach (var ((of, m), items) in older)
                if (of == f && m != hub.WatchMonth && (month == null || m == month)) list.AddRange(items);
            foreach (var it in list.DistinctBy(i => i.Path))
            {
                var local = DirectLogic.CacheFile(AppPaths.DirectDir, f, it);
                rows.Add(new DirectRow(it, f, Me, File.Exists(local) ? local : null, all));
            }
        }
        var sorted = DirectLogic.Sort(rows.Select(r => r.Item)).Select(i => rows.First(r => r.Item == i)).ToList();
        rows.Clear();
        rows.AddRange(sorted);
        List.ItemsSource = null;
        List.ItemsSource = rows;
        // The same picture stays selected; if it is gone (deleted), the first one.
        var sel = rows.FirstOrDefault(r => r.Item.Path == keep) ?? rows.FirstOrDefault();
        if (sel != null) { List.SelectedItem = sel; List.ScrollIntoView(sel); }
        if (sel?.Item.Path == wantedPath) wantedPath = null;
        var who = friend ?? "your friends";
        var when = month == null ? "" : $" in {month}";
        Empty.Text = hub.Friends.Count == 0 ? "No friend yet. Click Friends… to add one."
            : loadingOlder ? "Loading…" : $"Nothing shared with {who}{when} yet. Drop a picture above.";
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = rows.Count == 0 ? "" : $"{rows.Count} shown{when}" + $" · {rows.Count(r => r.Item.From != Me)} from {(friend ?? "friends")}";
        _ = FetchThumbs();
        if (rows.Count == 0) _ = ShowCurrent();
    }

    bool fetching;

    /// <summary>Downloads the pictures of the list that are not on this PC yet (one after the other).</summary>
    async Task FetchThumbs()
    {
        if (fetching) return;
        fetching = true;
        try { await FetchThumbsOnce(); }
        finally { fetching = false; }
    }

    async Task FetchThumbsOnce()
    {
        foreach (var r in rows.Where(r => r.LocalFile == null && r.Item.IsImage && r.Item.Size < 40_000_000).ToList())
        {
            try
            {
                r.LocalFile = await hub.LocalFileAsync(r.Friend, r.Item);
                if (!rows.Contains(r)) continue; // the list changed meanwhile
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
                ViewerNote.Text = hub.Friends.Count == 0 ? "" : "Select a picture";
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
                try { r.LocalFile = await hub.LocalFileAsync(r.Friend, it); }
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
            Multiselect = true, Title = $"Files for {SendTo}",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Videos|*.mp4;*.webm|All files|*.*",
        };
        if (d.ShowDialog(this) == true) await Send(d.FileNames.ToList());
    }

    async Task Send(List<string> files)
    {
        if (SendTo is not { } to || files.Count == 0 || sending) return;
        sending = true;
        UpdateDropZone();
        try
        {
            var sent = await hub.SendAsync(to, files, t => SendState.Text = t);
            SendState.Text = sent.Count == 1 ? $"Sent – {to} sees it now." : $"{sent.Count} sent – {to} sees them now.";
            // What was just sent must be in view.
            if (month != null && month != hub.WatchMonth) month = null;
            if (friend != null && friend != to) friend = null;
            FillFriends();
            FillMonths();
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
        if (Current is not { } r) return;
        if (label != null && label == r.Item.Label) return;
        try
        {
            await hub.SetMetaAsync(r.Friend, r.Item, label, pin, star);
            Replace(r.Friend, r.Item.Path, i => i with
            {
                Label = label ?? i.Label, Pinned = pin ?? i.Pinned,
                Stars = star == null ? i.Stars : star.Value ? i.Stars.Append(Me).Distinct().ToList() : i.Stars.Where(x => x != Me).ToList(),
            });
            Status(label != null ? "Label saved – your friend sees it" : pin != null ? (pin.Value ? "Pinned for both of you" : "Unpinned")
                : star == true ? "Starred" : "Star removed");
        }
        catch (DirectException e) { Status("Not saved: " + e.Message); }
    }

    void AskDelete()
    {
        if (Current is not { } r) return;
        ConfirmText.Text = $"Delete your copy of “{r.Item.Name}”? It is removed from your Peergos and this PC. "
                           + (r.Item.From == Me ? $"{r.Friend}'s copy stays theirs." : $"{r.Friend} keeps theirs.");
        ConfirmBar.Visibility = Visibility.Visible;
    }

    async Task Delete()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        if (Current is not { } r) return;
        try
        {
            await hub.DeleteAsync(r.Friend, r.Item);
            foreach (var k in older.Keys.Where(k => k.Friend == r.Friend).ToList())
                older[k] = older[k].Where(i => i.Path != r.Item.Path).ToList();
            Rebuild();
            Status("Your copy is deleted");
        }
        catch (DirectException e) { Status("Not deleted: " + e.Message); }
    }

    /// <summary>A label, pin or star shows at once also in a month that is not watched.</summary>
    void Replace(string f, string path, Func<DirectItem, DirectItem> change)
    {
        foreach (var k in older.Keys.Where(k => k.Friend == f).ToList())
            older[k] = older[k].Select(i => i.Path == path ? change(i) : i).ToList();
        Rebuild();
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
            remove.Click += async (_, _) => { await hub.RemoveFriendAsync(who); if (friend == who) friend = null; FillFriends(); Rebuild(); FillFriendsPanel(); };
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
            SelectFriend(user);
        }
        catch (DirectException e) { AddResult.Text = e.Message; }
        finally { AddBtn.IsEnabled = true; FillFriendsPanel(); }
    }

    void Status(string text) => StatusText.Text = text;
}
