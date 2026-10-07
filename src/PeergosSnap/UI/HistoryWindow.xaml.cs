using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap.UI;

/// <summary>
/// The history manager: every capture with its preview, label, origin and link, where it is (this PC, Peergos),
/// and deleting on either side or both. Deleting on this PC uses the Recycle Bin; deleting in Peergos removes the
/// file together with its links. Deletes are confirmed first (unless switched off), locked entries are never deleted, and
/// a delete that cannot be done in Peergos (not signed in) says so and lets the user choose.
/// </summary>
public partial class HistoryWindow : Window
{
    readonly TrayController app;
    readonly Dictionary<string, HistoryItem> items = [];
    HashSet<string>? remotePaths;
    /// <summary>The Peergos folder that <see cref="remotePaths"/> lists, and when that listing started.</summary>
    string? remoteFolder;
    DateTime? remoteCheckedAt;
    bool refreshAgain;
    string selectionShown = "";
    /// <summary>Entries being uploaded from this window: one upload each at a time (a second one would make a second
    /// copy whose link nothing tracks).</summary>
    readonly HashSet<string> uploading = [];
    readonly HashSet<string> downloading = [];
    /// <summary>The small pictures Peergos keeps with the files (path → data URL), from its listing and from "previews";
    /// only in memory.</summary>
    readonly Dictionary<string, string> remoteThumbs = new(StringComparer.Ordinal);
    /// <summary>Files asked for a preview already in this window (each once).</summary>
    readonly HashSet<string> previewTried = new(StringComparer.Ordinal);
    bool fetchingPreviews;
    readonly DispatcherTimer rebuildSoon = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer labelSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool showingDetails;
    Func<Task>? pending;
    bool askedDelete; // the confirmation shown is for a delete (its "Don't ask again" applies)
    bool refreshing;
    /// <summary>An entry to show once the list has it (the History button of a capture's card).</summary>
    string? revealId;

    HistoryStore Store => app.History;

    public HistoryWindow(TrayController app)
    {
        this.app = app;
        InitializeComponent();
        Theme.Attach(this);
        ShowBox.SelectedIndex = 0;
        SortBox.SelectedIndex = 0;

        rebuildSoon.Tick += (_, _) => { rebuildSoon.Stop(); Rebuild(); };
        labelSave.Tick += (_, _) => { labelSave.Stop(); Store.Save(); };
        Store.Changed += StoreChanged;
        app.SettingsChanged += SettingsChanged;
        Closed += (_, _) => { Store.Changed -= StoreChanged; app.SettingsChanged -= SettingsChanged; };

        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RebuildSoon();
        };
        ShowBox.SelectionChanged += (_, _) => RebuildSoon();
        SortBox.SelectionChanged += (_, _) => RebuildSoon();
        RefreshBtn.Click += async (_, _) => await Refresh();
        SelectAllBtn.Click += (_, _) => { List.SelectAll(); List.Focus(); };
        SelectNoneBtn.Click += (_, _) => List.UnselectAll();
        CleanBtn.Click += (_, _) => { CleanBtn.ContextMenu.PlacementTarget = CleanBtn; CleanBtn.ContextMenu.IsOpen = true; };
        // Small headings say what a menu's entries do.
        CleanBtn.ContextMenu.Items.Insert(0, MenuParts.Heading("Tidy up the list – no file is deleted"));
        DeleteMore.ContextMenu.Items.Insert(0, MenuParts.Heading("Delete"));
        DownloadBtn.Click += async (_, _) => { if (One() is { } it) await Download(it); };
        RemoveGoneItem.Click += (_, _) => AskRemoveGone();
        ClearItem.Click += (_, _) => AskClear();
        List.SelectionChanged += (_, _) => ShowDetails();
        List.MouseDoubleClick += (_, e) =>
        {
            if (InCheckBox(e.OriginalSource)) return; // a double click on a tick box only ticks
            if (e.OriginalSource is FrameworkElement { DataContext: HistoryItem row }) OpenItem(row);
        };
        // Space must be caught before the list's own handling, which uses it to select the focused row.
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && One() is { } v) { ShowFull(v); e.Handled = true; }
        };
        List.ContextMenu = new ContextMenu();
        List.ContextMenuOpening += (_, e) => { if (!FillRowMenu(List.ContextMenu)) e.Handled = true; };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Selected().FirstOrDefault() is { } it) { OpenItem(it); e.Handled = true; }
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && One() is { Record.Link: { } l } c && c.InPeergos != false) { CopyText(l, "Link copied to the clipboard"); e.Handled = true; }
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && Selected().Count > 0) { DefaultDelete(); e.Handled = true; }
            if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.None && Selected().Count > 0) { ToggleLock(); e.Handled = true; }
        };
        LabelBox.TextChanged += (_, _) =>
        {
            if (!showingDetails || Selected().Count != 1) return;
            var it = Selected()[0];
            it.Record.Label = LabelBox.Text;
            it.Update(it.Local, it.InPeergos, it.RemoteThumb);
            SelectionTitle.Text = (it.Record.Locked ? "🔒 " : "") + it.Title;
            labelSave.Stop();
            labelSave.Start();
        };
        CopyLinkBtn.Click += (_, _) => { if (One()?.Record.Link is { } l) CopyText(l, "Link copied to the clipboard"); };
        OpenLinkBtn.Click += (_, _) => { if (One()?.Record.Link is { } l) Shell(l); };
        OpenFileBtn.Click += (_, _) => { if (One() is { } it && FileOf(it) is { } f) Shell(f); };
        ShowFileBtn.Click += (_, _) => { if (One() is { } it && FileOf(it) is { } f) Process.Start("explorer.exe", "/select,\"" + f + "\""); };
        CopyMediaBtn.Click += (_, _) =>
        {
            if (One()?.Record.File is not { } f) return;
            try { ClipboardService.MediaToClipboard(f); Result((One()!.Record.IsVideo ? "Video" : "Picture") + " copied to the clipboard"); }
            catch (Exception ex) { Result("Could not copy: " + ex.Message, error: true); }
        };
        UploadBtn.Click += async (_, _) =>
        {
            if (One() is not { } it || uploading.Contains(it.Record.Id)) return;
            if (!app.Settings.PeergosConfigured) { AskSignIn("Uploading needs your Peergos account."); return; }
            uploading.Add(it.Record.Id);
            UploadBtn.IsEnabled = false;
            Result("Uploading…");
            BridgeResult r;
            try { r = await app.UploadRecordAsync(it.Record); }
            finally { uploading.Remove(it.Record.Id); }
            // Only into a real listing: without one, a single path would make every other entry count as "not in Peergos".
            if (r.Ok && it.Record.PeergosPath != null) remotePaths?.Add(it.Record.PeergosPath);
            Result(r.Ok ? "Uploaded – the link is on the clipboard" : "Upload failed: " + r.Error, error: !r.Ok);
            Rebuild();
        };
        DrawBtn.Click += async (_, _) => { if (One() is { } it) await Draw(it); };
        FullBtn.Click += (_, _) => { if (One() is { } it) ShowFull(it); };
        PreviewBox.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && One() is { } it) { ShowFull(it); e.Handled = true; } };
        LockBtn.Click += (_, _) => ToggleLock();
        DeleteBtn.Click += (_, _) => DefaultDelete();
        DeleteMore.Click += (_, _) => { MarkDeleteMenu(); DeleteMore.ContextMenu.PlacementTarget = DeleteMore; DeleteMore.ContextMenu.IsOpen = true; };
        DelBothItem.Click += (_, _) => AskDelete(local: true, remote: true);
        DelLocalItem.Click += (_, _) => AskDelete(local: true, remote: false);
        DelRemoteItem.Click += (_, _) => AskDelete(local: false, remote: true);
        RemoveItem.Click += (_, _) => AskRemove();
        ConfirmNo.Click += (_, _) => HideConfirm();
        ConfirmSignIn.Click += (_, _) => { HideConfirm(); app.ShowSettings(0); };
        ConfirmYes.Click += async (_, _) =>
        {
            var action = pending;
            if (ConfirmNever.IsChecked == true && askedDelete) app.UpdateSettings(s => s.ConfirmHistoryDelete = false);
            HideConfirm();
            if (action != null) await action();
        };
        Loaded += async (_, _) =>
        {
            app.DiscoverLocalCaptures();
            Rebuild();
            await Refresh();
        };
    }

    /// <summary>Signing in or out (or another account) while the window is open: look at Peergos again, so what can be
    /// deleted where shows what is really there.</summary>
    void SettingsChanged(Settings before)
    {
        var s = app.Settings;
        if (before.PeergosConfigured == s.PeergosConfigured && before.Username == s.Username && before.AccountFolder == s.AccountFolder
            && before.SessionProtected == s.SessionProtected)
        {
            if (before.HistoryDeleteAction != s.HistoryDeleteAction || before.DeleteBothRemovesEntry != s.DeleteBothRemovesEntry) ShowDetails();
            if (before.PreviewsFromPeergos != s.PreviewsFromPeergos)
            {
                Rebuild();
                _ = FetchPreviews();
            }
            return;
        }
        Dispatcher.BeginInvoke(async () => await Refresh());
    }

    /// <summary>Shows one entry: selected and scrolled to; a filter or search that hides it is cleared.</summary>
    public void Reveal(string id)
    {
        revealId = id;
        if (IsLoaded) Rebuild(); // otherwise the first build of the list, when the window has loaded, shows it
    }

    void StoreChanged() => Dispatcher.BeginInvoke(RebuildSoon);
    void RebuildSoon() { rebuildSoon.Stop(); rebuildSoon.Start(); }

    static string TagOf(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    List<HistoryItem> Selected() => List.SelectedItems.Cast<HistoryItem>().ToList();
    HistoryItem? One() => Selected() is { Count: 1 } s ? s[0] : null;

    /// <summary>Rebuilds the list from the history (filter, search, sort, day groups), keeping the selection.</summary>
    void Rebuild()
    {
        var keep = Selected().Select(i => i.Record.Id).ToHashSet();
        var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool LocalExists(string f) => exists.TryGetValue(f, out var e) ? e : exists[f] = File.Exists(f);

        // Uploaded since Peergos was last looked at (a capture, the tray's Upload files…): it is there, the upload said so.
        if (remotePaths != null && remoteCheckedAt is { } checkedAt)
            foreach (var r in Store.Records.Where(r => r.PeergosPath != null && r.Uploaded >= checkedAt)) remotePaths.Add(r.PeergosPath!);
        foreach (var r in Store.Records)
        {
            if (!items.TryGetValue(r.Id, out var it)) items[r.Id] = it = new HistoryItem(r);
            var thumb = app.Settings.PreviewsFromPeergos && r.PeergosPath != null && remoteThumbs.TryGetValue(r.PeergosPath, out var t) ? t : null;
            it.Update(r.File != null && LocalExists(r.File), HistoryLogic.InPeergos(r, remotePaths, remoteFolder), thumb);
        }
        foreach (var gone in items.Keys.Except(Store.Records.Select(r => r.Id)).ToList()) items.Remove(gone);

        var sort = TagOf(SortBox);
        var shown = HistoryLogic.Sort(HistoryLogic.Filter(Store.Records, TagOf(ShowBox), SearchBox.Text, LocalExists, remotePaths, remoteFolder), sort)
            .Select(r => items[r.Id]).ToList();
        var view = new ListCollectionView(shown);
        if (sort is "newest" or "oldest" or "")
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItem.Day)));
        List.ItemsSource = view;
        foreach (var it in shown.Where(i => keep.Contains(i.Record.Id))) List.SelectedItems.Add(it);
        if (revealId is { } rid)
        {
            if (!items.TryGetValue(rid, out var wanted)) revealId = null; // not in the history any more
            else if (shown.Contains(wanted))
            {
                revealId = null;
                List.SelectedItems.Clear();
                List.SelectedItem = wanted;
                List.ScrollIntoView(wanted);
                List.Focus();
            }
            else if (ShowBox.SelectedIndex != 0 || SearchBox.Text.Length > 0)
            {
                ShowBox.SelectedIndex = 0; // both rebuild the list soon, which then shows the entry
                SearchBox.Text = "";
            }
            else revealId = null;
        }

        Empty.Visibility = Store.Records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int onPc = items.Values.Count(i => i.Local), inPeergos = items.Values.Count(i => i.InPeergos == true);
        int missing = items.Values.Count(i => !i.Local && !i.Record.IsUpload);
        Counts.Text = $"{Store.Records.Count} captures · {onPc} on this PC · " +
                      (remotePaths == null ? "Peergos not checked" : $"{inPeergos} in Peergos") +
                      (missing > 0 ? $" · {missing} not on this PC" : "") +
                      (shown.Count != Store.Records.Count ? $" · showing {shown.Count}" : "");
        ShowDetails();
    }

    /// <summary>Looks at the captures folder and the Peergos folder again (links of older uploads are read back).</summary>
    async Task Refresh()
    {
        // A refresh asked for while one runs (e.g. signing in during the first look) runs again afterwards, so the
        // window never keeps showing the Peergos state of before.
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            app.DiscoverLocalCaptures();
            if (!app.Settings.PeergosConfigured)
            {
                remotePaths = null;
                remoteFolder = null;
                remoteCheckedAt = null;
                RemoteState.Text = "Not signed in to Peergos – files there are not shown";
                Rebuild();
                return;
            }
            RemoteState.Text = "Looking at your Peergos folder…";
            var started = DateTime.Now;
            var used = app.Settings.Clone();
            var (r, files) = await Uploader.ListAsync(used);
            if (!r.Ok)
            {
                remotePaths = null;
                remoteFolder = null;
                remoteCheckedAt = null;
                RemoteState.Text = "Peergos: " + r.Error;
                // Only the sign-in that was refused is dropped, not one made meanwhile.
                if (Uploader.NeedsSignIn(r.Error) && app.Settings.Session == used.Session) app.UpdateSettings(s => s.Session = "");
                Rebuild();
                return;
            }
            remotePaths = files.Select(f => f.Path).ToHashSet();
            remoteFolder = r.PeergosPath;
            remoteCheckedAt = started;
            foreach (var f in files.Where(f => f.Thumb != null)) remoteThumbs[f.Path] = f.Thumb!;
            var added = HistoryLogic.Discover(Store.Records, CaptureFiles.Scan(AppPaths.CacheDir), files.Where(f => !f.Folder).ToList(), File.GetLastWriteTime, Store.Dismissed);
            if (added.Count > 0) Store.AddRange(added);
            else Store.Save(); // links found for known records
            RemoteState.Text = $"Peergos: {files.Count(f => !f.Folder)} files in {r.PeergosPath}, checked {DateTime.Now:HH:mm}";
            Rebuild();
            _ = FetchPreviews();
        }
        catch (Exception e)
        {
            Log.Error("history refresh", e);
            RemoteState.Text = "Peergos: " + e.Message;
        }
        finally
        {
            refreshing = false;
            RefreshBtn.IsEnabled = true;
            if (refreshAgain)
            {
                refreshAgain = false;
                _ = Dispatcher.BeginInvoke(async () => await Refresh());
            }
        }
    }

    // ---------- details ----------

    void ShowDetails()
    {
        showingDetails = false;
        var sel = Selected();
        // A question stays while the same captures are selected: background saves and refreshes rebuild the list
        // (e.g. right after a delete refused for an ended sign-in), and must not take the question away. Locking or
        // unlocking one of them, or a new Peergos copy, does: the question was about what they were.
        var shownNow = string.Join(",", sel.Select(i => $"{i.Record.Id}:{i.Record.Locked}:{i.Record.PeergosPath}"));
        if (shownNow != selectionShown) HideConfirm();
        selectionShown = shownNow;
        NothingSelected.Visibility = sel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = sel.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in Actions.Children.OfType<UIElement>()) b.IsEnabled = sel.Count > 0;
        if (sel.Count == 0) return;
        MarkDefaultDelete(); // the default may have changed in Settings meanwhile
        bool signedIn = app.Settings.PeergosConfigured;
        bool anyLocal = sel.Any(i => i.Local && !i.Record.Locked);
        bool anyRemote = sel.Any(i => i.Record.PeergosPath != null && i.InPeergos != false && !i.Record.Locked);
        bool allLocked = sel.All(i => i.Record.Locked);
        LockBtn.IsChecked = allLocked;
        LockText.Text = allLocked ? "Locked" : "Lock";
        LockGlyph.Text = allLocked ? "\uE72E" : "\uE785";
        DeleteBtn.IsEnabled = !allLocked;
        DeleteBtn.ToolTip = allLocked ? "Locked – unlock first" : DeleteBtn.ToolTip;
        DelLocalItem.IsEnabled = anyLocal;
        DelRemoteItem.IsEnabled = anyRemote;
        DelBothItem.IsEnabled = anyLocal || anyRemote;
        RemoveItem.IsEnabled = !allLocked;
        DelRemoteItem.Header = "From Peergos" + (anyRemote && !signedIn ? " (sign in first)" : "");
        DelBothItem.Header = "From this PC and Peergos" + (anyRemote && !signedIn ? " (Peergos: sign in first)" : "");
        if (sel.Count > 1)
        {
            SinglePanel.Visibility = Visibility.Collapsed;
            PreviewBox.Visibility = Visibility.Collapsed;
            int locked = sel.Count(i => i.Record.Locked);
            SelectionTitle.Text = $"{sel.Count} captures selected" + (locked > 0 ? $" ({locked} locked)" : "");
            CopyLinkBtn.IsEnabled = OpenLinkBtn.IsEnabled = OpenFileBtn.IsEnabled = ShowFileBtn.IsEnabled = CopyMediaBtn.IsEnabled = false;
            UploadBtn.IsEnabled = DrawBtn.IsEnabled = FullBtn.IsEnabled = false;
            DownloadBtn.Visibility = Visibility.Collapsed;
            return;
        }
        var it = sel[0];
        var r = it.Record;
        SinglePanel.Visibility = Visibility.Visible;
        PreviewBox.Visibility = Visibility.Visible;
        SelectionTitle.Text = (r.Locked ? "🔒 " : "") + it.Title;
        LabelBox.Text = r.Label;
        InfoTaken.Text = r.Created.ToString("dddd d MMMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        InfoAppLabel.Text = r.IsUpload ? "Uploaded from" : "From";
        InfoApp.Text = r.IsUpload ? r.Source ?? "–"
            : string.IsNullOrEmpty(r.App) ? "–" : r.App + (string.IsNullOrEmpty(r.WindowTitle) ? "" : " – " + r.WindowTitle);
        var size = new List<string>();
        if (r.Width > 0) size.Add($"{r.Width} × {r.Height}");
        if (r.IsVideo && r.Seconds > 0) size.Add(it.Duration);
        if (r.Bytes > 0) size.Add(HistoryLogic.Size(r.Bytes));
        InfoSize.Text = size.Count > 0 ? string.Join(" · ", size) : "–";
        InfoLocal.Text = (it.Local ? r.File : r.IsUpload ? "No copy kept by Peergos Snap" : r.File != null ? "No longer on this PC" : "Not on this PC")
                         + (FileOf(it) == null && it.RemoteThumb != null ? " – the preview comes from Peergos" : "");
        InfoRemote.Text = it.PeergosTip + (r.PeergosPath != null && !signedIn ? " – sign in to Peergos to see or delete it" : "");
        InfoLink.Text = r.Link ?? "–";
        CopyLinkBtn.IsEnabled = OpenLinkBtn.IsEnabled = r.Link != null && it.InPeergos != false;
        OpenFileBtn.IsEnabled = ShowFileBtn.IsEnabled = FileOf(it) != null;
        CopyMediaBtn.IsEnabled = it.Local;
        CopyMediaText.Text = r.IsVideo ? "Copy video" : "Copy picture";
        UploadBtn.IsEnabled = it.Local && it.InPeergos != true && !uploading.Contains(r.Id);
        UploadBtn.Visibility = it.InPeergos == true || r.IsUpload ? Visibility.Collapsed : Visibility.Visible;
        DrawBtn.IsEnabled = it.Local && it.IsViewablePicture;
        DrawBtn.Visibility = r.IsVideo || r.IsUpload ? Visibility.Collapsed : Visibility.Visible;
        FullBtn.IsEnabled = it.PreviewFile != null;
        // Only in Peergos: Download brings a copy back, and then the buttons for a file here work again.
        DownloadBtn.Visibility = CanDownload(it) ? Visibility.Visible : Visibility.Collapsed;
        DownloadBtn.IsEnabled = CanDownload(it) && signedIn && !downloading.Contains(r.Id);
        _ = ShowPreview(it);
        showingDetails = true;
    }

    /// <summary>The file on this PC an entry opens: the capture, or the original of an uploaded file (only opened).</summary>
    static string? FileOf(HistoryItem it) =>
        it.Local ? it.Record.File : it.Record.IsUpload && it.Record.Source is { } s && (File.Exists(s) || Directory.Exists(s)) ? s : null;

    async Task ShowPreview(HistoryItem it)
    {
        Preview.Source = null;
        Preview.Stretch = System.Windows.Media.Stretch.Uniform;
        var r = it.Record;
        var file = it.PreviewFile;
        if (file == null)
        {
            // Not on this PC: a video's still made earlier, else the small picture from Peergos – at its own size.
            var still = r.IsVideo ? await ThumbFiles.VideoThumb(r) : null;
            var small = still != null ? await Task.Run(() => ThumbFiles.Load(still, 0)) : it.RemotePreview;
            if (small != null)
            {
                PreviewNote.Text = "";
                Preview.Stretch = System.Windows.Media.Stretch.None;
                if (One() == it) Preview.Source = small;
                return;
            }
        }
        PreviewNote.Text = file != null ? ""
            : r.IsFolder ? "A folder uploaded to Peergos – open its link to see it."
            : r.IsUpload ? "No preview for this file." + (r.Link != null ? " Open the link to see it." : "")
            : "No preview: the file is not on this PC." + (r.Link != null ? " Open the link to see it." : "");
        if (file == null) return;
        string? source = r.IsVideo ? await ThumbFiles.VideoThumb(r) : file;
        if (source == null) { PreviewNote.Text = "No preview"; return; }
        var img = await Task.Run(() => ThumbFiles.Load(source, 900));
        if (One() == it) Preview.Source = img;
    }

    // ---------- actions ----------

    /// <summary>The question above the preview. <paramref name="yes"/> null = only Cancel (and Sign in when offered).</summary>
    void Confirm(string text, string? yes, Func<Task>? action, bool isDelete = false, bool offerSignIn = false)
    {
        pending = action;
        askedDelete = isDelete;
        ConfirmText.Text = text;
        ConfirmYes.Content = yes ?? "";
        ConfirmYes.Visibility = yes == null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmSignIn.Visibility = offerSignIn ? Visibility.Visible : Visibility.Collapsed;
        ConfirmNever.IsChecked = false;
        ConfirmNever.Visibility = isDelete ? Visibility.Visible : Visibility.Collapsed;
        ConfirmBar.Visibility = Visibility.Visible;
        ConfirmBar.BringIntoView();
    }

    void HideConfirm()
    {
        pending = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    void Result(string text, bool error = false)
    {
        ActionResult.Text = text;
        if (error) ActionResult.Foreground = System.Windows.Media.Brushes.Firebrick;
        else ActionResult.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
    }

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>Asks before a delete – or does it at once when Settings → Files &amp; history says not to ask.</summary>
    async Task ConfirmDelete(string text, string yes, Func<Task> action)
    {
        if (!app.Settings.ConfirmHistoryDelete) { await action(); return; }
        Confirm(text, yes, action, isDelete: true);
    }

    void AskSignIn(string why)
    {
        SelectFirstIfNone();
        Confirm(why + " You are not signed in to Peergos.", null, null, offerSignIn: true);
    }

    async void AskDelete(bool local, bool remote)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var byRecord = sel.ToDictionary(i => i.Record);
        var plan = HistoryLogic.PlanDelete(sel.Select(i => i.Record).ToList(), local, remote, app.Settings.PeergosConfigured, File.Exists, remotePaths, remoteFolder);
        var lockedNote = plan.Locked.Count > 0 ? $" {Count(plan.Locked.Count, "locked entry is", "locked entries are")} left out." : "";
        if (plan.NeedSignIn.Count > 0)
        {
            // Never fail silently: say what cannot be done now, and let the user choose.
            var text = $"{Count(plan.NeedSignIn.Count, "file", "files")} in Peergos cannot be deleted now: you are not signed in to Peergos.";
            if (plan.Local.Count > 0)
            {
                Confirm(text + $" Delete only the {Count(plan.Local.Count, "file", "files")} on this PC now? The entries stay, so you can delete in Peergos after signing in." + lockedNote,
                    "Delete on this PC only", () => RunDelete(plan with { NeedSignIn = [] }, byRecord, removeEntries: false), offerSignIn: true);
            }
            else Confirm(text + " Sign in, then delete again." + lockedNote, null, null, offerSignIn: true);
            return;
        }
        if (plan.Nothing)
        {
            Result(plan.Locked.Count > 0 && plan.Locked.Count == sel.Count ? "Locked – unlock it first (Lock button or L)." : "Nothing to delete there." + lockedNote);
            return;
        }
        var parts = new List<string>();
        if (plan.Local.Count > 0) parts.Add($"{plan.Local.Count} file{(plan.Local.Count == 1 ? "" : "s")} on this PC go{(plan.Local.Count == 1 ? "es" : "")} to the Recycle Bin");
        if (plan.Remote.Count > 0) parts.Add($"{Count(plan.Remote.Count, "file", "files")} will be deleted from your Peergos – "
                                             + (plan.Remote.Count == 1 ? "its link stops" : "their links stop") + " working, and this cannot be undone");
        var mirrorNote = plan.Local.Any(r => r.MirrorFile != null) ? " Copies in your mirror folder are kept." : "";
        // Deleting from both places also removes the entries (Settings → Files & history can keep them instead).
        bool removeEntries = local && remote && app.Settings.DeleteBothRemovesEntry;
        var touched = plan.Local.Concat(plan.Remote).Distinct().Count();
        var entries = touched == 1 ? "the entry" : "the entries";
        var entryNote = removeEntries ? $" {char.ToUpper(entries[0])}{entries[1..]} leave{(touched == 1 ? "s" : "")} the history." : $" The history keeps {entries}.";
        await ConfirmDelete(string.Join("; ", parts) + "." + mirrorNote + entryNote + lockedNote, "Yes, delete",
            () => RunDelete(plan, byRecord, removeEntries));
    }

    async Task RunDelete(DeletePlan plan, Dictionary<HistoryRecord, HistoryItem> byRecord, bool removeEntries)
    {
        // Asked a moment ago: an entry locked since then is still never deleted.
        var lockedSince = plan.Local.Concat(plan.Remote).Where(r => r.Locked).Distinct().ToList();
        if (lockedSince.Count > 0)
        {
            plan = plan with { Local = plan.Local.Where(r => !r.Locked).ToList(), Remote = plan.Remote.Where(r => !r.Locked).ToList() };
            if (plan.Nothing) { Result("Locked – nothing was deleted."); Rebuild(); return; }
        }
        try { await RunDeleteSteps(plan, byRecord, removeEntries); }
        catch (Exception e)
        {
            // Never silently: what failed is said, and the entries stay for another try.
            Log.Error("history delete", e);
            Result("The delete stopped: " + e.Message + " – the entries stay, try again.", error: true);
            Rebuild();
        }
    }

    async Task RunDeleteSteps(DeletePlan plan, Dictionary<HistoryRecord, HistoryItem> byRecord, bool removeEntries)
    {
        var messages = new List<string>();
        bool problem = false;
        bool needSignIn = false;
        var localGone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in byRecord.Keys.Where(r => r.File != null && !File.Exists(r.File))) localGone.Add(r.File!);
        if (plan.Local.Count > 0)
        {
            var failed = Recycle.Delete(plan.Local.Select(r => r.File!));
            foreach (var r in plan.Local.Where(r => !failed.Contains(r.File!))) localGone.Add(r.File!);
            if (failed.Count == 0) messages.Add($"{plan.Local.Count} moved to the Recycle Bin");
            else { messages.Add($"{failed.Count} could not be moved to the Recycle Bin"); problem = true; }
        }
        var remoteGone = new HashSet<string>(StringComparer.Ordinal);
        var deletedThere = new List<HistoryRecord>();
        if (plan.Remote.Count > 0)
        {
            Result("Deleting in Peergos…");
            var (r, deleted, missing, failed) = await Uploader.DeleteAsync(app.Settings.Clone(), plan.Remote.Select(x => x.PeergosPath!));
            foreach (var rec in plan.Remote.Where(x => deleted.Contains(x.PeergosPath!) || missing.Contains(x.PeergosPath!)))
            {
                remoteGone.Add(rec.PeergosPath!);
                remotePaths?.Remove(rec.PeergosPath!);
                deletedThere.Add(rec);
            }
            int gone = deleted.Count + missing.Count;
            if (gone < plan.Remote.Count)
            {
                problem = true;
                var why = failed.FirstOrDefault() ?? r.Error ?? "unknown error";
                needSignIn = Uploader.NeedsSignIn(r.Error) || Uploader.NeedsSignIn(why);
                messages.Add($"{(gone > 0 ? $"{gone} deleted from Peergos, " : "")}{plan.Remote.Count - gone} NOT deleted in Peergos ({why}) – the entr{(plan.Remote.Count - gone == 1 ? "y stays" : "ies stay")}, try again");
                if (needSignIn) app.UpdateSettings(s => s.Session = ""); // the sign-in ended: show it everywhere
            }
            else messages.Add($"{gone} deleted from Peergos");
        }
        // Which entries leave the history is decided while their Peergos paths are still set: an entry without a copy
        // on this PC (an uploaded file or folder, a capture only in Peergos) would otherwise look as if it never had a file.
        var touched = plan.Local.Concat(plan.Remote).Distinct().ToList();
        List<string> ids = removeEntries ? HistoryLogic.RemovableAfterDelete(touched, localGone, remoteGone) : [];
        foreach (var rec in deletedThere)
        {
            rec.PeergosPath = null;
            rec.Link = null;
        }
        Store.Save();
        if (removeEntries)
        {
            // Not dismissed: a file restored from the Recycle Bin comes back into the history.
            if (ids.Count > 0) Store.Remove(ids, dismiss: false);
            if (ids.Count > 0) messages.Add($"{Count(ids.Count, "entry", "entries")} removed from the history");
            if (ids.Count < touched.Count && !problem) messages.Add($"{Count(touched.Count - ids.Count, "entry stays", "entries stay")} (not deleted everywhere)");
        }
        Result(string.Join(" · ", messages), error: problem);
        Rebuild();
        if (needSignIn) AskSignIn("Peergos refused the delete because your sign-in has ended.");
    }

    /// <summary>The Delete key and the Delete button: the action chosen in Settings → Files & history (from both places by default).</summary>
    void DefaultDelete()
    {
        switch (app.Settings.HistoryDeleteAction)
        {
            case HistoryDelete.Local: AskDelete(local: true, remote: false); break;
            case HistoryDelete.Peergos: AskDelete(local: false, remote: true); break;
            case HistoryDelete.Entry: AskRemove(); break;
            default:
                // "From both" also works when the capture is only on one side; with nothing left anywhere it removes the entry.
                if (DelLocalItem.IsEnabled || DelRemoteItem.IsEnabled) AskDelete(local: true, remote: true);
                else AskRemove();
                break;
        }
    }

    static readonly Dictionary<HistoryDelete, string> DeleteNames = new()
    {
        [HistoryDelete.Both] = "Delete", [HistoryDelete.Local] = "Delete on PC", [HistoryDelete.Peergos] = "Delete in Peergos", [HistoryDelete.Entry] = "Remove entry",
    };

    /// <summary>The Delete button does the default delete; its text and tooltip say which.</summary>
    void MarkDefaultDelete()
    {
        var d = app.Settings.HistoryDeleteAction;
        DeleteText.Text = DeleteNames[d];
        DeleteBtn.ToolTip = d switch
        {
            HistoryDelete.Both => app.Settings.DeleteBothRemovesEntry
                ? "Deletes the file on this PC (Recycle Bin) and in Peergos, and removes the entry from the history"
                : "Deletes the file on this PC (Recycle Bin) and in Peergos; the history keeps the entry",
            HistoryDelete.Local => "Moves the file on this PC to the Recycle Bin",
            HistoryDelete.Peergos => "Deletes the file from your Peergos; its link stops working",
            _ => "Only the history entry; the files stay",
        } + "  (Delete key; ▾ for the other ways)" + (app.Settings.ConfirmHistoryDelete ? "" : " – without asking");
        MarkDeleteMenu();
    }

    void MarkDeleteMenu()
    {
        var d = app.Settings.HistoryDeleteAction;
        DelBothItem.FontWeight = d == HistoryDelete.Both ? FontWeights.SemiBold : FontWeights.Normal;
        DelLocalItem.FontWeight = d == HistoryDelete.Local ? FontWeights.SemiBold : FontWeights.Normal;
        DelRemoteItem.FontWeight = d == HistoryDelete.Peergos ? FontWeights.SemiBold : FontWeights.Normal;
        RemoveItem.FontWeight = d == HistoryDelete.Entry ? FontWeights.SemiBold : FontWeights.Normal;
    }

    async void AskRemove()
    {
        var sel = Selected();
        var open = sel.Where(i => !i.Record.Locked).ToList();
        if (open.Count == 0) { if (sel.Count > 0) Result("Locked – unlock it first (Lock button or L)."); return; }
        var lockedNote = open.Count < sel.Count ? $" {Count(sel.Count - open.Count, "locked entry stays", "locked entries stay")}." : "";
        await ConfirmDelete($"Remove {open.Count} entr{(open.Count == 1 ? "y" : "ies")} from the history? The files stay where they are (this PC, Peergos) and are not added back later." + lockedNote,
            "Yes, remove", () =>
            {
                Store.Remove(open.Select(i => i.Record.Id));
                Result($"{open.Count} removed from the history" + lockedNote);
                return Task.CompletedTask;
            });
    }

    /// <summary>Locks the selected entries (or unlocks them when all are locked already).</summary>
    void ToggleLock()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        bool lockThem = !sel.All(i => i.Record.Locked);
        foreach (var it in sel) it.Record.Locked = lockThem;
        Store.Save();
        Result(lockThem ? $"{Count(sel.Count, "entry", "entries")} locked: never deleted or removed until unlocked"
                        : $"{Count(sel.Count, "entry", "entries")} unlocked");
    }

    async Task Draw(HistoryItem it)
    {
        try
        {
            var made = await app.DrawOnRecordAsync(it.Record);
            if (made == null) return;
            Rebuild();
            if (items.TryGetValue(made.Id, out var fresh)) { List.SelectedItems.Clear(); List.SelectedItem = fresh; List.ScrollIntoView(fresh); }
        }
        catch (Exception e)
        {
            Log.Error("draw", e);
            Result("The drawing editor failed: " + e.Message, error: true);
        }
    }

    /// <summary>Full screen, with ← → through the captures as listed.</summary>
    void ShowFull(HistoryItem it)
    {
        var shown = List.Items.Cast<HistoryItem>().ToList();
        var v = new ImageViewerWindow(shown, Math.Max(0, shown.IndexOf(it)), x => _ = Draw((HistoryItem)x)) { Owner = this };
        v.Show();
    }

    /// <summary>Runs a button's action as if it was clicked (the right-click menu offers the same actions).</summary>
    static void Press(System.Windows.Controls.Primitives.ButtonBase b)
    {
        if (b.IsEnabled) b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    /// <summary>Is this element (a click's source) inside a row's tick box?</summary>
    static bool InCheckBox(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is CheckBox) return true;
        return false;
    }

    /// <summary>A tick box was clicked: the keyboard goes to the list, so Delete, L, Enter and Ctrl+C work on the ticked rows.</summary>
    void Tick_Click(object sender, RoutedEventArgs e) => List.Focus();

    /// <summary>Right click on the list: everything for the selected captures in one menu, in groups with a small heading
    /// each. Entries that cannot apply to this entry are left out; ones that apply but not now are greyed.</summary>
    bool FillRowMenu(ContextMenu m)
    {
        var sel = Selected();
        if (sel.Count == 0) return false;
        m.Items.Clear();
        if (sel.Count == 1 && sel[0] is var one)
        {
            var r = one.Record;
            bool onPc = FileOf(one) != null, linked = r.Link != null && one.InPeergos != false;
            MenuParts.Group(m, "Open",
                one.PreviewFile != null ? MenuParts.Entry("View full screen", () => ShowFull(one)) : null,
                onPc ? MenuParts.Entry("Open", () => OpenItem(one)) : null,
                onPc ? MenuParts.Entry("Show in its folder", () => Process.Start("explorer.exe", "/select,\"" + FileOf(one) + "\"")) : null,
                CanDownload(one)
                    ? MenuParts.Entry("Download a copy to this PC", () => _ = Download(one), app.Settings.PeergosConfigured && !downloading.Contains(r.Id)) : null,
                linked ? MenuParts.Entry("Open the link in the browser", () => Shell(r.Link!)) : null);
            MenuParts.Group(m, "Copy and share",
                linked ? MenuParts.Entry("Copy link", () => CopyText(r.Link!, "Link copied to the clipboard")) : null,
                !r.IsUpload && one.Local ? MenuParts.Entry(r.IsVideo ? "Copy video" : "Copy picture", () => Press(CopyMediaBtn)) : null,
                !r.IsUpload && one.Local && one.InPeergos != true
                    ? MenuParts.Entry("Upload and copy the link", () => Press(UploadBtn), !uploading.Contains(r.Id)) : null,
                !r.IsUpload && !r.IsVideo && one.Local && one.IsViewablePicture ? MenuParts.Entry("Draw on a copy…", () => _ = Draw(one)) : null);
        }
        bool allLocked = sel.All(i => i.Record.Locked);
        MenuParts.Group(m, "Protect", MenuParts.Entry(allLocked ? "Unlock" : "Lock – never deleted until unlocked", ToggleLock));
        MarkDefaultDelete();
        // The default delete (the Delete button's) stands out, as in the button's ▾ menu.
        MenuItem Del(MenuItem like, Action run)
        {
            var e = MenuParts.Entry(like.Header as string ?? "", run, like.IsEnabled);
            e.FontWeight = like.FontWeight;
            return e;
        }
        MenuParts.Group(m, "Delete",
            Del(DelBothItem, () => AskDelete(true, true)),
            Del(DelLocalItem, () => AskDelete(true, false)),
            Del(DelRemoteItem, () => AskDelete(false, true)),
            Del(RemoveItem, AskRemove));
        return true;
    }

    /// <summary>A file that is only in Peergos (not a folder) can be downloaded.</summary>
    bool CanDownload(HistoryItem it) => FileOf(it) == null && it.InPeergos == true && it.Record is { IsFolder: false, PeergosPath: not null };

    /// <summary>Download: a copy from Peergos back on this PC – a capture where it was (or into the captures folder),
    /// an uploaded file where it came from (or into Downloads). The entry then works like one on this PC again.</summary>
    async Task Download(HistoryItem it)
    {
        var r = it.Record;
        if (!CanDownload(it) || downloading.Contains(r.Id)) return;
        if (!app.Settings.PeergosConfigured) { AskSignIn("Downloading needs your Peergos account."); return; }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var target = HistoryLogic.DownloadTarget(r, AppPaths.CacheDir, app.Settings.Subfolders, downloads, File.Exists, Directory.Exists);
        if (target == null) return;
        downloading.Add(r.Id);
        ShowDetails();
        Result($"Downloading {r.Name}…");
        BridgeResult res;
        try
        {
            res = await Uploader.GetAsync(app.Settings.Clone(), r.PeergosPath!, target,
                pct => Dispatcher.BeginInvoke(() => Result($"Downloading {r.Name}… {pct} %")));
        }
        catch (Exception e)
        {
            Log.Error("history download", e);
            res = new BridgeResult(false, null, null, e.Message);
        }
        finally { downloading.Remove(r.Id); }
        if (!res.Ok || !File.Exists(target))
        {
            Result("Not downloaded: " + (res.Error ?? "the file did not arrive"), error: true);
            ShowDetails();
            return;
        }
        if (r.IsUpload) r.Source = target;
        else r.File = target;
        if (r.Bytes <= 0) r.Bytes = new FileInfo(target).Length;
        Store.Save();
        Rebuild();
        Result("Downloaded: " + target);
    }

    /// <summary>Entries only in Peergos without a small picture get one: Peergos makes it from the picture and keeps it
    /// with the file (Settings → Files & history → previews from Peergos). Each is asked once per window.</summary>
    async Task FetchPreviews()
    {
        if (fetchingPreviews || !app.Settings.PreviewsFromPeergos || !app.Settings.PeergosConfigured || remotePaths == null) return;
        var want = items.Values
            .Where(i => !i.Local && i.InPeergos == true && i.Record.PeergosPath is { } p && !remoteThumbs.ContainsKey(p) && !previewTried.Contains(p)
                        && FileKinds.Of(p) == FileKind.Picture)
            .Select(i => i.Record.PeergosPath!).Take(60).ToList();
        if (want.Count == 0) return;
        fetchingPreviews = true;
        try
        {
            foreach (var p in want) previewTried.Add(p);
            var (r, thumbs) = await Uploader.PreviewsAsync(app.Settings.Clone(), want);
            if (!r.Ok) { Log.Error("history previews: " + r.Error); return; }
            foreach (var (p, t) in thumbs) remoteThumbs[p] = t;
            if (thumbs.Count > 0) Rebuild();
        }
        catch (Exception e) { Log.Error("history previews", e); } // the entries just show no preview
        finally { fetchingPreviews = false; }
    }

    void AskRemoveGone()
    {
        var gone = HistoryLogic.Gone(Store.Records, File.Exists, remotePaths, remoteFolder);
        if (gone.Count == 0)
        {
            Result(remotePaths == null && app.Settings.PeergosConfigured
                ? "No entries without files found (Peergos is not checked yet – click Refresh first)."
                : "Every entry still leads to a file.");
            SelectFirstIfNone();
            return;
        }
        SelectFirstIfNone();
        Confirm($"Remove {gone.Count} entr{(gone.Count == 1 ? "y" : "ies")} whose files are neither on this PC nor in Peergos? Locked entries stay.", "Yes, remove", () =>
        {
            // Checked again: a refresh since the question may have found a file for one of them.
            var still = HistoryLogic.Gone(gone, File.Exists, remotePaths, remoteFolder);
            Store.Remove(still.Select(g => g.Id));
            Result($"{Count(still.Count, "entry", "entries")} removed" + (still.Count < gone.Count ? $" ({gone.Count - still.Count} kept: a file turned up)" : ""));
            return Task.CompletedTask;
        });
    }

    void AskClear()
    {
        if (Store.Records.Count == 0) return;
        SelectFirstIfNone();
        int locked = Store.Records.Count(r => r.Locked);
        Confirm($"Clear the whole history ({Count(Store.Records.Count - locked, "entry", "entries")})? No file is deleted – only the list is emptied."
                + (locked > 0 ? $" {Count(locked, "locked entry stays", "locked entries stay")}." : ""), "Yes, clear", () =>
        {
            Store.Clear();
            Result(locked > 0 ? $"The history is cleared; {Count(locked, "locked entry stays", "locked entries stay")}" : "The history is empty");
            return Task.CompletedTask;
        });
    }

    /// <summary>The confirmation bar lives in the details panel; make sure it is visible.</summary>
    void SelectFirstIfNone()
    {
        if (Selected().Count == 0 && List.Items.Count > 0) List.SelectedIndex = 0;
        Details.Visibility = Visibility.Visible;
        NothingSelected.Visibility = Visibility.Collapsed;
    }

    void OpenItem(HistoryItem it)
    {
        if (FileOf(it) is { } f) Shell(f);
        else if (it.Record.Link != null) Shell(it.Record.Link);
    }

    void CopyText(string text, string done)
    {
        try { ClipboardService.SetText(text); Result(done); }
        catch (Exception e) { Result("Could not copy: " + e.Message, error: true); }
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
