using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

/// <summary>One file in the direct window's list (and in its full-screen view).</summary>
public sealed class DirectRow(DirectItem item, string friend, string me, string? keptFile, bool showFriend, Func<DirectRow, Task<string?>> fetch,
    Func<DirectItem, string?> peergosThumb) : INotifyPropertyChanged, IViewable
{
    /// <summary>The friend this file is shared with (sender or receiver).</summary>
    public string Friend { get; } = friend;
    public DirectItem Item { get; } = item;
    public event PropertyChangedEventHandler? PropertyChanged;
    string? local = keptFile;
    ImageSource? thumb, remote;
    string? thumbOf;
    bool loadingThumb;

    /// <summary>The copy on this PC: kept (in the direct folder, or the file it was sent from) or – while received
    /// files are not kept – a temporary one for viewing; null until it is needed.</summary>
    public string? LocalFile
    {
        get => local;
        set
        {
            if (local == value) return;
            local = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    /// <summary>The copy stays on this PC (Folder shows it); false for a temporary one.</summary>
    public bool Kept { get; set; } = keptFile != null;

    public bool FromMe => string.Equals(Item.From, me, StringComparison.OrdinalIgnoreCase);

    /// <summary>A kept picture's own, a kept video's still (made once); otherwise the small picture Peergos keeps with
    /// the file – nothing is downloaded for it. Loaded once per file (the list is refreshed often).</summary>
    public ImageSource? Thumb
    {
        get
        {
            if (Kept && LocalFile is { } f)
            {
                if (thumbOf != f)
                {
                    if (Item.IsImage)
                    {
                        thumb = ThumbFiles.Load(f, 180);
                        thumbOf = f;
                    }
                    else if (Item.IsVideo && !loadingThumb) _ = LoadVideoThumb(f);
                }
                if (thumbOf == f && thumb != null) return thumb;
            }
            return remote ??= ThumbFiles.FromBytes(DirectLogic.ThumbBytes(peergosThumb(Item)), 180);
        }
    }

    async Task LoadVideoThumb(string f)
    {
        loadingThumb = true;
        try
        {
            if (await ThumbFiles.VideoThumbOf(f) is not { } still) return;
            thumb = await Task.Run(() => ThumbFiles.Load(still, 180));
            thumbOf = f;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
        }
        catch (Exception e) { Log.Error("direct: video still " + f, e); }
        finally { loadingThumb = false; }
    }

    public string Glyph => FileKinds.Glyph(Item.Kind);
    public Visibility VideoBadge => Item.IsVideo ? Visibility.Visible : Visibility.Collapsed;
    public string Title => Item.Label.Trim().Length > 0 ? Item.Label.Trim() : Item.Name;
    public string Line2 => (FromMe ? (showFriend ? "You → " + Friend : "You") : Item.From) + " · "
                           + Item.Modified.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture)
                           + (Item.Size > 0 ? " · " + HistoryLogic.Size(Item.Size) : "")
                           + (Item.Label.Trim().Length > 0 ? " · " + Item.Name : "");
    public string Badges => (Item.Pinned ? "📌 " : "") + (Item.Stars.Count > 0 ? "★ " + DirectLogic.StarredBy(Item.Stars, me) : "");

    // ---------- the full-screen view ----------
    public string ViewTitle => Title;
    public string ViewLine => Line2;
    public bool IsVideo => Item.IsVideo;
    public string? OpenFile => LocalFile;
    public bool CanDraw => Item.IsImage;
    public string NoPreview => $"{Item.Name}\n{FileKinds.Describe(Item.Name)} – no preview. Enter opens it in its app.";

    public async Task<string?> PictureAsync()
    {
        if (!Item.IsImage && !Item.IsVideo) return null;
        var f = LocalFile ?? await fetch(this);
        if (f == null) return null;
        return Item.IsVideo ? await ThumbFiles.VideoThumbOf(f) : f;
    }
}

/// <summary>
/// The direct window: send pictures, videos, files and folders to a friend by dropping, pasting or choosing them (or
/// take a picture, optionally drawing on it first), and see what they send at once. Pictures zoom where the pointer
/// is and open full screen; videos play here; every file shows what it is and what can be done with it. Both sides
/// can label, pin, star and delete their own copies.
/// </summary>
public partial class DirectWindow : Window
{
    readonly DirectHub hub;
    readonly TrayController app;
    readonly DispatcherTimer labelSave = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DispatcherTimer playClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly List<DirectRow> rows = [];
    const string AllFriends = "All friends", AllMonths = "All months"; // never usernames (those have no spaces)
    string? friend; // the friend shown; null = all friends
    string? month;  // the month shown; null = all months
    /// <summary>Months other than the watched one (no live updates): loaded once, again with Refresh.</summary>
    readonly Dictionary<(string Friend, string Month), List<DirectItem>> older = [];
    readonly Dictionary<string, List<string>> monthsOf = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>ZIP files opened as a folder here, and that folder (opened again rather than unpacked twice).</summary>
    readonly Dictionary<string, string> unpacked = new(StringComparer.OrdinalIgnoreCase);
    string? wantedPath;
    bool loadingFriend, showing, sending, playing, clockMovesSeek, rebuilding;
    DirectItem? shownItem; // what the right side shows (a rebuild that keeps it changes nothing there)
    double fitScale = 1;
    string? shownFile; // the picture in the viewer (kept, with its zoom, when only its label or stars change)
    string? playerFile; // the video in the player (released when another file is shown)
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
        app.SettingsChanged += SettingsChanged;
        // Looking at this window is looking at what friends sent: the tray icon stops flashing.
        Activated += (_, _) => app.Seen();
        Closed += (_, _) =>
        {
            hub.Updated -= HubUpdated;
            app.SettingsChanged -= SettingsChanged;
            StopPlayer();
            hub.ClearViewFiles(); // the temporary copies of files not kept on this PC
        };

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
        FolderSendBtn.Click += async (_, _) => await ChooseFolders();
        PasteBtn.Click += async (_, _) => await Paste();
        CaptureBtn.Click += async (_, _) =>
        {
            if (SendTo is not { } to) return;
            WindowState = WindowState.Minimized; // out of the way while selecting
            await Task.Delay(250);
            await app.CapturePicture(toFriend: to, annotate: app.Settings.DirectDrawFirst);
            WindowState = WindowState.Normal;
        };
        DrawFirstBox.Click += (_, _) => app.UpdateSettings(s => s.DirectDrawFirst = DrawFirstBox.IsChecked == true);
        DragEnter += (_, e) => { e.Effects = CanTake(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; DropFrame.StrokeThickness = 4; e.Handled = true; };
        DragOver += (_, e) => { e.Effects = CanTake(e.Data) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragLeave += (_, _) => DropFrame.StrokeThickness = 2;
        Drop += async (_, e) => { DropFrame.StrokeThickness = 2; await Take(e.Data); };
        PreviewKeyDown += async (_, e) => await Keys(e);

        List.SelectionChanged += async (_, _) => { if (!rebuilding) await ShowCurrent(); };
        List.MouseDoubleClick += async (_, _) => await OpenCurrent();
        ZoomInBtn.Click += (_, _) => ZoomAt(Zoom.ScaleX * 1.25, null);
        ZoomOutBtn.Click += (_, _) => ZoomAt(Zoom.ScaleX / 1.25, null);
        FitBtn.Click += (_, _) => ZoomAt(fitScale = FitScale(allowLarger: true), null);
        ActualBtn.Click += (_, _) => ZoomAt(1, null);
        ViewBtn.Click += (_, _) => ShowFull();
        // As in the history's full-screen view: the wheel zooms where the pointer is (one notch 20 %).
        Viewer.PreviewMouseWheel += (_, e) =>
        {
            if (Picture.Source == null || Current?.Item.IsImage != true) return;
            ZoomAt(Zoom.ScaleX * Math.Pow(1.2, e.Delta / 120.0), e.GetPosition(Viewer));
            e.Handled = true;
        };
        // Drag the picture to move around when it is larger than the window; a double-click shows it full screen.
        Picture.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { ShowFull(); e.Handled = true; return; }
            dragFrom = e.GetPosition(Viewer);
            dragOffset = new Point(Viewer.HorizontalOffset, Viewer.VerticalOffset);
            Picture.CaptureMouse();
        };
        Picture.MouseMove += (_, e) =>
        {
            if (dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(Viewer);
            Viewer.ScrollToHorizontalOffset(dragOffset.X - (p.X - from.X));
            Viewer.ScrollToVerticalOffset(dragOffset.Y - (p.Y - from.Y));
        };
        Picture.MouseLeftButtonUp += (_, _) => { dragFrom = null; Picture.ReleaseMouseCapture(); };
        Viewer.SizeChanged += (_, _) => { if (Math.Abs(Zoom.ScaleX - fitScale) < 0.001) ZoomAt(fitScale = FitScale(allowLarger: false), null); };

        // Videos play here.
        PlayBtn.Click += async (_, _) => await PlayPause();
        BigPlayBtn.Click += async (_, _) => await PlayPause();
        Player.MouseLeftButtonUp += async (_, _) => await PlayPause();
        MuteBtn.Click += (_, _) =>
        {
            Player.IsMuted = !Player.IsMuted;
            MuteBtn.Content = Player.IsMuted ? "🔇" : "🔊";
        };
        Player.MediaOpened += (_, _) =>
        {
            if (Player.NaturalDuration.HasTimeSpan)
            {
                Seek.Maximum = Math.Max(0.1, Player.NaturalDuration.TimeSpan.TotalSeconds);
                Seek.IsEnabled = true;
            }
            ShowTime();
        };
        Player.MediaEnded += (_, _) =>
        {
            Player.Pause();
            Player.Position = TimeSpan.Zero;
            playing = false;
            ShowTime();
            MarkPlaying();
        };
        Player.MediaFailed += (_, e) =>
        {
            var why = e.ErrorException?.Message ?? "unknown error";
            Log.Error("direct: video " + playerFile + ": " + why);
            StopPlayer();
            ViewerNote.Text = "This video cannot be played here – Open plays it in your video player.";
        };
        playClock.Tick += (_, _) => ShowTime();
        Seek.ValueChanged += (_, _) =>
        {
            if (clockMovesSeek || playerFile == null) return;
            Player.Position = TimeSpan.FromSeconds(Seek.Value);
            ShowTime();
        };

        StarBtn.Click += async (_, _) => await Meta(star: StarBtn.IsChecked == true);
        PinBtn.Click += async (_, _) => await Meta(pin: PinBtn.IsChecked == true);
        LabelBox.TextChanged += (_, _) => { if (!showing) { labelSave.Stop(); labelSave.Start(); } };
        labelSave.Tick += async (_, _) => { labelSave.Stop(); await Meta(label: LabelBox.Text.Trim()); };
        OpenBtn.Click += async (_, _) => await OpenCurrent();
        FolderBtn.Click += async (_, _) =>
        {
            if (Current is { } r && await Fetch(r) is { } f) Process.Start("explorer.exe", "/select,\"" + f + "\"");
        };
        SaveBtn.Click += async (_, _) => await SaveAs();
        CopyBtn.Click += async (_, _) =>
        {
            if (Current is not { } r || await Fetch(r) is not { } f) return;
            try { ClipboardService.MediaToClipboard(f); Status(r.Item.IsImage ? "The picture is on the clipboard" : "The file is on the clipboard – paste it in Explorer or an app"); }
            catch (Exception e) { Status("Could not copy: " + e.Message); }
        };
        DrawBtn.Click += async (_, _) => await DrawOnCurrent();
        UnpackBtn.Click += async (_, _) => await Unpack(choose: false);
        UnpackToBtn.Click += async (_, _) => await Unpack(choose: true);
        ForwardBtn.Click += (_, _) => ForwardMenu();
        LinkBtn.Click += async (_, _) => await GetLink();
        DeleteBtn.Click += (_, _) => AskDelete();
        ConfirmNo.Click += (_, _) => ConfirmBar.Visibility = Visibility.Collapsed;
        ConfirmYes.Click += async (_, _) => await Delete();

        ZoomAt(1, null);
        BringFront.IsChecked = app.Settings.DirectBringToFront;
        DrawFirstBox.IsChecked = app.Settings.DirectDrawFirst;
    }

    void SettingsChanged(Settings before)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SettingsChanged(before)); return; }
        DrawFirstBox.IsChecked = app.Settings.DirectDrawFirst;
        BringFront.IsChecked = app.Settings.DirectBringToFront;
        if (before.DirectKeepOnPc != app.Settings.DirectKeepOnPc)
        {
            // In effect at once: what is shown, the Folder button and the list's pictures follow the new choice.
            shownFile = null;
            _ = ShowCurrent();
            _ = FetchThumbs();
        }
    }

    // ---------- showing ----------

    /// <summary>Shows the window: everything (all friends, all months) unless a friend is named. With a path, that file
    /// is selected (the filters open up when they would hide it). Without <paramref name="activate"/> it comes to the
    /// front but does not take the keyboard from the app the user is typing in.</summary>
    public void ShowFor(string? who, string? path, bool activate, bool friendsPanel = false)
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
        if (hub.Friends.Count == 0 || friendsPanel) _ = ToggleFriends(true);
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
        // Send to: the friend shown, else the one chosen here before, else the one chosen in the tray menu (Other →
        // Friend), else the last one sent to, else the first.
        SendToBox.SelectedItem = friend ?? (keep != null && list.Contains(keep) ? keep
            : DirectLogic.ChosenFriend(list, app.Settings.DirectFriend)
              ?? (list.Contains(app.Settings.DirectLastFriend) ? app.Settings.DirectLastFriend : list.FirstOrDefault()));
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
        // Asked while a load runs (the filter widened, Refresh): it runs once more afterwards with what is shown then.
        if (loadingOlder) { loadOlderAgain = true; loadOlderFresh |= again; return; }
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
        finally
        {
            loadingOlder = false;
            Rebuild();
            if (loadOlderAgain)
            {
                var fresh = loadOlderFresh;
                loadOlderAgain = loadOlderFresh = false;
                _ = LoadOlder(fresh);
            }
        }
    }

    bool loadingOlder, loadOlderAgain, loadOlderFresh;

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
        DropTitle.Text = hub.Friends.Count == 0 ? "Add a friend to share directly"
            : friend == null && hub.Friends.Count > 1 ? "Send to" : $"Send to {SendTo}";
        ChooseBtn.IsEnabled = FolderSendBtn.IsEnabled = PasteBtn.IsEnabled = CaptureBtn.IsEnabled = can;
    }

    /// <summary>The files shown: of the friends and months chosen (pinned first, then newest), keeping the selection.</summary>
    void Rebuild()
    {
        var keep = wantedPath ?? Current?.Item.Path;
        // Rows are kept between rebuilds (with their thumbnails); a changed label, pin or star makes a new one.
        var known = new Dictionary<DirectItem, DirectRow>();
        foreach (var r in rows) known.TryAdd(r.Item, r);
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
                if (known.TryGetValue(it, out var same) && same.Friend == f) { rows.Add(same); continue; }
                rows.Add(new DirectRow(it, f, Me, hub.KeptFile(f, it), all, Fetch, i => hub.Thumb(i.Path)));
            }
        }
        var sorted = DirectLogic.Sort(rows.Select(r => r.Item)).Select(i => rows.First(r => r.Item == i)).ToList();
        rows.Clear();
        rows.AddRange(sorted);
        // The right side is only shown again when another file (or a changed one) is selected: a video keeps
        // playing and a picture keeps its zoom while the list is rebuilt around them.
        rebuilding = true;
        DirectRow? sel;
        try
        {
            List.ItemsSource = null;
            List.ItemsSource = rows;
            // The same file stays selected; if it is gone (deleted), the first one.
            sel = rows.FirstOrDefault(r => r.Item.Path == keep) ?? rows.FirstOrDefault();
            if (sel != null) { List.SelectedItem = sel; List.ScrollIntoView(sel); }
        }
        finally { rebuilding = false; }
        if (sel?.Item.Path == wantedPath) wantedPath = null;
        if (sel?.Item != shownItem || sel == null) _ = ShowCurrent();
        var who = friend ?? "your friends";
        var when = month == null ? "" : $" in {month}";
        Empty.Text = hub.Friends.Count == 0 ? "No friend yet. Click Friends… to add one."
            : loadingOlder ? "Loading…" : $"Nothing shared with {who}{when} yet. Drop a picture, a file or a folder above.";
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = rows.Count == 0 ? "" : $"{rows.Count} shown{when}" + $" · {rows.Count(r => !r.FromMe)} from {(friend ?? "friends")}";
        _ = FetchThumbs();
    }

    bool fetching;

    /// <summary>With "Also keep received files on this PC": downloads the pictures and short videos of the list that are
    /// not on this PC yet (one after the other), for their thumbnails. Without it nothing is downloaded – the list shows
    /// the small pictures Peergos keeps with the files.</summary>
    async Task FetchThumbs()
    {
        if (fetching) return;
        fetching = true;
        try { await FetchThumbsOnce(); }
        finally { fetching = false; }
    }

    async Task FetchThumbsOnce()
    {
        if (!hub.KeepOnPc) return;
        foreach (var r in rows.Where(r => !r.Kept && (r.Item.IsImage && r.Item.Size < 40_000_000 || r.Item.IsVideo && r.Item.Size < 100_000_000)).ToList())
        {
            try
            {
                if (!hub.KeepOnPc) return; // switched off meanwhile
                r.LocalFile = await hub.LocalFileAsync(r.Friend, r.Item);
                r.Kept = true;
                if (!rows.Contains(r)) continue; // the list changed meanwhile
                if (Current == r) await ShowCurrent();
            }
            catch (DirectException) { }
        }
    }

    /// <summary>The file on this PC (downloaded first when needed – kept, or a temporary copy when received files are
    /// not kept here), or null with the reason in the status line.</summary>
    async Task<string?> Fetch(DirectRow r)
    {
        if (r.LocalFile is { } have && File.Exists(have)) return have;
        try
        {
            Status($"Loading {r.Item.Name}…");
            r.LocalFile = await hub.FileAsync(r.Friend, r.Item);
            r.Kept = hub.KeptFile(r.Friend, r.Item) != null;
            Status(hub.Status);
            if (Current == r) UpdateActions(r);
            return r.LocalFile;
        }
        catch (DirectException e)
        {
            Status("Could not load it: " + e.Message);
            return null;
        }
    }

    async Task ShowCurrent()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        ViewerNote.Margin = new Thickness(20);
        var r = Current;
        shownItem = r?.Item;
        showing = true;
        try
        {
            if (r == null || playerFile != null && r.LocalFile != playerFile) StopPlayer();
            UpdateActions(r);
            if (r == null)
            {
                ShowNothing(hub.Friends.Count == 0 ? "" : "Select a file");
                NameText.Text = FactsText.Text = WhereText.Text = "";
                if (!LabelBox.IsKeyboardFocused) LabelBox.Text = "";
                return;
            }
            var it = r.Item;
            StarBtn.IsChecked = it.Stars.Contains(Me);
            StarBtn.Content = StarBtn.IsChecked == true ? "★ Starred" : "☆ Star";
            PinBtn.IsChecked = it.Pinned;
            PinBtn.Content = it.Pinned ? "📌 Pinned" : "📌 Pin";
            if (!LabelBox.IsKeyboardFocused) LabelBox.Text = it.Label;
            NameText.Text = it.Name;
            ShowFacts(r, null);
            if (it.IsImage) await ShowPicture(r);
            else if (it.IsVideo) await ShowVideo(r);
            else await ShowFile(r);
        }
        finally { showing = false; }
    }

    /// <summary>What the file is (kind, size, its picture size or length) and where it is.</summary>
    void ShowFacts(DirectRow r, string? measure)
    {
        var it = r.Item;
        var facts = new List<string> { FileKinds.Describe(it.Name) };
        if (measure != null) facts.Add(measure);
        if (it.Size > 0) facts.Add(HistoryLogic.Size(it.Size));
        if (it.Stars.Count > 0) facts.Add("★ " + DirectLogic.StarredBy(it.Stars, Me));
        FactsText.Text = string.Join(" · ", facts);
        var when = it.Modified.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        var who = r.FromMe ? $"You sent it to {r.Friend} on {when}" : $"{it.From} sent it on {when}";
        var where = r.FromMe ? $"it is in your Peergos (shared with {r.Friend})" : "your copy is in your Peergos";
        var here = r.Kept ? " and on this PC."
            : hub.KeepOnPc ? "; not on this PC yet (it loads when you open it)."
            : "; it is not kept on this PC (Settings → Direct) – opening it uses a temporary copy.";
        WhereText.Text = $"{who} – {where}" + here;
    }

    void ShowNothing(string note)
    {
        ZoomText.Text = "";
        Picture.Source = null;
        shownFile = null;
        Viewer.Visibility = Visibility.Visible;
        FileCard.Visibility = Visibility.Collapsed;
        PlayerBar.Visibility = BigPlayBtn.Visibility = Visibility.Collapsed;
        ZoomBar.IsEnabled = false;
        ViewerNote.Text = note;
    }

    async Task ShowPicture(DirectRow r)
    {
        FileCard.Visibility = Visibility.Collapsed;
        PlayerBar.Visibility = BigPlayBtn.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
        ZoomBar.IsEnabled = true;
        // Not kept on this PC: shown straight from Peergos, from memory – nothing is written to disk.
        bool fromMemory = r.LocalFile == null && !hub.KeepOnPc && r.Item.Size <= 30_000_000;
        var key = fromMemory ? "peergos:" + r.Item.Path : r.LocalFile;
        if (key != null && shownFile == key && Picture.Source is BitmapSource same)
        {
            ShowFacts(r, $"{same.PixelWidth} × {same.PixelHeight}");
            return; // same picture: keep it and its zoom
        }
        Picture.Source = null;
        shownFile = null;
        BitmapSource? img;
        if (fromMemory)
        {
            ViewerNote.Text = "Loading the picture…";
            byte[]? bytes = null;
            try { bytes = await hub.BytesAsync(r.Item); }
            catch (Exception e) when (e is DirectException or FormatException) { Status("Could not load it: " + e.Message); }
            if (Current != r) return;
            if (bytes == null) { ViewerNote.Text = "Could not load it – see below."; return; }
            img = await Task.Run(() => ThumbFiles.FromBytes(bytes, 0));
        }
        else
        {
            if (r.LocalFile == null)
            {
                ViewerNote.Text = "Loading the picture…";
                if (await Fetch(r) == null) { ViewerNote.Text = "Could not load it – see below."; return; }
                if (Current != r) return;
                ShowFacts(r, null);
            }
            key = r.LocalFile;
            img = await Task.Run(() => LoadFull(r.LocalFile!));
        }
        if (Current != r) return;
        Picture.Source = img;
        shownFile = img == null ? null : key;
        ViewerNote.Text = img == null ? "This picture cannot be shown – Open shows it in its app." : "";
        if (img == null) return;
        ShowFacts(r, $"{img.PixelWidth} × {img.PixelHeight}");
        Picture.Width = img.PixelWidth;
        Picture.Height = img.PixelHeight;
        UpdateLayout();
        ZoomAt(fitScale = FitScale(allowLarger: false), null);
    }

    /// <summary>A still in the picture area, fitted (a video's, before it plays).</summary>
    void ShowStill(ImageSource? still)
    {
        if (still is not BitmapSource b) return;
        Picture.Source = b;
        Picture.Width = b.PixelWidth;
        Picture.Height = b.PixelHeight;
        UpdateLayout();
        ZoomAt(fitScale = FitScale(allowLarger: true), null);
        ZoomText.Text = ""; // the still is fitted, not zoomed
    }

    /// <summary>A video: its still with ▶ (it plays here), its length and picture size.</summary>
    async Task ShowVideo(DirectRow r)
    {
        FileCard.Visibility = Visibility.Collapsed;
        ZoomBar.IsEnabled = false;
        ZoomText.Text = "";
        if (playerFile != null && playerFile == r.LocalFile) return; // playing it already
        Viewer.Visibility = Visibility.Visible;
        Picture.Source = null;
        shownFile = null;
        PlayerBar.Visibility = Visibility.Visible;
        BigPlayBtn.Visibility = Visibility.Visible;
        Seek.Value = 0;
        Seek.IsEnabled = false;
        TimeText.Text = "";
        MarkPlaying();
        if (r.LocalFile == null)
        {
            if (!hub.KeepOnPc || r.Item.Size > 300_000_000)
            {
                // Not loaded yet: the still Peergos keeps with it; ▶ loads and plays it (a temporary copy when received
                // files are not kept on this PC).
                ShowStill(r.Thumb);
                ViewerNote.Margin = new Thickness(20, 190, 20, 20); // under the big play button
                ViewerNote.Text = Picture.Source != null ? ""
                    : r.Item.Size > 300_000_000 ? $"A large video ({HistoryLogic.Size(r.Item.Size)}) – ▶ loads and plays it." : "▶ loads and plays it.";
                return;
            }
            ViewerNote.Text = "Loading the video…";
            if (await Fetch(r) == null) { ViewerNote.Text = "Could not load it – see below."; return; }
            if (Current != r) return;
        }
        ViewerNote.Text = "";
        var file = r.LocalFile!;
        var facts = await ThumbFiles.ProbeVideo(file);
        if (Current != r) return;
        var measure = new List<string>();
        if (facts.Width > 0) measure.Add($"{facts.Width} × {facts.Height}");
        if (facts.Duration is { } d) measure.Add(VideoFacts.Length(d));
        ShowFacts(r, measure.Count > 0 ? string.Join(" · ", measure) : null);
        TimeText.Text = facts.Duration is { } len ? "0:00 / " + VideoFacts.Length(len) : "";
        if (!r.Kept)
        {
            ShowStill(r.Thumb); // a temporary copy: no still is made and kept for it
            return;
        }
        if (await ThumbFiles.VideoThumbOf(file) is { } still && Current == r && playerFile == null)
        {
            var img = await Task.Run(() => ThumbFiles.Load(still, 0));
            if (Current != r || playerFile != null || img == null) return;
            ShowStill(img);
        }
    }

    async Task PlayPause()
    {
        if (Current is not { Item.IsVideo: true } r) return;
        if (playerFile != null)
        {
            if (playing) Player.Pause();
            else Player.Play();
            playing = !playing;
            if (playing) playClock.Start(); else playClock.Stop();
            MarkPlaying();
            return;
        }
        var f = await Fetch(r);
        if (f == null || Current != r) return;
        playerFile = f;
        Player.Source = new Uri(f);
        Player.Visibility = Visibility.Visible;
        Viewer.Visibility = Visibility.Collapsed;
        BigPlayBtn.Visibility = Visibility.Collapsed;
        ViewerNote.Text = "";
        Player.Play();
        playing = true;
        playClock.Start();
        MarkPlaying();
    }

    void MarkPlaying()
    {
        PlayBtn.Content = playing ? "⏸" : "▶";
        BigPlayBtn.Visibility = !playing && Current?.Item.IsVideo == true && playerFile == null && PlayerBar.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    void ShowTime()
    {
        if (playerFile == null) return;
        clockMovesSeek = true;
        try
        {
            if (Player.NaturalDuration.HasTimeSpan)
            {
                Seek.Value = Math.Min(Seek.Maximum, Player.Position.TotalSeconds);
                TimeText.Text = VideoFacts.Length(Player.Position) + " / " + VideoFacts.Length(Player.NaturalDuration.TimeSpan);
            }
        }
        finally { clockMovesSeek = false; }
    }

    /// <summary>Stops the video and lets go of its file (it may be deleted or replaced next).</summary>
    void StopPlayer()
    {
        playClock.Stop();
        if (playerFile != null)
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
            playerFile = null;
        }
        playing = false;
        Player.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
        MarkPlaying();
    }

    /// <summary>Any other file: what it is – for a ZIP file what is inside – and the buttons below.</summary>
    async Task ShowFile(DirectRow r)
    {
        Picture.Source = null;
        shownFile = null;
        ZoomBar.IsEnabled = false;
        ZoomText.Text = "";
        PlayerBar.Visibility = BigPlayBtn.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Collapsed;
        ViewerNote.Text = "";
        FileCard.Visibility = Visibility.Visible;
        var it = r.Item;
        FileGlyph.Text = FileKinds.Glyph(it.Kind);
        FileTitle.Text = it.Name;
        FileFacts.Text = FileKinds.Describe(it.Name) + (it.Size > 0 ? " · " + HistoryLogic.Size(it.Size) : "");
        bool temporary = !r.Kept && !hub.KeepOnPc;
        FileHint.Text = it.IsArchive
            ? (temporary ? "Open as folder unpacks it into a temporary folder; Unpack to… keeps it where you choose."
                : "Open as folder unpacks it next to its copy on this PC; Unpack to… puts it where you choose.")
            : "Open shows it in its app; Save as… keeps a copy where you like.";
        FileList.Text = "";
        if (!it.IsArchive || it.Size > 200_000_000) return;
        try
        {
            FolderPack.Contents c;
            if (r.LocalFile == null && temporary)
            {
                // What is inside, read from memory (nothing written to disk); a large one only when opened.
                if (it.Size > 20_000_000) return;
                var bytes = await hub.BytesAsync(it);
                if (Current != r) return;
                c = await Task.Run(() => FolderPack.Read(new MemoryStream(bytes)));
            }
            else
            {
                var f = r.LocalFile ?? await Fetch(r);
                if (f == null || Current != r) return;
                c = await Task.Run(() => FolderPack.Read(f));
            }
            if (Current != r) return;
            FileFacts.Text = $"{FileKinds.Describe(it.Name)} · {(c.Files == 1 ? "1 file" : $"{c.Files} files")}"
                             + (c.Folders > 0 ? $" in {(c.Folders == 1 ? "1 folder" : $"{c.Folders} folders")}" : "")
                             + $" · {HistoryLogic.Size(c.Bytes)} unpacked ({HistoryLogic.Size(it.Size)} packed)";
            FileList.Text = string.Join("\n", c.Top.Take(14)) + (c.Top.Count > 14 ? $"\n… and {c.Top.Count - 14} more" : "");
            ShowFacts(r, $"{c.Files} files");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            FileHint.Text = "This ZIP file cannot be read: " + e.Message;
        }
        catch (DirectException e) { Status("Could not load it: " + e.Message); }
    }

    void UpdateActions(DirectRow? r)
    {
        bool any = r != null;
        foreach (var b in new UIElement[] { StarBtn, PinBtn, LabelBox, OpenBtn, FolderBtn, SaveBtn, CopyBtn, DrawBtn, UnpackBtn, UnpackToBtn, ForwardBtn, LinkBtn, DeleteBtn, ViewBtn })
            b.IsEnabled = any;
        DrawBtn.Visibility = r?.Item.IsImage == true ? Visibility.Visible : Visibility.Collapsed;
        // Folder only for a copy that stays on this PC (not for a temporary one).
        FolderBtn.Visibility = r != null && (r.Kept || hub.KeepOnPc) ? Visibility.Visible : Visibility.Collapsed;
        UnpackBtn.Visibility = UnpackToBtn.Visibility = r?.Item.IsArchive == true ? Visibility.Visible : Visibility.Collapsed;
        ForwardBtn.Visibility = hub.Friends.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewBtn.IsEnabled = r != null && (r.Item.IsImage || r.Item.IsVideo);
        DrawBtn.ToolTip = r == null ? DrawBtn.ToolTip
            : $"Draw on a copy – arrows, numbers, notes, highlights, blur – and send it to {r.Friend} (the picture itself stays as it is)";
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

    /// <summary>Full screen, as in the history: the pictures and videos of the list, ← → through them.</summary>
    void ShowFull()
    {
        var viewable = rows.Where(x => x.Item.IsImage || x.Item.IsVideo).ToList();
        if (Current is not { } cur || !viewable.Contains(cur)) return;
        StopPlayer();
        var v = new ImageViewerWindow(viewable, viewable.IndexOf(cur), x => _ = DrawOn((DirectRow)x)) { Owner = this };
        v.Show();
    }

    // ---------- zoom ----------

    double FitScale(bool allowLarger)
    {
        if (Picture.Source is not BitmapSource b || b.PixelWidth == 0) return 1;
        double w = Math.Max(50, Viewer.ViewportWidth - 8), h = Math.Max(50, Viewer.ViewportHeight - 8);
        var s = Math.Min(w / b.PixelWidth, h / b.PixelHeight);
        return allowLarger ? s : Math.Min(1, s);
    }

    /// <summary>Zooms, keeping the point of the picture under the pointer (or the middle of the view) where it is.</summary>
    void ZoomAt(double s, Point? at)
    {
        s = Math.Clamp(s, 0.05, 16);
        var anchor = at ?? new Point(Viewer.ViewportWidth / 2, Viewer.ViewportHeight / 2);
        var before = Picture.Source != null ? Viewer.TranslatePoint(anchor, Picture) : default;
        Zoom.ScaleX = Zoom.ScaleY = s;
        ZoomText.Text = $"{s * 100:0} %";
        if (Picture.Source == null) return;
        Viewer.UpdateLayout();
        var after = Picture.TranslatePoint(before, Viewer);
        Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + after.X - anchor.X);
        Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + after.Y - anchor.Y);
    }

    // ---------- sending ----------

    static bool CanTake(IDataObject d) => d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(DataFormats.Bitmap);

    async Task Take(IDataObject d)
    {
        if (d.GetDataPresent(DataFormats.FileDrop) && d.GetData(DataFormats.FileDrop) is string[] paths)
            await Send(paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList());
        else if (d.GetDataPresent(DataFormats.Bitmap) && d.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            await Send([SavePng(bmp)]);
    }

    async Task Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                await Send(Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToList());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
                await Send([SavePng(img)]);
            else Status("The clipboard has no picture, file or folder");
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
            Filter = "All files|*.*|Pictures|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|Videos|*.mp4;*.webm;*.mov;*.mkv",
        };
        if (d.ShowDialog(this) == true) await Send(d.FileNames.ToList());
    }

    async Task ChooseFolders()
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = $"Folder for {SendTo} (it travels as one ZIP file)" };
        if (d.ShowDialog(this) == true) await Send(d.FolderNames.ToList());
    }

    /// <summary>Sends files and folders to the friend chosen above; a folder is packed into one ZIP file first.</summary>
    async Task Send(List<string> paths)
    {
        if (SendTo is not { } to || paths.Count == 0 || sending) return;
        sending = true;
        UpdateDropZone();
        var packed = new List<string>();
        try
        {
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (File.Exists(p)) { files.Add(p); continue; }
                if (!Directory.Exists(p)) continue;
                var name = Path.GetFileName(p.TrimEnd('\\'));
                SendState.Text = $"Packing {name}…";
                var zip = await Task.Run(() => FolderPack.Pack(p, Path.Combine(AppPaths.WorkDir, "direct-folders"),
                    pc => Dispatcher.BeginInvoke(() => SendState.Text = $"Packing {name}… {pc} %")));
                packed.Add(zip);
                files.Add(zip);
            }
            if (files.Count == 0) { SendState.Text = "Nothing to send: the folder is empty."; return; }
            var sent = await hub.SendAsync(to, files, t => SendState.Text = t);
            SendState.Text = sent.Count == 1 ? $"Sent – {to} sees it now." : $"{sent.Count} sent – {to} sees them now.";
            // What was just sent must be in view.
            if (month != null && month != hub.WatchMonth) month = null;
            if (friend != null && friend != to) friend = null;
            FillFriends();
            FillMonths();
            wantedPath = sent.LastOrDefault()?.Path;
            Rebuild();
            _ = LoadOlder(false); // "All months" again: the other months of the friends shown
        }
        catch (DirectException e) { SendState.Text = "Not sent: " + e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error("direct: pack", e);
            SendState.Text = "Not sent – the folder could not be packed: " + e.Message;
        }
        finally
        {
            foreach (var z in packed)
                try { File.Delete(z); }
                catch { }
            sending = false;
            UpdateDropZone();
        }
    }

    async Task Keys(KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !typing) { e.Handled = true; await Paste(); return; }
        if (typing) return;
        bool picture = Current?.Item.IsImage == true;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add when picture: ZoomAt(Zoom.ScaleX * 1.25, null); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract when picture: ZoomAt(Zoom.ScaleX / 1.25, null); e.Handled = true; break;
            case Key.D0 or Key.NumPad0 when picture: ZoomAt(1, null); e.Handled = true; break;
            case Key.F when picture: ZoomAt(fitScale = FitScale(allowLarger: true), null); e.Handled = true; break;
            case Key.Space when Current?.Item.IsVideo == true: e.Handled = true; await PlayPause(); break;
            case Key.Enter when Current != null && Keyboard.FocusedElement is not Button: e.Handled = true; await OpenCurrent(); break;
            case Key.Delete: if (Current != null) { AskDelete(); e.Handled = true; } break;
            case Key.Escape:
                e.Handled = true;
                if (ConfirmBar.Visibility == Visibility.Visible) ConfirmBar.Visibility = Visibility.Collapsed;
                else if (FriendsPanel.Visibility == Visibility.Visible) await ToggleFriends(false);
                break;
        }
    }

    // ---------- what to do with a file ----------

    async Task OpenCurrent()
    {
        if (Current is not { } r || await Fetch(r) is not { } f) return;
        StopPlayer();
        Shell(f);
    }

    async Task SaveAs()
    {
        if (Current is not { } r || await Fetch(r) is not { } f) return;
        var ext = Path.GetExtension(r.Item.Name);
        var d = new Microsoft.Win32.SaveFileDialog { FileName = r.Item.Name, Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}|All files|*.*" };
        if (d.ShowDialog(this) != true) return;
        try { File.Copy(f, d.FileName, true); Status("Saved: " + d.FileName); }
        catch (Exception e) { Status("Not saved: " + e.Message); }
    }

    async Task DrawOnCurrent()
    {
        if (Current is { Item.IsImage: true } r) await DrawOn(r);
    }

    /// <summary>The drawing editor on a copy of a picture; "Send to …" sends the drawn copy to that friend.</summary>
    async Task DrawOn(DirectRow r)
    {
        if (await Fetch(r) is not { } f) return;
        try { await app.DrawAndSendAsync(f, r.Friend, r.Title); }
        catch (Exception e)
        {
            Log.Error("direct: draw", e);
            Status("The drawing editor failed: " + e.Message);
        }
    }

    /// <summary>A ZIP file as a folder: unpacked next to its copy on this PC (or where chosen) and opened in Explorer.</summary>
    async Task Unpack(bool choose)
    {
        if (Current is not { Item.IsArchive: true } r || await Fetch(r) is not { } f) return;
        string parent;
        if (choose)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = $"Where to unpack {r.Item.Name} (a new folder is made there)" };
            if (d.ShowDialog(this) != true) return;
            parent = d.FolderName;
        }
        else
        {
            if (unpacked.TryGetValue(f, out var done) && Directory.Exists(done)) { Shell(done); return; }
            parent = Path.GetDirectoryName(f)!;
        }
        try
        {
            Status($"Unpacking {r.Item.Name}…");
            // Named after the file (the copy on this PC may carry a prefix: "mine-…" for what you sent).
            var folder = await Task.Run(() => FolderPack.Unpack(f, parent, r.Item.Name));
            if (!choose) unpacked[f] = folder;
            Status(choose || r.Kept ? "Unpacked into " + folder
                : "Unpacked into a temporary folder (it goes when this window closes) – Unpack to… keeps it where you choose");
            Shell(folder);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status("Not unpacked: " + e.Message);
        }
    }

    /// <summary>Send to… : the other friends, each a click away.</summary>
    void ForwardMenu()
    {
        if (Current is not { } r) return;
        var menu = ForwardBtn.ContextMenu!;
        menu.Items.Clear();
        foreach (var f in hub.Friends.Where(f => !string.Equals(f, r.Friend, StringComparison.OrdinalIgnoreCase)))
        {
            var to = f;
            var item = new MenuItem { Header = $"Send to {to}" };
            item.Click += async (_, _) => await Forward(r, to);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = ForwardBtn;
        menu.IsOpen = true;
    }

    async Task Forward(DirectRow r, string to)
    {
        if (await Fetch(r) is not { } f) return;
        try
        {
            Status($"Sending {r.Item.Name} to {to}…");
            await hub.SendAsync(to, [f]);
            Status($"Sent to {to} – {to} sees it now.");
        }
        catch (DirectException e) { Status("Not sent: " + e.Message); }
    }

    /// <summary>Get a link: a copy goes to the user's own Peergos folder with a secret link (copied) – for someone who
    /// is not a Peergos friend. The direct folders themselves never get links.</summary>
    async Task GetLink()
    {
        if (Current is not { } r) return;
        if (!app.Settings.PeergosConfigured) { Status("Sign in to Peergos first (Settings → Peergos)."); return; }
        if (await Fetch(r) is not { } f) return;
        Status($"Uploading {r.Item.Name} for a link…");
        await app.UploadFilesAsync([f]);
        Status("The link is on the clipboard (and in the history).");
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
                           + (r.FromMe ? $"{r.Friend}'s copy stays theirs." : $"{r.Friend} keeps theirs.");
        ConfirmBar.Visibility = Visibility.Visible;
    }

    async Task Delete()
    {
        ConfirmBar.Visibility = Visibility.Collapsed;
        if (Current is not { } r) return;
        StopPlayer(); // the player holds the file
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

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
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
