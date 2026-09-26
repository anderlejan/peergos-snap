using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>
/// Full-desktop selection layer that does NOT freeze the screen: the window is (almost) fully transparent, so
/// games, 3D worlds and video keep moving underneath while the user drags. Only a border, an optional dim
/// outside the selection and a size label are drawn. All coordinates are physical screen pixels.
/// </summary>
public sealed class SelectionOverlay : Window
{
    readonly Settings s;
    readonly CaptureKind kind;
    readonly TaskCompletionSource<PxRect?> result = new();
    readonly Canvas canvas = new();
    readonly Rectangle border = new() { StrokeThickness = 1.5, SnapsToDevicePixels = true };
    readonly Rectangle hover = new() { StrokeThickness = 2, StrokeDashArray = [4, 3], SnapsToDevicePixels = true };
    readonly System.Windows.Shapes.Path dim = new();
    readonly Border label = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)) };
    readonly TextBlock labelText = new() { Foreground = Brushes.White, FontSize = 12 };
    readonly Border hint = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6), Background = new SolidColorBrush(Color.FromArgb(210, 20, 20, 20)) };
    readonly PxRect screen = Native.VirtualScreen();
    int startX, startY;
    bool dragging;
    PxRect current;
    PxRect? hoverRect;
    double scale = 1;

    public SelectionOverlay(Settings s, CaptureKind kind)
    {
        this.s = s;
        this.kind = kind;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Title = "Peergos Snap selection";
        // Alpha 1 keeps the window hit-testable while looking fully transparent.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        var color = ParseColor(s.BorderColor);
        border.Stroke = new SolidColorBrush(color);
        hover.Stroke = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B));
        dim.Fill = new SolidColorBrush(Color.FromArgb((byte)(s.OverlayDimPercent * 255 / 100), 0, 0, 0));
        label.Child = labelText;
        hint.Child = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 13,
            Text = (kind == CaptureKind.Video ? "Record video: " : "Picture: ") +
                   "drag an area" + (s.PickWindowOnClick ? " or click a window" : "") + " · Esc cancels",
        };
        canvas.Children.Add(dim);
        canvas.Children.Add(hover);
        canvas.Children.Add(border);
        canvas.Children.Add(label);
        canvas.Children.Add(hint);
        border.Visibility = label.Visibility = hover.Visibility = Visibility.Collapsed;
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, false, false);
            // Esc must cancel even when Windows refuses us the keyboard focus (e.g. started by a command):
            // make it a global hotkey for as long as the overlay is open.
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Native.WM_HOTKEY && w.ToInt32() == EscHotkeyId) { handled = true; Finish(null); }
                return IntPtr.Zero;
            });
            escRegistered = Native.RegisterHotKey(hwnd, EscHotkeyId, Hotkey.MOD_NOREPEAT, 0x1B);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, screen.X, screen.Y, screen.Width, screen.Height, Native.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) =>
        {
            scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, screen.X, screen.Y, screen.Width, screen.Height, Native.SWP_SHOWWINDOW);
            Activate();
            Focus();
            PlaceHint();
            UpdateDim();
        };
        DpiChanged += (_, e) => { scale = e.NewDpi.DpiScaleX; PlaceHint(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        MouseRightButtonDown += (_, _) => Finish(null);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        Closed += (_, _) =>
        {
            if (escRegistered) Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, EscHotkeyId);
        };
        Deactivated += (_, _) => { if (!dragging && !result.Task.IsCompleted) Activate(); };
    }

    const int EscHotkeyId = 0x7E5C;
    bool escRegistered;

    public Task<PxRect?> Result => result.Task;

    static Color ParseColor(string c)
    {
        try { return (Color)ColorConverter.ConvertFromString(c); }
        catch { return Color.FromRgb(255, 59, 48); }
    }

    static (int X, int Y) CursorPos() { Native.GetCursorPos(out var p); return (p.X, p.Y); }

    Rect ToDip(PxRect r) => new((r.X - screen.X) / scale, (r.Y - screen.Y) / scale, r.Width / scale, r.Height / scale);

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        (startX, startY) = CursorPos();
        dragging = true;
        current = new PxRect(startX, startY, 0, 0);
        CaptureMouse();
        hint.Visibility = Visibility.Collapsed;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var (x, y) = CursorPos();
        if (dragging)
        {
            current = PxRect.FromPoints(startX, startY, x, y);
            hover.Visibility = Visibility.Collapsed;
            Draw(current);
        }
        else if (s.PickWindowOnClick)
        {
            var w = Native.WindowAt(x, y);
            if (hoverRect != w)
            {
                hoverRect = w;
                var d = ToDip(w);
                Canvas.SetLeft(hover, d.X); Canvas.SetTop(hover, d.Y);
                hover.Width = Math.Max(0, d.Width); hover.Height = Math.Max(0, d.Height);
                hover.Visibility = Visibility.Visible;
            }
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        var (x, y) = CursorPos();
        current = PxRect.FromPoints(startX, startY, x, y);
        if (current.Width < 4 || current.Height < 4)
        {
            if (!s.PickWindowOnClick) { hint.Visibility = Visibility.Visible; return; }
            current = Native.WindowAt(x, y);
        }
        Finish(current.Intersect(screen));
    }

    void Draw(PxRect r)
    {
        var d = ToDip(r);
        Canvas.SetLeft(border, d.X - 1); Canvas.SetTop(border, d.Y - 1);
        border.Width = d.Width + 2; border.Height = d.Height + 2;
        border.Visibility = Visibility.Visible;
        if (s.ShowSizeLabel)
        {
            labelText.Text = $"{r.Width} × {r.Height}";
            label.Visibility = Visibility.Visible;
            double ly = d.Bottom + 6;
            if (ly + 24 > ActualHeight) ly = d.Y - 28;
            if (ly < 0) ly = d.Y + 6;
            Canvas.SetLeft(label, d.X); Canvas.SetTop(label, ly);
        }
        UpdateDim(d);
    }

    void UpdateDim(Rect? hole = null)
    {
        if (s.OverlayDimPercent <= 0) { dim.Data = null; return; }
        var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        dim.Data = hole is { } h ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(h)) : all;
    }

    void PlaceHint()
    {
        var (x, y) = CursorPos();
        var mon = Native.MonitorAt(x, y);
        var d = ToDip(mon);
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(hint, d.X + (d.Width - hint.DesiredSize.Width) / 2);
        Canvas.SetTop(hint, d.Y + 24);
    }

    void Finish(PxRect? r)
    {
        if (result.Task.IsCompleted) return;
        // Hide at once so the capture sees the live screen, not us.
        Hide();
        result.TrySetResult(r is { IsEmpty: false } ? r : null);
        Close();
    }
}
