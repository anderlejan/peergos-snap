using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PeergosSnap.Core;
using WPath = System.Windows.Shapes.Path;

namespace PeergosSnap.UI;

/// <summary>Where the editor was opened: on a fresh capture (the drawing goes into it), on a capture from the
/// history (the drawing becomes a new copy, the original stays), or on a picture of the direct window (a new copy
/// that can go to the friend).</summary>
public enum AnnotateMode { Capture, Copy, Send }

public enum AnnotateOutcome { Cancelled, Saved, SavedUpload, SavedCopy, SavedSend }

public enum AnnotateTool { Select, Arrow, Line, Box, Ellipse, Highlight, Pen, Text, Number, Blur }

/// <summary>
/// The drawing editor: arrows, lines, boxes, ellipses, highlights, free pen, text labels, numbered steps and blur
/// (pixelation, which really removes what is under it) on a picture, for tutorials and pointing things out.
/// Every mark can be selected, moved and deleted; Ctrl+Z / Ctrl+Y undo and redo. The picture is saved at its own
/// size (pixel for pixel), in its own format.
/// </summary>
public sealed class AnnotateWindow : Window
{
    readonly string file;
    readonly AnnotateMode mode;
    /// <summary>The friend the picture goes to (Send, and Capture with "Draw first"), or null.</summary>
    readonly string? sendTo;
    readonly TrayController app;
    readonly BitmapSource source;
    readonly byte[] pixels; // the original in Bgra32, for blur
    readonly int pw, ph;

    readonly Grid stage = new() { ClipToBounds = true };       // what is saved: the picture and the marks
    readonly Canvas layer = new();                                // the marks
    readonly Canvas overlay = new() { IsHitTestVisible = true };  // selection frame and the text being typed (never saved)
    readonly Grid host = new();                                   // stage + overlay, zoomed
    readonly ScaleTransform zoom = new(1, 1);
    readonly ScrollViewer viewer = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false,
    };
    readonly Rectangle selFrame = new() { StrokeThickness = 1.5, StrokeDashArray = [4, 3], Stroke = Brushes.DeepSkyBlue, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    readonly TextBlock zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 54, TextAlignment = TextAlignment.Center };
    readonly TextBlock hint = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Dictionary<AnnotateTool, RadioButton> toolButtons = [];
    readonly List<Button> swatches = [];
    readonly List<ToggleButton> sizeButtons = [];
    readonly Button undoBtn = new(), redoBtn = new(), deleteBtn = new();

    AnnotateTool tool = AnnotateTool.Arrow;
    Color color;
    int size;
    FrameworkElement? selected;
    FrameworkElement? drawing;     // the mark being drawn
    Point start;
    Point moveFrom;
    double moveDx, moveDy;
    bool moving;
    TextBox? typing;
    Point typingAt;
    readonly Stack<Step> undo = new(), redo = new();

    public AnnotateOutcome Outcome { get; private set; } = AnnotateOutcome.Cancelled;
    /// <summary>"Draw first" for a friend: closed without sending (Don't send, ✕, Esc twice).</summary>
    public bool Aborted { get; private set; }
    public string? SavedFile { get; private set; }
    public int SavedWidth => pw;
    public int SavedHeight => ph;

    /// <summary>One undoable change.</summary>
    sealed record Step(Action Undo, Action Redo);

    /// <summary>What a blur mark covers (it is redrawn from the original after a move).</summary>
    sealed record BlurTag(int X, int Y, int W, int H, int Block);

    sealed record NumberTag(int N);

    static readonly Color[] Palette =
    [
        Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0xFD, 0xD8, 0x35), Color.FromRgb(0x43, 0xA0, 0x47),
        Color.FromRgb(0x1E, 0x88, 0xE5), Color.FromRgb(0x8E, 0x24, 0xAA), Color.FromRgb(0x21, 0x21, 0x21), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    public AnnotateWindow(string file, AnnotateMode mode, TrayController app, string? sendTo = null)
    {
        this.file = file;
        this.mode = mode;
        this.app = app;
        this.sendTo = sendTo;
        // A picture taken for a friend is only sent with one of the two send buttons.
        Aborted = mode == AnnotateMode.Capture && sendTo != null;
        source = LoadPicture(file);
        pw = source.PixelWidth;
        ph = source.PixelHeight;
        var conv = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        pixels = new byte[pw * ph * 4];
        conv.CopyPixels(pixels, pw * 4, 0);
        color = ParseColor(app.Settings.AnnotateColor);
        size = app.Settings.AnnotateSize;

        Title = sendTo != null ? $"Peergos Snap – draw, then send to {sendTo}"
            : mode == AnnotateMode.Capture ? "Peergos Snap – draw on the picture" : "Peergos Snap – draw on a copy";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Theme.Attach(this);
        var work = SystemParameters.WorkArea;
        Width = Math.Min(work.Width * 0.92, Math.Max(940, pw + 80));
        Height = Math.Min(work.Height * 0.92, Math.Max(620, ph + 190));
        MinWidth = 760;
        MinHeight = 480;

        // The picture at its own pixel size: one unit = one pixel of the saved picture.
        var img = new Image { Source = source, Width = pw, Height = ph, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        stage.Width = pw; stage.Height = ph;
        stage.Children.Add(img);
        layer.Width = pw; layer.Height = ph;
        stage.Children.Add(layer);
        overlay.Width = pw; overlay.Height = ph;
        overlay.Children.Add(selFrame);
        host.Children.Add(stage);
        host.Children.Add(overlay);
        host.LayoutTransform = zoom;
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.VerticalAlignment = VerticalAlignment.Center;
        host.Background = Brushes.Transparent; // the whole picture takes the mouse
        viewer.Content = host;

        host.MouseLeftButtonDown += Down;
        host.MouseMove += MoveMouse;
        host.MouseLeftButtonUp += Up;
        host.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && tool == AnnotateTool.Select && selected?.Tag is string) EditText(selected); };
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            ZoomAt(zoom.ScaleX * Math.Pow(1.15, e.Delta / 120.0), e.GetPosition(viewer));
            e.Handled = true;
        };

        var root = new DockPanel();
        root.Children.Add(Toolbar());
        root.Children.Add(BottomBar());
        var view = new Border { Background = (Brush)Application.Current.Resources["Bg3"], Child = viewer };
        root.Children.Add(view);
        Content = root;

        PreviewKeyDown += Keys;
        Focusable = true;
        Loaded += (_, _) => { Fit(); Activate(); Focus(); }; // the tool keys work at once
        Closing += (_, e) =>
        {
            if (Outcome != AnnotateOutcome.Cancelled || layer.Children.Count == 0) return;
            var r = MessageBox.Show(this, Aborted ? "Close without sending? The picture stays in your captures, without the drawing."
                    : mode == AnnotateMode.Capture ? "Close without your drawing? The picture is used as it was taken."
                    : "Close without saving your drawing?", "Peergos Snap", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) e.Cancel = true;
        };
        SetTool(AnnotateTool.Arrow);
        MarkSwatches();
        UpdateButtons();
    }

    /// <summary>Read through a stream, not a URI: WPF keeps pictures loaded from a URI in a cache, and the file is
    /// about to change.</summary>
    static BitmapSource LoadPicture(string file)
    {
        using var fs = File.OpenRead(file);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = fs;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Palette[0]; }
    }

    // ---------- bars ----------

    UIElement Toolbar()
    {
        var bar = new WrapPanel { Margin = new Thickness(8, 8, 8, 2) };
        void Tool(AnnotateTool t, string text, string key, string tip)
        {
            var b = new RadioButton { Content = text, GroupName = "tool", Margin = new Thickness(0, 0, 4, 6), Padding = new Thickness(9, 4, 9, 4),
                                      ToolTip = $"{tip} ({key})", Focusable = false };
            b.SetResourceReference(StyleProperty, typeof(ToggleButton));
            b.Checked += (_, _) => SetTool(t);
            toolButtons[t] = b;
            bar.Children.Add(b);
        }
        Tool(AnnotateTool.Select, "↖ Select", "V", "Select a mark to move it (drag) or delete it (Delete); double-click a text to change it");
        Tool(AnnotateTool.Arrow, "➜ Arrow", "A", "Drag from where the arrow starts to what it points at");
        Tool(AnnotateTool.Line, "╱ Line", "L", "A straight line (hold Shift for steps of 45°)");
        Tool(AnnotateTool.Box, "▭ Box", "R", "A frame around something (Shift: square)");
        Tool(AnnotateTool.Ellipse, "◯ Ellipse", "E", "A circle around something (Shift: round)");
        Tool(AnnotateTool.Highlight, "▮ Highlight", "H", "Mark an area like with a highlighter pen");
        Tool(AnnotateTool.Pen, "✎ Pen", "P", "Draw freely");
        Tool(AnnotateTool.Text, "T Text", "T", "Click where the label goes and type; Enter finishes, Shift+Enter starts a new line");
        Tool(AnnotateTool.Number, "① Number", "N", "Click to place numbered steps 1, 2, 3 …");
        Tool(AnnotateTool.Blur, "▦ Blur", "B", "Drag over something private (passwords, names, faces): it becomes unreadable in the saved picture");
        bar.Children.Add(new Border { Width = 12 });
        foreach (var c in Palette)
        {
            var sw = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 3, 6), Padding = new Thickness(0), Focusable = false, Tag = c,
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(2), ToolTip = "Colour",
                Template = SwatchTemplate(),
            };
            sw.Click += (_, _) => SetColor(c);
            swatches.Add(sw);
            bar.Children.Add(sw);
        }
        bar.Children.Add(new Border { Width = 8 });
        foreach (var (label, i) in new[] { ("S", 0), ("M", 1), ("L", 2) })
        {
            var b = new ToggleButton { Content = label, MinWidth = 32, Margin = new Thickness(0, 0, 3, 6), Padding = new Thickness(6, 4, 6, 4), Focusable = false,
                                       ToolTip = "Line width and text size (1, 2, 3)" };
            int idx = i;
            b.Click += (_, _) => SetSize(idx);
            sizeButtons.Add(b);
            bar.Children.Add(b);
        }
        bar.Children.Add(new Border { Width = 12 });
        undoBtn.Content = "↶ Undo"; undoBtn.ToolTip = "Undo (Ctrl+Z)"; undoBtn.Click += (_, _) => Undo();
        redoBtn.Content = "↷ Redo"; redoBtn.ToolTip = "Redo (Ctrl+Y)"; redoBtn.Click += (_, _) => Redo();
        deleteBtn.Content = "✕ Delete"; deleteBtn.ToolTip = "Delete the selected mark (Delete)"; deleteBtn.Click += (_, _) => DeleteSelected();
        foreach (var b in new[] { undoBtn, redoBtn, deleteBtn })
        {
            b.Margin = new Thickness(0, 0, 4, 6);
            b.Padding = new Thickness(9, 4, 9, 4);
            b.Focusable = false;
            bar.Children.Add(b);
        }
        bar.Children.Add(new Border { Width = 12 });
        var zOut = new Button { Content = "−", MinWidth = 30, Margin = new Thickness(0, 0, 2, 6), Focusable = false, ToolTip = "Zoom out (Ctrl + mouse wheel)" };
        zOut.Click += (_, _) => ZoomAt(zoom.ScaleX / 1.25, null);
        var zIn = new Button { Content = "+", MinWidth = 30, Margin = new Thickness(0, 0, 4, 6), Focusable = false, ToolTip = "Zoom in (Ctrl + mouse wheel)" };
        zIn.Click += (_, _) => ZoomAt(zoom.ScaleX * 1.25, null);
        var fit = new Button { Content = "Fit", Margin = new Thickness(0, 0, 4, 6), Focusable = false, ToolTip = "The whole picture (F)" };
        fit.Click += (_, _) => Fit();
        var actual = new Button { Content = "100 %", Margin = new Thickness(0, 0, 4, 6), Focusable = false, ToolTip = "Actual size (Ctrl+0)" };
        actual.Click += (_, _) => ZoomAt(1 / Dpi, null);
        bar.Children.Add(zOut);
        bar.Children.Add(zoomText);
        bar.Children.Add(zIn);
        bar.Children.Add(fit);
        bar.Children.Add(actual);
        var border = new Border { Child = bar, BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(border, Dock.Top);
        return border;
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        t.VisualTree = f;
        return t;
    }

    UIElement BottomBar()
    {
        var bar = new DockPanel { Margin = new Thickness(10, 8, 10, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button B(string text, string tip, Action a, bool accent = false)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 5, 14, 5) };
            if (accent) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
            b.Click += (_, _) => a();
            buttons.Children.Add(b);
            return b;
        }
        B("Copy", "Copy the picture with the drawing to the clipboard (Ctrl+C)", CopyToClipboard);
        if (mode == AnnotateMode.Capture && sendTo != null)
        {
            B("Don't send", "Keep the picture in your captures and send nothing (Esc twice)", () => { Aborted = true; layer.Children.Clear(); Close(); });
            B("Send without drawing", $"Send the picture to {sendTo} as it was taken",
                () => { Aborted = false; Outcome = AnnotateOutcome.Cancelled; layer.Children.Clear(); Close(); });
            B($"Send to {sendTo} ✓", "Put the drawing into the picture and send it (Ctrl+S)", () => { Aborted = false; Finish(AnnotateOutcome.Saved); }, accent: true);
        }
        else if (mode == AnnotateMode.Capture)
        {
            B("Skip", "Use the picture as it was taken (Esc twice)", () => { Outcome = AnnotateOutcome.Cancelled; layer.Children.Clear(); Close(); });
            B("Done ✓", "Put the drawing into the picture and go on as usual (Ctrl+S)", () => Finish(AnnotateOutcome.Saved), accent: true);
        }
        else if (mode == AnnotateMode.Send)
        {
            B("Cancel", "Close without saving", Close);
            B("Save copy", "Save as a new capture; nothing is sent", () => Finish(AnnotateOutcome.Saved));
            B($"Send to {sendTo} ✓", $"Save as a new capture and send it to {sendTo} (Ctrl+S)", () => Finish(AnnotateOutcome.SavedSend), accent: true);
        }
        else
        {
            B("Cancel", "Close without saving", Close);
            B("Save copy", "Save as a new capture; the original stays as it is", () => Finish(AnnotateOutcome.Saved));
            B("Save & copy picture", "Save as a new capture and copy it to the clipboard", () => Finish(AnnotateOutcome.SavedCopy));
            B("Save & upload", "Save as a new capture, upload it and copy the link (Ctrl+S)", () => Finish(AnnotateOutcome.SavedUpload), accent: true);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        hint.Foreground = (Brush)Application.Current.Resources["Fg3"];
        bar.Children.Add(hint);
        var border = new Border { Child = bar, BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(border, Dock.Bottom);
        return border;
    }

    void SetTool(AnnotateTool t)
    {
        CommitText();
        tool = t;
        if (toolButtons.TryGetValue(t, out var b) && b.IsChecked != true) b.IsChecked = true;
        if (t != AnnotateTool.Select) Select(null);
        host.Cursor = t switch { AnnotateTool.Select => Cursors.Arrow, AnnotateTool.Text => Cursors.IBeam, _ => Cursors.Cross };
        hint.Text = t switch
        {
            AnnotateTool.Select => "Click a mark to select it, drag to move it, Delete removes it.",
            AnnotateTool.Text => "Click where the label goes, type, Enter to finish.",
            AnnotateTool.Number => "Click to place the next number.",
            AnnotateTool.Blur => "Drag over what nobody should read.",
            _ => "Drag on the picture. Ctrl+Z undoes, Ctrl + mouse wheel zooms.",
        };
    }

    void MarkSwatches()
    {
        foreach (var s in swatches)
            s.BorderBrush = (Color)s.Tag == color ? (Brush)Application.Current.Resources["Acc"] : (Brush)Application.Current.Resources["Line"];
        foreach (var s in swatches) s.BorderThickness = new Thickness((Color)s.Tag == color ? 3 : 1);
    }

    void SetColor(Color c)
    {
        color = c;
        MarkSwatches();
        app.UpdateSettings(s => s.AnnotateColor = c.ToString());
        // A selected mark takes the new colour.
        if (selected != null && selected.Tag is not BlurTag) Recolor(selected, c);
    }

    void SetSize(int s)
    {
        size = s;
        for (int i = 0; i < sizeButtons.Count; i++) sizeButtons[i].IsChecked = i == s;
        app.UpdateSettings(x => x.AnnotateSize = s);
    }

    void UpdateButtons()
    {
        undoBtn.IsEnabled = undo.Count > 0;
        redoBtn.IsEnabled = redo.Count > 0;
        deleteBtn.IsEnabled = selected != null;
        for (int i = 0; i < sizeButtons.Count; i++) sizeButtons[i].IsChecked = i == size;
    }

    // ---------- sizes ----------

    double Stroke => AnnotateLogic.StrokeWidth(size, pw, ph);
    double FontPx => AnnotateLogic.TextSize(size, pw, ph);

    // ---------- mouse ----------

    Point Pos(MouseEventArgs e)
    {
        var p = e.GetPosition(stage);
        return new Point(Math.Clamp(p.X, 0, pw), Math.Clamp(p.Y, 0, ph));
    }

    void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1) return;
        var p = Pos(e);
        if (typing != null && !IsInside(typing, e)) CommitText();
        switch (tool)
        {
            case AnnotateTool.Select:
                var hit = HitMark(p);
                Select(hit);
                if (hit != null)
                {
                    moving = true;
                    moveFrom = p;
                    moveDx = moveDy = 0;
                    host.CaptureMouse();
                }
                break;
            case AnnotateTool.Text:
                if (typing == null) StartText(p, null);
                break;
            case AnnotateTool.Number:
                AddMark(MakeNumber(p, NextNumber()));
                break;
            default:
                start = p;
                drawing = tool switch
                {
                    AnnotateTool.Pen => new Polyline { Points = [p], Stroke = new SolidColorBrush(color), StrokeThickness = Stroke,
                                                       StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
                    AnnotateTool.Blur => new Rectangle { Stroke = Brushes.Gray, StrokeDashArray = [3, 2], StrokeThickness = 1 / zoom.ScaleX },
                    _ => null,
                };
                if (drawing != null) layer.Children.Add(drawing);
                host.CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    void MoveMouse(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = Pos(e);
        if (moving && selected != null)
        {
            var dx = p.X - moveFrom.X;
            var dy = p.Y - moveFrom.Y;
            Translate(selected).X += dx;
            Translate(selected).Y += dy;
            moveDx += dx;
            moveDy += dy;
            moveFrom = p;
            ShowSelection();
            return;
        }
        if (!host.IsMouseCaptured || tool is AnnotateTool.Select or AnnotateTool.Text or AnnotateTool.Number) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var end = shift ? Constrain(start, p, tool is AnnotateTool.Line or AnnotateTool.Arrow) : p;
        switch (tool)
        {
            case AnnotateTool.Pen:
                if (drawing is Polyline pl) pl.Points.Add(p);
                break;
            case AnnotateTool.Blur:
                if (drawing is Rectangle r) PlaceRect(r, start, end);
                break;
            default:
                if (drawing != null) layer.Children.Remove(drawing);
                drawing = Shape(tool, start, end);
                if (drawing != null) layer.Children.Add(drawing);
                break;
        }
    }

    void Up(object sender, MouseButtonEventArgs e)
    {
        if (!host.IsMouseCaptured) return;
        host.ReleaseMouseCapture();
        if (moving)
        {
            moving = false;
            if (selected != null && (Math.Abs(moveDx) > 0.5 || Math.Abs(moveDy) > 0.5))
            {
                var el = selected;
                double dx = moveDx, dy = moveDy;
                // Already moved on screen: only bake it in (blur is drawn again from the original at its new place).
                Translate(el).X -= dx; Translate(el).Y -= dy;
                (dx, dy) = ApplyMove(el, dx, dy);
                Push(new Step(() => ApplyMove(el, -dx, -dy), () => ApplyMove(el, dx, dy)));
            }
            return;
        }
        var p = Pos(e);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var end = shift ? Constrain(start, p, tool is AnnotateTool.Line or AnnotateTool.Arrow) : p;
        var mark = drawing;
        drawing = null;
        if (mark == null) return;
        layer.Children.Remove(mark);
        if (tool == AnnotateTool.Blur)
        {
            var (x, y, w, h) = AnnotateLogic.PixelRect(start.X, start.Y, end.X, end.Y, pw, ph);
            if (w >= 3 && h >= 3) AddMark(MakeBlur(x, y, w, h, AnnotateLogic.BlurBlock(size, w, h)));
            return;
        }
        if (tool == AnnotateTool.Pen ? mark is Polyline { Points.Count: >= 2 } : (start - end).Length >= 4)
            AddMark(mark);
    }

    static bool IsInside(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    /// <summary>The topmost mark under the pointer (with a few pixels of tolerance, so thin lines are easy to catch).</summary>
    FrameworkElement? HitMark(Point p)
    {
        FrameworkElement? found = null;
        var tol = Math.Max(4, 8 / zoom.ScaleX);
        VisualTreeHelper.HitTest(layer, null, r =>
        {
            for (var d = r.VisualHit as DependencyObject; d != null && d != layer; d = VisualTreeHelper.GetParent(d))
                if (d is FrameworkElement fe && VisualTreeHelper.GetParent(fe) == layer) { found = fe; return HitTestResultBehavior.Stop; }
            return HitTestResultBehavior.Continue;
        }, new GeometryHitTestParameters(new EllipseGeometry(p, tol, tol)));
        return found;
    }

    // ---------- marks ----------

    FrameworkElement? Shape(AnnotateTool t, Point a, Point b)
    {
        var brush = new SolidColorBrush(color);
        switch (t)
        {
            case AnnotateTool.Arrow:
            case AnnotateTool.Line:
                return new WPath
                {
                    Data = t == AnnotateTool.Arrow ? ArrowGeometry(a, b, Stroke) : new LineGeometry(a, b),
                    Stroke = brush, Fill = brush, StrokeThickness = Stroke,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
            case AnnotateTool.Box:
            case AnnotateTool.Ellipse:
            {
                Shape s = t == AnnotateTool.Box ? new Rectangle { RadiusX = 3, RadiusY = 3 } : new Ellipse();
                s.Stroke = brush;
                s.StrokeThickness = Stroke;
                s.Fill = Brushes.Transparent; // the inside can be clicked to select it
                PlaceRect(s, a, b);
                return s;
            }
            case AnnotateTool.Highlight:
            {
                var r = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B)), RadiusX = 2, RadiusY = 2 };
                PlaceRect(r, a, b);
                return r;
            }
        }
        return null;
    }

    static System.Windows.Media.Geometry ArrowGeometry(Point a, Point b, double width)
    {
        var (s, l, r) = AnnotateLogic.ArrowHead(a.X, a.Y, b.X, b.Y, width);
        Point shaftEnd = new(s.X, s.Y), left = new(l.X, l.Y), right = new(r.X, r.Y);
        var g = new PathGeometry();
        g.Figures.Add(new PathFigure(a, [new LineSegment(shaftEnd, true)], false) { IsFilled = false });
        g.Figures.Add(new PathFigure(b, [new LineSegment(left, true), new LineSegment(right, true)], true) { IsFilled = true });
        return g;
    }

    static Point Constrain(Point a, Point b, bool line)
    {
        var (x, y) = AnnotateLogic.Constrain(a.X, a.Y, b.X, b.Y, line);
        return new Point(x, y);
    }

    static Color TextOn(Color c) => AnnotateLogic.DarkText(c.R, c.G, c.B) ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;

    static void PlaceRect(FrameworkElement r, Point a, Point b)
    {
        Canvas.SetLeft(r, Math.Min(a.X, b.X));
        Canvas.SetTop(r, Math.Min(a.Y, b.Y));
        r.Width = Math.Abs(b.X - a.X);
        r.Height = Math.Abs(b.Y - a.Y);
    }

    int NextNumber() => layer.Children.OfType<FrameworkElement>().Select(e => e.Tag is NumberTag n ? n.N : 0).DefaultIfEmpty(0).Max() + 1;

    FrameworkElement MakeNumber(Point p, int n)
    {
        var d = AnnotateLogic.NumberDiameter(size, pw, ph);
        var g = new Grid { Width = d, Height = d, Tag = new NumberTag(n) };
        g.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = Math.Max(1.5, d / 14) });
        g.Children.Add(new TextBlock
        {
            Text = n.ToString(), Foreground = new SolidColorBrush(TextOn(color)), FontWeight = FontWeights.Bold,
            FontSize = d * (n > 9 ? 0.46 : 0.56), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI"),
        });
        Canvas.SetLeft(g, p.X - d / 2);
        Canvas.SetTop(g, p.Y - d / 2);
        return g;
    }

    /// <summary>Pixelated from the original picture: the saved picture no longer contains what was under it.</summary>
    FrameworkElement MakeBlur(int x, int y, int w, int h, int block)
    {
        var buf = AnnotateLogic.Pixelate(pixels, pw, ph, x, y, w, h, block);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, buf, w * 4);
        bmp.Freeze();
        var img = new Image { Source = bmp, Width = w, Height = h, Stretch = Stretch.Fill, Tag = new BlurTag(x, y, w, h, block) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, x);
        Canvas.SetTop(img, y);
        return img;
    }

    FrameworkElement MakeLabel(Point p, string text, Color? colour = null, double? fontSize = null)
    {
        var fs = fontSize ?? FontPx;
        var c = colour ?? color;
        var label = new Border
        {
            Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(fs * 0.25), Padding = new Thickness(fs * 0.35, fs * 0.12, fs * 0.35, fs * 0.16),
            Child = new TextBlock
            {
                Text = text, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TextOn(c)),
                FontFamily = new FontFamily("Segoe UI"),
            },
            Tag = text,
        };
        Canvas.SetLeft(label, p.X);
        Canvas.SetTop(label, p.Y);
        return label;
    }

    void Recolor(FrameworkElement el, Color c)
    {
        var before = Snapshot(el);
        Paint(el, c);
        var after = Snapshot(el);
        Push(new Step(() => Paint(el, before), () => Paint(el, after)));
    }

    static Color Snapshot(FrameworkElement el) => el switch
    {
        Shape { Stroke: SolidColorBrush b } => b.Color,
        Rectangle { Fill: SolidColorBrush f } => Color.FromRgb(f.Color.R, f.Color.G, f.Color.B),
        Border { Background: SolidColorBrush b } => b.Color,
        Grid g when g.Children.OfType<Ellipse>().FirstOrDefault()?.Fill is SolidColorBrush b => b.Color,
        _ => Colors.Red,
    };

    static void Paint(FrameworkElement el, Color c)
    {
        switch (el)
        {
            case Rectangle { Stroke: null } hl: hl.Fill = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)); break;
            case WPath p: p.Stroke = p.Fill = new SolidColorBrush(c); break;
            case Shape s: s.Stroke = new SolidColorBrush(c); break;
            case Border b:
                b.Background = new SolidColorBrush(c);
                if (b.Child is TextBlock t) t.Foreground = new SolidColorBrush(TextOn(c));
                break;
            case Grid g:
                foreach (var e in g.Children.OfType<Ellipse>()) e.Fill = new SolidColorBrush(c);
                foreach (var tb in g.Children.OfType<TextBlock>()) tb.Foreground = new SolidColorBrush(TextOn(c));
                break;
        }
    }

    static TranslateTransform Translate(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        var n = new TranslateTransform();
        el.RenderTransform = n;
        return n;
    }

    /// <summary>Moves a mark for good and returns how far it really moved. Blur is drawn again from the original at its
    /// new place, and stops at the picture's edge.</summary>
    (double Dx, double Dy) ApplyMove(FrameworkElement el, double dx, double dy)
    {
        if (el.Tag is BlurTag b)
        {
            int nx = Math.Clamp(b.X + (int)Math.Round(dx), 0, Math.Max(0, pw - b.W)), ny = Math.Clamp(b.Y + (int)Math.Round(dy), 0, Math.Max(0, ph - b.H));
            dx = nx - b.X;
            dy = ny - b.Y;
            var fresh = (Image)MakeBlur(nx, ny, b.W, b.H, b.Block);
            ((Image)el).Source = fresh.Source;
            el.Tag = fresh.Tag;
            Canvas.SetLeft(el, nx);
            Canvas.SetTop(el, ny);
        }
        else
        {
            Translate(el).X += dx;
            Translate(el).Y += dy;
        }
        if (el == selected) ShowSelection();
        return (dx, dy);
    }

    void AddMark(FrameworkElement el)
    {
        layer.Children.Add(el);
        Push(new Step(() => { layer.Children.Remove(el); if (selected == el) Select(null); }, () => layer.Children.Add(el)));
    }

    void DeleteSelected()
    {
        if (selected is not { } el) return;
        int at = layer.Children.IndexOf(el);
        layer.Children.Remove(el);
        Select(null);
        Push(new Step(() => layer.Children.Insert(Math.Min(at, layer.Children.Count), el), () => { layer.Children.Remove(el); if (selected == el) Select(null); }));
    }

    void Push(Step s)
    {
        undo.Push(s);
        redo.Clear();
        UpdateButtons();
    }

    void Undo()
    {
        CommitText();
        if (undo.Count == 0) return;
        var s = undo.Pop();
        s.Undo();
        redo.Push(s);
        UpdateButtons();
    }

    void Redo()
    {
        if (redo.Count == 0) return;
        var s = redo.Pop();
        s.Redo();
        undo.Push(s);
        UpdateButtons();
    }

    void Select(FrameworkElement? el)
    {
        selected = el;
        ShowSelection();
        deleteBtn.IsEnabled = el != null;
    }

    void ShowSelection()
    {
        if (selected == null || !layer.Children.Contains(selected)) { selFrame.Visibility = Visibility.Collapsed; return; }
        layer.UpdateLayout();
        var b = VisualTreeHelper.GetDescendantBounds(selected);
        if (b.IsEmpty) b = new Rect(0, 0, selected.ActualWidth, selected.ActualHeight);
        var box = selected.TransformToAncestor(layer).TransformBounds(b);
        box.Inflate(4, 4);
        selFrame.StrokeThickness = 1.5 / zoom.ScaleX;
        Canvas.SetLeft(selFrame, box.X);
        Canvas.SetTop(selFrame, box.Y);
        selFrame.Width = box.Width;
        selFrame.Height = box.Height;
        selFrame.Visibility = Visibility.Visible;
    }

    // ---------- text ----------

    FrameworkElement? editingLabel;
    Color editingColour;
    double editingFont;

    void StartText(Point p, string? text, Color? colour = null, double? fontSize = null)
    {
        typingAt = p;
        var fs = fontSize ?? FontPx;
        var c = colour ?? color;
        typing = new TextBox
        {
            Text = text ?? "", FontSize = fs, FontWeight = FontWeights.SemiBold, MinWidth = Math.Max(80, fs * 4), AcceptsReturn = false,
            Background = new SolidColorBrush(c), Foreground = new SolidColorBrush(TextOn(c)), BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
        };
        typing.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                var i = typing!.CaretIndex;
                typing.Text = typing.Text.Insert(i, "\n");
                typing.CaretIndex = i + 1;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter) { CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
        };
        Canvas.SetLeft(typing, p.X);
        Canvas.SetTop(typing, p.Y);
        overlay.Children.Add(typing);
        Dispatcher.BeginInvoke(() => { typing?.Focus(); typing?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void EditText(FrameworkElement label)
    {
        if (label.Tag is not string text) return;
        var at = label.TranslatePoint(new Point(0, 0), layer);
        editingLabel = label;
        editingColour = Snapshot(label);
        editingFont = (label as Border)?.Child is TextBlock tb ? tb.FontSize : FontPx;
        label.Visibility = Visibility.Hidden;
        StartText(at, text, editingColour, editingFont);
    }

    void CommitText()
    {
        if (typing == null) return;
        var text = typing.Text.TrimEnd();
        overlay.Children.Remove(typing);
        typing = null;
        var old = editingLabel;
        editingLabel = null;
        if (old != null)
        {
            old.Visibility = Visibility.Visible;
            if (text.Length == 0 || text == old.Tag as string) return;
            // A changed text replaces the old label, in that label's colour and size (undo brings the old one back).
            var fresh = MakeLabel(typingAt, text, editingColour, editingFont);
            int at = layer.Children.IndexOf(old);
            if (at < 0) { AddMark(fresh); return; } // the old label was deleted or undone meanwhile
            layer.Children.Remove(old);
            layer.Children.Insert(at, fresh);
            Select(null);
            Push(new Step(() => { Unselect(fresh); layer.Children.Remove(fresh); layer.Children.Insert(Math.Min(at, layer.Children.Count), old); },
                          () => { Unselect(old); layer.Children.Remove(old); layer.Children.Insert(Math.Min(at, layer.Children.Count), fresh); }));
            return;
        }
        if (text.Length > 0) AddMark(MakeLabel(typingAt, text));
    }

    /// <summary>A mark that leaves the picture (undo, redo) is no longer selected.</summary>
    void Unselect(FrameworkElement el)
    {
        if (selected == el) Select(null);
    }

    void CancelText()
    {
        if (typing == null) return;
        overlay.Children.Remove(typing);
        typing = null;
        if (editingLabel != null) { editingLabel.Visibility = Visibility.Visible; editingLabel = null; }
    }

    // ---------- keys and zoom ----------

    bool escOnce;

    void Keys(object sender, KeyEventArgs e)
    {
        if (typing != null && typing.IsKeyboardFocusWithin) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Escape) escOnce = false;
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): Redo(); e.Handled = true; return;
                case Key.Z: Undo(); e.Handled = true; return;
                case Key.Y: Redo(); e.Handled = true; return;
                case Key.C: CopyToClipboard(); e.Handled = true; return;
                case Key.S:
                    if (mode == AnnotateMode.Capture) Aborted = false;
                    Finish(mode switch { AnnotateMode.Capture => AnnotateOutcome.Saved, AnnotateMode.Send => AnnotateOutcome.SavedSend, _ => AnnotateOutcome.SavedUpload });
                    e.Handled = true;
                    return;
                case Key.D0 or Key.NumPad0: ZoomAt(1 / Dpi, null); e.Handled = true; return;
            }
            return;
        }
        AnnotateTool? t = key switch
        {
            Key.V => AnnotateTool.Select, Key.A => AnnotateTool.Arrow, Key.L => AnnotateTool.Line, Key.R => AnnotateTool.Box,
            Key.E => AnnotateTool.Ellipse, Key.H => AnnotateTool.Highlight, Key.P => AnnotateTool.Pen, Key.T => AnnotateTool.Text,
            Key.N => AnnotateTool.Number, Key.B => AnnotateTool.Blur, _ => null,
        };
        if (t is { } tt) { SetTool(tt); e.Handled = true; return; }
        switch (key)
        {
            case Key.D1 or Key.NumPad1: SetSize(0); e.Handled = true; break;
            case Key.D2 or Key.NumPad2: SetSize(1); e.Handled = true; break;
            case Key.D3 or Key.NumPad3: SetSize(2); e.Handled = true; break;
            case Key.F: Fit(); e.Handled = true; break;
            case Key.Delete or Key.Back: DeleteSelected(); e.Handled = true; break;
            case Key.Escape:
                e.Handled = true;
                if (selected != null) { Select(null); break; }
                // Twice in a row closes (a single Esc never throws a drawing away by accident).
                if (escOnce) Close();
                else { escOnce = true; hint.Text = "Press Esc again to close."; }
                break;
        }
    }

    void Fit()
    {
        viewer.UpdateLayout();
        double w = Math.Max(100, viewer.ViewportWidth - 16), h = Math.Max(100, viewer.ViewportHeight - 16);
        ZoomAt(Math.Min(1 / Dpi, Math.Min(w / pw, h / ph)), null);
    }

    /// <summary>The display scaling (1.25 at 125 %): the stage is in picture pixels, the screen in units of 1/96 inch.</summary>
    double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Zooms, keeping the point under the mouse where it is (or the middle of the view).</summary>
    void ZoomAt(double s, Point? at)
    {
        s = Math.Clamp(s, 0.05, 16);
        var anchor = at ?? new Point(viewer.ViewportWidth / 2, viewer.ViewportHeight / 2);
        var before = viewer.TranslatePoint(anchor, stage); // picture pixel under the anchor
        zoom.ScaleX = zoom.ScaleY = s;
        zoomText.Text = $"{s * Dpi * 100:0} %";
        viewer.UpdateLayout();
        var after = stage.TranslatePoint(before, viewer);
        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + after.X - anchor.X);
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + after.Y - anchor.Y);
        ShowSelection();
    }

    // ---------- saving ----------

    /// <summary>The picture with the drawing, pixel for pixel.</summary>
    BitmapSource Render()
    {
        CommitText();
        selFrame.Visibility = Visibility.Collapsed;
        stage.UpdateLayout();
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new VisualBrush(stage)
            {
                Viewbox = new Rect(0, 0, pw, ph), ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, pw, ph), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, pw, ph));
        }
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        ShowSelection();
        return rtb;
    }

    void CopyToClipboard()
    {
        try
        {
            Clipboard.SetImage(Render());
            hint.Text = "Copied to the clipboard.";
        }
        catch (Exception e) { hint.Text = "Could not copy: " + e.Message; }
    }

    void Finish(AnnotateOutcome outcome)
    {
        try
        {
            var bmp = Render();
            string target;
            if (mode == AnnotateMode.Capture) target = file;
            else
            {
                var now = DateTime.Now;
                var folder = CaptureFiles.Folder(AppPaths.CacheDir, now, app.Settings.Subfolders);
                Directory.CreateDirectory(folder);
                var ext = System.IO.Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                target = System.IO.Path.Combine(folder, FileNames.Unique(folder, FileNames.ForCapture(now, ext is "jpg" or "jpeg" ? "jpg" : "png")));
            }
            Save(bmp, target);
            SavedFile = target;
            Outcome = outcome;
            Close();
        }
        catch (Exception e)
        {
            Log.Error("annotate save", e);
            MessageBox.Show(this, "The picture could not be saved: " + e.Message, "Peergos Snap", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void Save(BitmapSource bmp, string target)
    {
        var ext = System.IO.Path.GetExtension(target).ToLowerInvariant();
        BitmapEncoder enc = ext is ".jpg" or ".jpeg" ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        // Opaque like the captures themselves (no alpha channel): same look, smaller files.
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        var tmp = target + ".tmp";
        using (var fs = File.Create(tmp)) enc.Save(fs);
        File.Move(tmp, target, true);
    }
}
