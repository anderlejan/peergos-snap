using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

public enum ToastKind { Ok, Busy, Warn, Error }

/// <summary>
/// The app's own notification card, bottom right of the screen. Unlike Windows balloon tips it cannot be
/// swallowed by "Do not disturb" or notification settings, so every capture always reports what happened.
/// It never takes the focus and is never captured in pictures or videos.
/// </summary>
public sealed class ToastWindow : Window
{
    static ToastWindow? current;
    readonly Border bar = new() { Width = 5, CornerRadius = new CornerRadius(3, 0, 0, 3) };
    readonly Image thumb = new() { Width = 64, Height = 48, Stretch = Stretch.UniformToFill, Margin = new Thickness(10, 10, 0, 10), VerticalAlignment = VerticalAlignment.Top };
    readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    readonly ProgressBar progress = new() { Height = 4, Margin = new Thickness(0, 6, 0, 0), Minimum = 0, Maximum = 100 };
    readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    readonly DispatcherTimer hide = new();
    readonly Border card;

    ToastWindow()
    {
        Tag = "toast";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        Title = "Peergos Snap notification";

        var close = new Button { Content = "✕", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 6, 6, 0), VerticalAlignment = VerticalAlignment.Top, Focusable = false, ToolTip = "Close", BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        close.Click += (_, _) => Close();
        var textPanel = new StackPanel { Margin = new Thickness(10, 10, 0, 10) };
        textPanel.Children.Add(title);
        textPanel.Children.Add(text);
        textPanel.Children.Add(progress);
        textPanel.Children.Add(actions);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(thumb, 1); Grid.SetColumn(textPanel, 2); Grid.SetColumn(close, 3);
        grid.Children.Add(bar); grid.Children.Add(thumb); grid.Children.Add(textPanel); grid.Children.Add(close);
        card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid, Margin = new Thickness(0, 0, 0, 0) };
        Content = card;

        hide.Tick += (_, _) => { hide.Stop(); Close(); };
        MouseEnter += (_, _) => hide.Stop();
        MouseLeave += (_, _) => { if (hide.Interval > TimeSpan.Zero) hide.Start(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, false, true);
            // PEERGOS_SNAP_CAPTURE_UI=1 (testing only) keeps the card visible to screenshots.
            if (Environment.GetEnvironmentVariable("PEERGOS_SNAP_CAPTURE_UI") != "1") Native.ExcludeFromCapture(hwnd);
        };
        SizeChanged += (_, _) => Place();
        Closed += (_, _) => { if (current == this) current = null; };
        ApplyTheme();
        Theme.Changed += ApplyTheme;
        Closed += (_, _) => Theme.Changed -= ApplyTheme;
    }

    void ApplyTheme()
    {
        var sc = Theme.Current;
        card.Background = new SolidColorBrush(Theme.C(sc.Bg2));
        card.BorderBrush = new SolidColorBrush(Theme.C(sc.Line));
        Foreground = new SolidColorBrush(Theme.C(sc.Fg));
        text.Foreground = new SolidColorBrush(Theme.C(sc.Fg2));
        FontSize = Theme.FontSize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
    }

    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetCursorPos(out var p);
        var work = Native.MonitorAt(p.X, p.Y, work: true);
        double sc = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * sc), h = (int)Math.Ceiling(ActualHeight * sc);
        int m = (int)(12 * sc);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Right - w - m, work.Bottom - h - m, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Shows (or updates) the single notification card.</summary>
    public static ToastWindow Show(ToastKind kind, string heading, string body, string? link = null, string? file = null, int? percent = null,
        (string Label, Action Run)? extra = null, Action? discard = null)
    {
        var t = current ??= new ToastWindow();
        var sc = Theme.Current;
        t.bar.Background = new SolidColorBrush(kind switch
        {
            ToastKind.Error => Color.FromRgb(0xD9, 0x3B, 0x3B),
            ToastKind.Warn => Color.FromRgb(0xE0, 0x9A, 0x1F),
            _ => Theme.C(sc.Acc),
        });
        t.title.Text = heading;
        t.text.Text = body;
        t.progress.Visibility = kind == ToastKind.Busy ? Visibility.Visible : Visibility.Collapsed;
        t.progress.IsIndeterminate = percent == null;
        if (percent is { } pc) t.progress.Value = pc;
        t.thumb.Source = LoadThumb(file);
        t.thumb.Visibility = t.thumb.Source == null ? Visibility.Collapsed : Visibility.Visible;

        t.actions.Children.Clear();
        void Action(string label, Action a)
        {
            var b = new Button { Content = label, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
            b.Click += (_, _) => { a(); t.Close(); };
            t.actions.Children.Add(b);
        }
        if (extra is { } x) Action(x.Label, x.Run);
        if (link != null) Action("Open link", () => Open(link));
        // The file may be gone by the time the button is clicked (discarded, or not kept after the upload).
        if (file != null && File.Exists(file)) Action("Show file", () => { if (File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\""); });
        if (discard != null) Action("Discard", discard);
        t.actions.Visibility = t.actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        t.hide.Stop();
        t.hide.Interval = kind switch
        {
            ToastKind.Busy => TimeSpan.Zero,
            ToastKind.Ok => TimeSpan.FromSeconds(6),
            _ => TimeSpan.FromSeconds(14),
        };
        if (t.hide.Interval > TimeSpan.Zero) t.hide.Start();
        if (!t.IsVisible) t.Show();
        t.Place();
        return t;
    }

    static BitmapImage? LoadThumb(string? file)
    {
        if (file == null || !File.Exists(file)) return null;
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // the capture may have been drawn on since
            bi.DecodePixelWidth = 160;
            bi.UriSource = new Uri(file);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
