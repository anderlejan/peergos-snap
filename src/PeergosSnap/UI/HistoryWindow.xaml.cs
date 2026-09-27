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
/// file together with its links. Every delete is confirmed first.
/// </summary>
public partial class HistoryWindow : Window
{
    readonly TrayController app;
    readonly Dictionary<string, HistoryItem> items = [];
    HashSet<string>? remotePaths;
    readonly DispatcherTimer rebuildSoon = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer labelSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool showingDetails;
    Func<Task>? pending;
    bool refreshing;

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
        Closed += (_, _) => Store.Changed -= StoreChanged;

        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RebuildSoon();
        };
        ShowBox.SelectionChanged += (_, _) => RebuildSoon();
        SortBox.SelectionChanged += (_, _) => RebuildSoon();
        RefreshBtn.Click += async (_, _) => await Refresh();
        CleanBtn.Click += (_, _) => { CleanBtn.ContextMenu.PlacementTarget = CleanBtn; CleanBtn.ContextMenu.IsOpen = true; };
        RemoveGoneItem.Click += (_, _) => AskRemoveGone();
        ClearItem.Click += (_, _) => AskClear();
        List.SelectionChanged += (_, _) => ShowDetails();
        List.MouseDoubleClick += (_, e) => { if (Selected().FirstOrDefault() is { } it && e.OriginalSource is FrameworkElement { DataContext: HistoryItem }) OpenItem(it); };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Selected().FirstOrDefault() is { } it) { OpenItem(it); e.Handled = true; }
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Selected().FirstOrDefault() is { Record.Link: { } l }) { CopyText(l, "Link copied"); e.Handled = true; }
        };
        LabelBox.TextChanged += (_, _) =>
        {
            if (!showingDetails || Selected().Count != 1) return;
            var it = Selected()[0];
            it.Record.Label = LabelBox.Text;
            it.Update(it.Local, it.InPeergos);
            SelectionTitle.Text = it.Title;
            labelSave.Stop();
            labelSave.Start();
        };
        CopyLinkBtn.Click += (_, _) => { if (One()?.Record.Link is { } l) CopyText(l, "Link copied to the clipboard"); };
        OpenLinkBtn.Click += (_, _) => { if (One()?.Record.Link is { } l) Shell(l); };
        OpenFileBtn.Click += (_, _) => { if (One()?.Record.File is { } f) Shell(f); };
        ShowFileBtn.Click += (_, _) => { if (One()?.Record.File is { } f) Process.Start("explorer.exe", "/select,\"" + f + "\""); };
        CopyMediaBtn.Click += (_, _) =>
        {
            if (One()?.Record.File is not { } f) return;
            try { ClipboardService.MediaToClipboard(f); Result((One()!.Record.IsVideo ? "Video" : "Picture") + " copied to the clipboard"); }
            catch (Exception ex) { Result("Could not copy: " + ex.Message); }
        };
        UploadBtn.Click += async (_, _) =>
        {
            if (One() is not { } it) return;
            UploadBtn.IsEnabled = false;
            Result("Uploading…");
            var r = await app.UploadRecordAsync(it.Record);
            if (r.Ok && it.Record.PeergosPath != null) (remotePaths ??= []).Add(it.Record.PeergosPath);
            Result(r.Ok ? "Uploaded – the link is on the clipboard" : "Upload failed: " + r.Error);
            Rebuild();
        };
        DelLocalBtn.Click += (_, _) => AskDelete(local: true, remote: false);
        DelRemoteBtn.Click += (_, _) => AskDelete(local: false, remote: true);
        DelBothBtn.Click += (_, _) => AskDelete(local: true, remote: true);
        RemoveBtn.Click += (_, _) => AskRemove();
        ConfirmNo.Click += (_, _) => HideConfirm();
        ConfirmYes.Click += async (_, _) =>
        {
            var action = pending;
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

        foreach (var r in Store.Records)
        {
            if (!items.TryGetValue(r.Id, out var it)) items[r.Id] = it = new HistoryItem(r);
            it.Update(r.File != null && LocalExists(r.File), HistoryLogic.InPeergos(r, remotePaths));
        }
        foreach (var gone in items.Keys.Except(Store.Records.Select(r => r.Id)).ToList()) items.Remove(gone);

        var sort = TagOf(SortBox);
        var shown = HistoryLogic.Sort(HistoryLogic.Filter(Store.Records, TagOf(ShowBox), SearchBox.Text, LocalExists, remotePaths), sort)
            .Select(r => items[r.Id]).ToList();
        var view = new ListCollectionView(shown);
        if (sort is "newest" or "oldest" or "")
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryItem.Day)));
        List.ItemsSource = view;
        foreach (var it in shown.Where(i => keep.Contains(i.Record.Id))) List.SelectedItems.Add(it);

        Empty.Visibility = Store.Records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int onPc = items.Values.Count(i => i.Local), inPeergos = items.Values.Count(i => i.InPeergos == true);
        int missing = items.Values.Count(i => !i.Local);
        Counts.Text = $"{Store.Records.Count} captures · {onPc} on this PC · " +
                      (remotePaths == null ? "Peergos not checked" : $"{inPeergos} in Peergos") +
                      (missing > 0 ? $" · {missing} not on this PC" : "") +
                      (shown.Count != Store.Records.Count ? $" · showing {shown.Count}" : "");
        ShowDetails();
    }

    /// <summary>Looks at the captures folder and the Peergos folder again (links of older uploads are read back).</summary>
    async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        RefreshBtn.IsEnabled = false;
        try
        {
            app.DiscoverLocalCaptures();
            if (!app.Settings.PeergosConfigured)
            {
                remotePaths = null;
                RemoteState.Text = "Not signed in to Peergos – files there are not shown";
                Rebuild();
                return;
            }
            RemoteState.Text = "Looking at your Peergos folder…";
            var (r, files) = await Uploader.ListAsync(app.Settings.Clone());
            if (!r.Ok)
            {
                RemoteState.Text = "Peergos: " + r.Error;
                Rebuild();
                return;
            }
            remotePaths = files.Select(f => f.Path).ToHashSet();
            var added = HistoryLogic.Discover(Store.Records, CaptureFiles.Scan(AppPaths.CacheDir), files, File.GetLastWriteTime, Store.Dismissed);
            if (added.Count > 0) Store.AddRange(added);
            else Store.Save(); // links found for known records
            RemoteState.Text = $"Peergos: {files.Count} files in {r.PeergosPath}, checked {DateTime.Now:HH:mm}";
            Rebuild();
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
        }
    }

    // ---------- details ----------

    void ShowDetails()
    {
        showingDetails = false;
        var sel = Selected();
        HideConfirm();
        NothingSelected.Visibility = sel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = sel.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (sel.Count == 0) return;
        bool signedIn = app.Settings.PeergosConfigured;
        DelLocalBtn.IsEnabled = sel.Any(i => i.Local);
        DelRemoteBtn.IsEnabled = signedIn && sel.Any(i => i.Record.PeergosPath != null && i.InPeergos != false);
        DelBothBtn.IsEnabled = DelLocalBtn.IsEnabled && DelRemoteBtn.IsEnabled;
        if (sel.Count > 1)
        {
            SinglePanel.Visibility = Visibility.Collapsed;
            PreviewBox.Visibility = Visibility.Collapsed;
            SelectionTitle.Text = $"{sel.Count} captures selected";
            return;
        }
        var it = sel[0];
        var r = it.Record;
        SinglePanel.Visibility = Visibility.Visible;
        PreviewBox.Visibility = Visibility.Visible;
        SelectionTitle.Text = it.Title;
        LabelBox.Text = r.Label;
        InfoTaken.Text = r.Created.ToString("dddd d MMMM yyyy, HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        InfoApp.Text = string.IsNullOrEmpty(r.App) ? "–" : r.App + (string.IsNullOrEmpty(r.WindowTitle) ? "" : " – " + r.WindowTitle);
        var size = new List<string>();
        if (r.Width > 0) size.Add($"{r.Width} × {r.Height}");
        if (r.IsVideo && r.Seconds > 0) size.Add(it.Duration);
        if (r.Bytes > 0) size.Add(HistoryLogic.Size(r.Bytes));
        InfoSize.Text = size.Count > 0 ? string.Join(" · ", size) : "–";
        InfoLocal.Text = it.Local ? r.File : r.File != null ? "No longer on this PC" : "Not on this PC";
        InfoRemote.Text = it.PeergosTip;
        InfoLink.Text = r.Link ?? "–";
        CopyLinkBtn.IsEnabled = OpenLinkBtn.IsEnabled = r.Link != null && it.InPeergos != false;
        OpenFileBtn.IsEnabled = ShowFileBtn.IsEnabled = CopyMediaBtn.IsEnabled = it.Local;
        CopyMediaBtn.Content = r.IsVideo ? "Copy video" : "Copy picture";
        UploadBtn.IsEnabled = it.Local && signedIn && it.InPeergos != true;
        UploadBtn.Visibility = it.InPeergos == true ? Visibility.Collapsed : Visibility.Visible;
        _ = ShowPreview(it);
        showingDetails = true;
    }

    async Task ShowPreview(HistoryItem it)
    {
        Preview.Source = null;
        var r = it.Record;
        PreviewNote.Text = !it.Local ? "No preview: the file is not on this PC." + (r.Link != null ? " Open the link to see it." : "") : "";
        if (!it.Local) return;
        string? source = r.IsVideo ? await ThumbFiles.VideoThumb(r) : r.File;
        if (source == null) { PreviewNote.Text = "No preview"; return; }
        var img = await Task.Run(() => ThumbFiles.Load(source, 900));
        if (One() == it) Preview.Source = img;
    }

    // ---------- actions ----------

    void Confirm(string text, string yes, Func<Task> action)
    {
        pending = action;
        ConfirmText.Text = text;
        ConfirmYes.Content = yes;
        ConfirmBar.Visibility = Visibility.Visible;
        ConfirmBar.BringIntoView();
    }

    void HideConfirm()
    {
        pending = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    void Result(string text) => ActionResult.Text = text;

    void AskDelete(bool local, bool remote)
    {
        var sel = Selected();
        var localItems = local ? sel.Where(i => i.Local).ToList() : [];
        var remoteItems = remote ? sel.Where(i => i.Record.PeergosPath != null && i.InPeergos != false).ToList() : [];
        if (localItems.Count + remoteItems.Count == 0) return;
        var parts = new List<string>();
        if (localItems.Count > 0) parts.Add($"{localItems.Count} file{(localItems.Count == 1 ? "" : "s")} on this PC go{(localItems.Count == 1 ? "es" : "")} to the Recycle Bin");
        if (remoteItems.Count > 0) parts.Add($"{remoteItems.Count} file{(remoteItems.Count == 1 ? "" : "s")} will be deleted from your Peergos folder – their links stop working, and this cannot be undone");
        var mirrorNote = localItems.Any(i => i.Record.MirrorFile != null) ? " Copies in your mirror folder are kept." : "";
        Confirm(string.Join("; ", parts) + "." + mirrorNote + " The history keeps the entries.", "Yes, delete", async () =>
        {
            var messages = new List<string>();
            if (localItems.Count > 0)
            {
                var failed = Recycle.Delete(localItems.Select(i => i.Record.File!));
                messages.Add(failed.Count == 0 ? $"{localItems.Count} moved to the Recycle Bin" : $"{failed.Count} could not be moved to the Recycle Bin");
            }
            if (remoteItems.Count > 0)
            {
                Result("Deleting in Peergos…");
                var (r, deleted, missing, failed) = await Uploader.DeleteAsync(app.Settings.Clone(), remoteItems.Select(i => i.Record.PeergosPath!));
                foreach (var it in remoteItems.Where(i => deleted.Contains(i.Record.PeergosPath!) || missing.Contains(i.Record.PeergosPath!)))
                {
                    remotePaths?.Remove(it.Record.PeergosPath!);
                    it.Record.PeergosPath = null;
                    it.Record.Link = null;
                }
                Store.Save();
                messages.Add(r.Raw == null && !r.Ok ? "Peergos: " + r.Error
                    : $"{deleted.Count + missing.Count} deleted from Peergos" + (failed.Count > 0 ? $", {failed.Count} failed: {failed[0]}" : ""));
            }
            Result(string.Join(" · ", messages));
            Rebuild();
        });
    }

    void AskRemove()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        Confirm($"Remove {sel.Count} entr{(sel.Count == 1 ? "y" : "ies")} from the history? The files stay where they are (this PC, Peergos) and are not added back later.",
            "Yes, remove", () =>
            {
                Store.Remove(sel.Select(i => i.Record.Id));
                Result($"{sel.Count} removed from the history");
                return Task.CompletedTask;
            });
    }

    void AskRemoveGone()
    {
        var gone = HistoryLogic.Gone(Store.Records, File.Exists, remotePaths);
        if (gone.Count == 0)
        {
            Result(remotePaths == null && app.Settings.PeergosConfigured
                ? "No entries without files found (Peergos is not checked yet – click Refresh first)."
                : "Every entry still leads to a file.");
            SelectFirstIfNone();
            return;
        }
        SelectFirstIfNone();
        Confirm($"Remove {gone.Count} entr{(gone.Count == 1 ? "y" : "ies")} whose files are neither on this PC nor in Peergos?", "Yes, remove", () =>
        {
            Store.Remove(gone.Select(g => g.Id));
            Result($"{gone.Count} entries removed");
            return Task.CompletedTask;
        });
    }

    void AskClear()
    {
        if (Store.Records.Count == 0) return;
        SelectFirstIfNone();
        Confirm($"Clear the whole history ({Store.Records.Count} entries)? No file is deleted – only the list is emptied.", "Yes, clear", () =>
        {
            Store.Clear();
            Result("The history is empty");
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
        if (it.Local && it.Record.File != null) Shell(it.Record.File);
        else if (it.Record.Link != null) Shell(it.Record.Link);
    }

    void CopyText(string text, string done)
    {
        try { ClipboardService.SetText(text); Result(done); }
        catch (Exception e) { Result("Could not copy: " + e.Message); }
    }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
