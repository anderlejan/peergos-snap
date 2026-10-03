using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>
/// Full-screen preview of the history's pictures. The mouse wheel zooms where the pointer is; when the picture is
/// larger than the screen the view follows the mouse (no dragging). A click zooms in or back to the whole picture.
/// ← → go to the previous / next capture of the list, Esc or right click closes.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    readonly IReadOnlyList<HistoryItem> items;
    readonly Action<HistoryItem>? onDraw;
    int index;
    readonly Canvas canvas = new() { Background = Brushes.Black, ClipToBounds = true };
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly ScaleTransform scale = new();
    readonly TranslateTransform move = new();
    readonly TextBlock caption = new() { Foreground = Brushes.White, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock note = new() { Foreground = Brushes.Gainsboro, FontSize = 16, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520 };
    readonly Border bar;
    BitmapSource? picture;
    double fit = 1;
    Point mouse;

    public ImageViewerWindow(IReadOnlyList<HistoryItem> items, int index, Action<HistoryItem>? onDraw)
    {
        this.items = items;
        this.index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        this.onDraw = onDraw;
        Title = "Peergos Snap – view";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; // full screen on the monitor of the history window
        WindowState = WindowState.Maximized;
        Background = Brushes.Black;
        ShowInTaskbar = true;

        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.RenderTransform = new TransformGroup { Children = [scale, move] };
        canvas.Children.Add(image);

        var close = new Button { Content = "✕", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), Focusable = false, ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Close();
        var draw = new Button { Content = "✎ Draw on it", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0), Focusable = false, ToolTip = "Draw on a copy (D)" };
        draw.Click += (_, _) => Draw();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (onDraw != null) right.Children.Add(draw);
        right.Children.Add(close);
        var dock = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(caption);
        bar = new Border { Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), Padding = new Thickness(14, 8, 14, 8), Child = dock, VerticalAlignment = VerticalAlignment.Bottom };

        var root = new Grid();
        root.Children.Add(canvas);
        root.Children.Add(new Border { Child = note, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        root.Children.Add(bar);
        Content = root;

        canvas.MouseMove += (_, e) => { mouse = e.GetPosition(canvas); Place(); };
        canvas.MouseWheel += (_, e) => { mouse = e.GetPosition(canvas); Zoom(scale.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2)); };
        canvas.MouseLeftButtonUp += (_, e) => { mouse = e.GetPosition(canvas); Zoom(scale.ScaleX > fit * 1.01 ? fit : ViewerLogic.ClickZoom(fit)); };
        canvas.MouseRightButtonUp += (_, _) => Close();
        canvas.SizeChanged += (_, _) => { bool fitted = Math.Abs(scale.ScaleX - fit) < 0.001; fit = Fit(); if (fitted) Zoom(fit); else Place(); };
        PreviewKeyDown += Keys;
        Loaded += (_, _) => Show(this.index);
    }

    void Keys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left or Key.PageUp: Show(index - 1); break;
            case Key.Right or Key.PageDown or Key.Space: Show(index + 1); break;
            case Key.Home: Show(0); break;
            case Key.End: Show(items.Count - 1); break;
            case Key.OemPlus or Key.Add: CenterMouse(); Zoom(scale.ScaleX * 1.25); break;
            case Key.OemMinus or Key.Subtract: CenterMouse(); Zoom(scale.ScaleX / 1.25); break;
            case Key.D0 or Key.NumPad0: CenterMouse(); Zoom(1); break;
            case Key.F: Zoom(fit); break;
            case Key.D: Draw(); break;
            case Key.Enter: if (Current?.PreviewFile is { } f) Open(f); break;
            default: return;
        }
        e.Handled = true;
    }

    HistoryItem? Current => index >= 0 && index < items.Count ? items[index] : null;

    void CenterMouse() => mouse = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);

    void Draw()
    {
        if (onDraw == null || Current is not { IsViewablePicture: true, Local: true } it) return;
        Close();
        onDraw(it);
    }

    async void Show(int i)
    {
        if (items.Count == 0) return;
        index = Math.Clamp(i, 0, items.Count - 1);
        var it = items[index];
        caption.Text = $"{index + 1} / {items.Count} · {it.Title} · {it.Line2}    ← → next · wheel zooms · click zooms in / out · Esc closes";
        picture = null;
        image.Source = null;
        note.Text = "";
        string? file = it.PreviewFile;
        if (it.Record.IsVideo && it.Local) file = await ThumbFiles.VideoThumb(it.Record);
        if (file == null)
        {
            note.Text = it.Record.IsUpload ? $"{it.Title}\nNo preview: {(it.Record.IsFolder ? "a folder" : "not a picture, or the original is gone")}."
                : "No preview: the file is not on this PC." + (it.Record.Link != null ? " Open its link instead." : "");
            return;
        }
        var bmp = await Task.Run(() => ThumbFiles.Load(file, 0));
        if (Current != it) return;
        if (bmp == null) { note.Text = "This picture cannot be shown."; return; }
        if (it.Record.IsVideo) note.Text = "Video – Enter plays it in your video player";
        picture = bmp;
        image.Source = bmp;
        image.Width = bmp.PixelWidth;
        image.Height = bmp.PixelHeight;
        fit = Fit();
        CenterMouse();
        Zoom(fit);
    }

    double Fit() => picture == null ? 1 : ViewerLogic.Fit(canvas.ActualWidth, canvas.ActualHeight, picture.PixelWidth, picture.PixelHeight);

    void Zoom(double s)
    {
        if (picture == null) return;
        scale.ScaleX = scale.ScaleY = Math.Clamp(s, Math.Min(fit, 0.05), 16);
        Place();
    }

    /// <summary>The view follows the mouse (see <see cref="ViewerLogic.Offset"/>).</summary>
    void Place()
    {
        if (picture == null) return;
        move.X = ViewerLogic.Offset(mouse.X, canvas.ActualWidth, picture.PixelWidth * scale.ScaleX);
        move.Y = ViewerLogic.Offset(mouse.Y, canvas.ActualHeight, picture.PixelHeight * scale.ScaleY);
    }

    static void Open(string f)
    {
        try { Process.Start(new ProcessStartInfo(f) { UseShellExecute = true }); } catch { }
    }
}
