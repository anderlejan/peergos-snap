using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>One row of the History window.</summary>
public sealed class HistoryItem : INotifyPropertyChanged
{
    public HistoryRecord Record { get; }
    public bool Local { get; private set; }
    /// <summary>In the Peergos folder? null = not known (not signed in / not checked).</summary>
    public bool? InPeergos { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryItem(HistoryRecord r) => Record = r;

    public void Update(bool local, bool? inPeergos)
    {
        Local = local;
        InPeergos = inPeergos;
        Changed(nameof(Title), nameof(Line2), nameof(Day), nameof(PcBrush), nameof(PcText), nameof(PcTip),
            nameof(PeergosBrush), nameof(PeergosText), nameof(PeergosTip), nameof(LinkBadge), nameof(Thumb), nameof(Glyph));
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

    public string Glyph => Record.IsVideo ? "" : Local ? "" : "";
    public Visibility VideoBadge => Record.IsVideo && Record.Seconds > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Duration => TimeSpan.FromSeconds(Record.Seconds).ToString(Record.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public Visibility LinkBadge => !string.IsNullOrEmpty(Record.Link) && InPeergos != false ? Visibility.Visible : Visibility.Collapsed;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public Brush PcBrush => Local ? Res("Acc2") : Res("Bg3");
    public Brush PcText => Local ? Res("OnAcc") : Res("Fg3");
    public string PcTip => Local ? "The file is on this PC: " + Record.File : "Not on this PC";
    public Brush PeergosBrush => InPeergos == true ? Res("Acc2") : Res("Bg3");
    public Brush PeergosText => InPeergos == true ? Res("OnAcc") : Res("Fg3");
    public string PeergosTip => InPeergos switch
    {
        true => "In your Peergos folder: " + Record.PeergosPath,
        null when Record.PeergosPath != null => "Uploaded to " + Record.PeergosPath + " (not checked yet)",
        _ when Record.Uploaded != null => "No longer in Peergos",
        _ => "Not uploaded",
    };

    // ---------- thumbnails ----------

    ImageSource? thumb;
    bool loading;

    public ImageSource? Thumb
    {
        get
        {
            if (thumb == null && !loading && Local) { loading = true; _ = LoadThumb(); }
            return thumb;
        }
    }

    async Task LoadThumb()
    {
        try
        {
            var file = Record.File!;
            string? source = file;
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
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > 0) bi.DecodePixelWidth = width;
        bi.UriSource = new Uri(file);
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>A small picture of a video (made once with the bundled FFmpeg, kept with the app's data).</summary>
    public static async Task<string?> VideoThumb(HistoryRecord r)
    {
        var thumb = Path.Combine(AppPaths.ThumbsDir, r.Id + ".jpg");
        if (File.Exists(thumb)) return thumb;
        if (r.File == null || !File.Exists(r.File) || !File.Exists(AppPaths.FfmpegExe)) return null;
        await Ffmpeg.WaitAsync();
        try
        {
            Directory.CreateDirectory(AppPaths.ThumbsDir);
            var psi = new ProcessStartInfo(AppPaths.FfmpegExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", "0.5", "-i", r.File, "-frames:v", "1", "-vf", "scale=400:-2", thumb })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return File.Exists(thumb) ? thumb : null;
        }
        finally { Ffmpeg.Release(); }
    }
}
