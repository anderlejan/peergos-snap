using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

public enum FinishChoice { Upload, Clipboard, SaveAs, Discard, KeepLocal }

/// <summary>Short confirmation after a recording (or, when asked for, after a picture): upload, copy the file, save
/// it elsewhere, or throw it away.</summary>
public sealed class FinishRecordingDialog : Window
{
    public FinishChoice Choice { get; private set; } = FinishChoice.KeepLocal;

    public FinishRecordingDialog(string file, TimeSpan length, Settings s, bool video = true, string soundNote = "")
    {
        Title = video ? "Peergos Snap – recording finished" : "Peergos Snap – picture taken";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Theme.Attach(this);

        var size = new FileInfo(file).Length;
        var info = new TextBlock
        {
            Text = video ? $"{Path.GetFileName(file)}\n{length:mm\\:ss} · {size / 1024.0 / 1024.0:0.0} MB" + (soundNote.Length > 0 ? " · " + soundNote : "")
                         : $"{Path.GetFileName(file)}\n{HistoryLogic.Size(size)}",
            Margin = new Thickness(0, 0, 0, 12),
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        if (!video)
        {
            // A small preview, so the decision is made on what was really captured.
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 640;
                bi.UriSource = new Uri(file);
                bi.EndInit();
                root.Children.Add(new Image { Source = bi, MaxWidth = 420, MaxHeight = 260, Stretch = System.Windows.Media.Stretch.Uniform,
                                              HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
            }
            catch (Exception e) { Log.Error("finish preview", e); }
        }
        var open = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(video ? "▶ Play it first" : "Open it first"));
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
        open.Inlines.Add(link);

        bool linkMode = s.Output == OutputMode.SecretLink;
        Button B(string text, FinishChoice c, bool isDefault = false, string? tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = isDefault, ToolTip = tip };
            b.Click += (_, _) => { Choice = c; DialogResult = true; };
            return b;
        }
        var what = video ? "video" : "picture";
        var buttons = new WrapPanel();
        buttons.Children.Add(B("Upload & copy link", FinishChoice.Upload, linkMode, "Upload to Peergos and copy the secret link"));
        buttons.Children.Add(B(video ? "Copy video" : "Copy picture", FinishChoice.Clipboard, !linkMode, $"Copy the {what} to the clipboard (no upload)"));
        buttons.Children.Add(B("Save as…", FinishChoice.SaveAs, false, "Save a copy somewhere (no upload)"));
        buttons.Children.Add(B("Discard", FinishChoice.Discard, false,
            s.DiscardPermanently ? $"Delete this {what} at once; nothing is kept" : $"Move this {what} to the Recycle Bin"));

        var note = new TextBlock
        {
            Text = $"Closing this window keeps the {what} only in the local captures folder.",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg3"],
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        };
        root.Children.Add(info);
        root.Children.Add(open);
        root.Children.Add(buttons);
        root.Children.Add(note);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Choice = FinishChoice.KeepLocal; DialogResult = false; }
            if (e.Key == Key.Delete) { Choice = FinishChoice.Discard; DialogResult = true; }
        };
        Loaded += (_, _) => Activate();
    }
}
