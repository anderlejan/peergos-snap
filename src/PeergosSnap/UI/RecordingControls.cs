using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>Small floating Stop / Pause / Cancel bar next to the recorded region. Never recorded itself.</summary>
public sealed class RecordingControls : Window
{
    readonly TextBlock time = new() { Foreground = new SolidColorBrush(Theme.C(Theme.Current.Fg)), FontSize = Theme.FontSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 48 };
    readonly Ellipse dot = new() { Width = 10, Height = 10, Fill = Brushes.Red, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly Button pause;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly PxRect region;
    readonly Func<TimeSpan> elapsed;

    public event Action? StopClicked, PauseClicked, CancelClicked;

    public RecordingControls(PxRect region, Func<TimeSpan> elapsed)
    {
        this.region = region;
        this.elapsed = elapsed;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Peergos Snap recording";

        Button B(string text, string tip, Action a)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(2), Focusable = false, FontSize = Theme.FontSize };
            b.Click += (_, _) => a();
            return b;
        }
        pause = B("⏸ Pause", "Pause / resume the recording", () => PauseClicked?.Invoke());
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(dot);
        panel.Children.Add(time);
        panel.Children.Add(B("■ Stop", "Stop and choose what to do with the video", () => StopClicked?.Invoke()));
        panel.Children.Add(pause);
        panel.Children.Add(B("✕ Cancel", "Throw the recording away", () => CancelClicked?.Invoke()));
        Content = new Border
        {
            Background = new SolidColorBrush(Theme.C(Theme.Current.Bg2)) { Opacity = 0.96 },
            BorderBrush = new SolidColorBrush(Theme.C(Theme.Current.Line)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Child = panel,
        };

        timer.Tick += (_, _) => time.Text = elapsed().ToString(elapsed().TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, false, true);
            Native.ExcludeFromCapture(hwnd);
        };
        Loaded += (_, _) => { Place(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    public void SetPaused(bool paused)
    {
        pause.Content = paused ? "▶ Resume" : "⏸ Pause";
        dot.Fill = paused ? Brushes.Orange : Brushes.Red;
    }

    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        double sc = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * sc), h = (int)Math.Ceiling(ActualHeight * sc);
        var mon = Native.MonitorAt(region.X + region.Width / 2, region.Y + region.Height / 2, work: true);
        var (x, y, _) = Core.Geometry.PlaceControls(region, mon, w, h);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }
}

/// <summary>A thin click-through frame just outside the recorded region, excluded from capture.</summary>
public sealed class RegionFrame : Window
{
    const int Thick = 2;

    public RegionFrame(PxRect region, string color)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(color); } catch { c = Colors.Red; }
        var rect = new Rectangle { Stroke = new SolidColorBrush(c), StrokeDashArray = [6, 4], SnapsToDevicePixels = true };
        Content = rect;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, true, true);
            Native.ExcludeFromCapture(hwnd);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, region.X - Thick, region.Y - Thick,
                region.Width + 2 * Thick, region.Height + 2 * Thick, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) =>
        {
            double sc = VisualTreeHelper.GetDpi(this).DpiScaleX;
            rect.StrokeThickness = Thick / sc;
        };
    }
}
