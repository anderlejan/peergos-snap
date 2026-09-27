using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>
/// The delay countdown, bottom right of the screen. It never takes the keyboard focus (so a menu the user opens
/// stays open) and is never part of a picture or video.
/// </summary>
public sealed class CountdownWindow : Window
{
    readonly TextBlock number = new() { FontSize = 34, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) };
    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 250 };
    public event Action? CancelClicked;

    public CountdownWindow(string what)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Peergos Snap countdown";
        var sc = Theme.Current;
        number.Foreground = new SolidColorBrush(Theme.C(sc.Acc));
        text.Foreground = new SolidColorBrush(Theme.C(sc.Fg));
        text.Text = what;
        var cancel = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Cursor = Cursors.Hand };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Cancel"))
        {
            Foreground = new SolidColorBrush(Theme.C(sc.Acc)),
        };
        link.Click += (_, _) => CancelClicked?.Invoke();
        cancel.Inlines.Add(link);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(text);
        right.Children.Add(cancel);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 10, 16, 10) };
        row.Children.Add(number);
        row.Children.Add(right);
        Content = new Border
        {
            Background = new SolidColorBrush(Theme.C(sc.Bg2)),
            BorderBrush = new SolidColorBrush(Theme.C(sc.Acc2)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Child = row,
        };
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = Theme.FontSize;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, false, true);
            if (Environment.GetEnvironmentVariable("PEERGOS_SNAP_CAPTURE_UI") != "1") Native.ExcludeFromCapture(hwnd);
        };
        SizeChanged += (_, _) => Place();
    }

    public void SetSeconds(int s) => number.Text = s.ToString();

    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetCursorPos(out var p);
        var work = Native.MonitorAt(p.X, p.Y, work: true);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale), m = (int)(16 * scale);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Right - w - m, work.Bottom - h - m, 0, 0,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);
    }
}
