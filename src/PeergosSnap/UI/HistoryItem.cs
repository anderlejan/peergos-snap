using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>One row of the History window.</summary>
public sealed class HistoryItem : INotifyPropertyChanged, IViewable
{
    public HistoryRecord Record { get; }
    public bool Local { get; private set; }
    /// <summary>In the Peergos folder? null = not known (not signed in / not checked).</summary>
    public bool? InPeergos { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryItem(HistoryRecord r) => Record = r;

    /// <summary>The small picture Peergos keeps with the file (Settings → Files & history → previews from Peergos):
    /// shown while the file is not on this PC.</summary>
    public string? RemoteThumb { get; private set; }
    ImageSource? remoteImage;
    string? remoteImageOf;

    public void Update(bool local, bool? inPeergos, string? remoteThumb = null)
    {
        Local = local;
        InPeergos = inPeergos;
        RemoteThumb = remoteThumb;
        // A picture drawn on in place keeps its name: a new size means new content, so the thumbnail is made again.
        if (Record.Bytes != thumbBytes)
        {
            thumbBytes = Record.Bytes;
            if (thumb != null) { thumb = null; loading = false; }
        }
        Changed(nameof(Title), nameof(Line2), nameof(Day), nameof(PcBrush), nameof(PcText), nameof(PcTip),
            nameof(PeergosBrush), nameof(PeergosText), nameof(PeergosTip), nameof(PeergosLabel), nameof(LinkBadge), nameof(LockBadge),
            nameof(Thumb), nameof(Glyph));
    }

    void Changed(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public string Title => Record.Title;
    public string Day => HistoryLogic.DayLabel(Record.Created, DateTime.Today);

    public string Line2
    {
        get
        {
            var parts = new List<string> { Record.Created.ToString("HH:mm") };
            if (!string.IsNullOrEmpty(Record.App)) parts.Add(Record.App!);
            if (Record.Width > 0) parts.Add($"{Record.Width} × {Record.Height}");
            var size = HistoryLogic.Size(Record.Bytes);
            if (size.Length > 0) parts.Add(size);
            if (Record.Label.Trim().Length > 0) parts.Add(Record.Name);
            return string.Join(" · ", parts);
        }
    }

    public string Glyph => Record.IsFolder ? "\uE8B7" : Record.Kind == "file" ? "\uE8A5" : Record.IsVideo ? "\uE714" : Local ? "\uEB9F" : "\uE7BA";
    public Visibility LockBadge => Record.Locked ? Visibility.Visible : Visibility.Collapsed;

    static readonly string[] PictureTypes = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"];

    /// <summary>The file a preview is made from: the capture on this PC, or for an uploaded picture its original
    /// (only looked at, never changed).</summary>
    public string? PreviewFile =>
        Local && Record.File != null ? Record.File
        : Record is { Kind: "file", Source: { } src } && PictureTypes.Contains(Path.GetExtension(src).ToLowerInvariant()) && File.Exists(src) ? src
        : null;

    /// <summary>A picture that can be shown full screen or drawn on.</summary>
    public bool IsViewablePicture => PreviewFile is { } f && PictureTypes.Contains(Path.GetExtension(f).ToLowerInvariant());
    public Visibility VideoBadge => Record.IsVideo && Record.Seconds > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------- the full-screen viewer ----------
    public string ViewTitle => Title;
    public string ViewLine => Line2;
    public bool IsVideo => Record.IsVideo;
    public async Task<string?> PictureAsync() => Record.IsVideo ? (Local ? await ThumbFiles.VideoThumb(Record) : null) : PreviewFile;
    public string NoPreview => Record.IsUpload
        ? $"{Title}\nNo preview: {(Record.IsFolder ? "a folder" : "not a picture, or the original is gone")}."
        : "No preview: the file is not on this PC." + (Record.Link != null ? " Open its link instead." : "");
    public string? OpenFile => PreviewFile;
    public bool CanDraw => IsViewablePicture && Local && !Record.IsVideo;
    public string Duration => TimeSpan.FromSeconds(Record.Seconds).ToString(Record.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public Visibility LinkBadge => !string.IsNullOrEmpty(Record.Link) && InPeergos != false ? Visibility.Visible : Visibility.Collapsed;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public Brush PcBrush => Local ? Res("Acc2") : Res("Bg3");
    public Brush PcText => Local ? Res("OnAcc") : Res("Fg3");
    public string PcTip => Local ? "The file is on this PC: " + Record.File
        : Record.IsUpload ? "Uploaded from " + Record.Source + " (Peergos Snap keeps no copy of it)" : "Not on this PC";
    public Brush PeergosBrush => InPeergos == true ? Res("Acc2") : Res("Bg3");
    public Brush PeergosText => InPeergos == true ? Res("OnAcc") : Res("Fg3");
    /// <summary>"Peergos ?" while it is not known (not signed in, or not looked yet).</summary>
    public string PeergosLabel => InPeergos == null && Record.PeergosPath != null ? "Peergos ?" : "Peergos";
    public string PeergosTip => InPeergos switch
    {
        true => "In your Peergos folder: " + Record.PeergosPath,
        null when Record.PeergosPath != null => "Uploaded to " + Record.PeergosPath + " (not checked: sign in to Peergos, or Refresh)",
        _ when Record.Uploaded != null => "No longer in Peergos",
        _ => "Not uploaded",
    };

    // ---------- thumbnails ----------

    ImageSource? thumb;
    bool loading;
    long thumbBytes = -1;

    /// <summary>The file's own picture (or a video's still made earlier); without the file on this PC the small picture
    /// from Peergos – in memory only.</summary>
    public ImageSource? Thumb
    {
        get
        {
            if (thumb == null && !loading && (PreviewFile != null || Record.IsVideo)) { loading = true; _ = LoadThumb(); }
            if (thumb != null || RemoteThumb == null) return thumb;
            if (remoteImageOf != RemoteThumb)
            {
                remoteImage = ThumbFiles.FromBytes(DirectLogic.ThumbBytes(RemoteThumb), 200);
                remoteImageOf = RemoteThumb;
            }
            return remoteImage;
        }
    }

    /// <summary>The small picture from Peergos at its own size, for the details (null without one).</summary>
    public ImageSource? RemotePreview => RemoteThumb == null ? null : ThumbFiles.FromBytes(DirectLogic.ThumbBytes(RemoteThumb), 0);

    async Task LoadThumb()
    {
        try
        {
            string? source = PreviewFile;
            if (Record.IsVideo) source = await ThumbFiles.VideoThumb(Record);
            if (source == null) return;
            var img = await Task.Run(() => ThumbFiles.Load(source, 200));
            thumb = img;
            Changed(nameof(Thumb));
        }
        catch (Exception e) { Log.Error("thumbnail " + Record.File, e); }
    }
}

/// <summary>Loading pictures without locking the files, and first-frame pictures for videos.</summary>
public static class ThumbFiles
{
    static readonly SemaphoreSlim Ffmpeg = new(1, 1);

    public static BitmapImage? Load(string file, int width)
    {
        if (!File.Exists(file)) return null;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        // IgnoreImageCache: a capture drawn on keeps its name, and WPF would otherwise show the old picture.
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
        if (width > 0) bi.DecodePixelWidth = width;
        bi.UriSource = new Uri(file);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>A picture from bytes in memory (nothing on disk), or null when they are not a picture.</summary>
    public static BitmapImage? FromBytes(byte[]? data, int width)
    {
        if (data == null || data.Length == 0) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width > 0) bi.DecodePixelWidth = width;
            bi.StreamSource = new MemoryStream(data);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The small picture Peergos keeps with a file (since 2.6): a JPEG of a picture, or of a video's still, at most
    /// 256 pixels on its longer side, as "data:image/jpeg;base64,…". It lets the friend's app – and Peergos in the
    /// browser – show the file without downloading it. Null when none can be made.
    /// </summary>
    public static async Task<string?> DataUrlAsync(string file)
    {
        try
        {
            byte[]? bytes = FileKinds.Of(file) switch
            {
                FileKind.Picture => await File.ReadAllBytesAsync(file),
                FileKind.Video => await StillAsync(file),
                _ => null,
            };
            if (bytes == null) return null;
            return await Task.Run(() => JpegDataUrl(bytes, 256));
        }
        catch (Exception e)
        {
            Log.Error("thumbnail " + file, e);
            return null;
        }
    }

    /// <summary>A video's still as bytes, through a temporary file that is removed at once (nothing is kept).</summary>
    static async Task<byte[]?> StillAsync(string video)
    {
        var dir = Path.Combine(AppPaths.WorkDir, "stills");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jpg");
        try { return await MakeVideoThumb(video, tmp) is { } made ? await File.ReadAllBytesAsync(made) : null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static string? JpegDataUrl(byte[] data, int longest)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth == 0 || frame.PixelHeight == 0) return null;
        double scale = Math.Min(1.0, (double)longest / Math.Max(frame.PixelWidth, frame.PixelHeight));
        int w = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
        // On white, so that a transparent picture does not turn black in the JPEG.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, w, h));
            dc.DrawImage(frame, new System.Windows.Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new JpegBitmapEncoder { QualityLevel = 72 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>A small picture of a video (made once with the bundled FFmpeg, kept with the app's data).</summary>
    public static async Task<string?> VideoThumb(HistoryRecord r)
    {
        var thumb = Path.Combine(AppPaths.ThumbsDir, r.Id + ".jpg");
        if (File.Exists(thumb)) return thumb;
        if (r.File == null || !File.Exists(r.File)) return null;
        return await MakeVideoThumb(r.File, thumb);
    }

    /// <summary>A small picture of any video file (the direct window's), kept by its path, size and time.</summary>
    public static async Task<string?> VideoThumbOf(string file)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists) return null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{fi.FullName.ToLowerInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")))[..20];
        var thumb = Path.Combine(AppPaths.ThumbsDir, "v-" + key + ".jpg");
        return File.Exists(thumb) ? thumb : await MakeVideoThumb(file, thumb);
    }

    static async Task<string?> MakeVideoThumb(string file, string thumb)
    {
        if (!File.Exists(AppPaths.FfmpegExe)) return null;
        await Ffmpeg.WaitAsync();
        try
        {
            Directory.CreateDirectory(AppPaths.ThumbsDir);
            // Half a second in (past a black first frame); a shorter video gets its first frame.
            foreach (var at in new[] { "0.5", "0" })
            {
                var psi = new ProcessStartInfo(AppPaths.FfmpegExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", at, "-i", file, "-frames:v", "1", "-vf", "scale=400:-2", thumb })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (File.Exists(thumb)) return thumb;
            }
            return null;
        }
        finally { Ffmpeg.Release(); }
    }

    static readonly Dictionary<string, VideoFacts> Probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Length and size of a video (asked FFmpeg once per file).</summary>
    public static async Task<VideoFacts> ProbeVideo(string file)
    {
        var key = file + "|" + (File.Exists(file) ? new FileInfo(file).Length : 0);
        lock (Probed) if (Probed.TryGetValue(key, out var known)) return known;
        var facts = new VideoFacts(null, 0, 0);
        if (File.Exists(file) && File.Exists(AppPaths.FfmpegExe))
        {
            var psi = new ProcessStartInfo(AppPaths.FfmpegExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-i", file }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var text = await p.StandardError.ReadToEndAsync(); // without an output FFmpeg only describes the file
            await p.WaitForExitAsync();
            facts = VideoFacts.Parse(text);
        }
        lock (Probed) Probed[key] = facts;
        return facts;
    }
}
