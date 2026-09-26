using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

public enum FinishChoice { Upload, Clipboard, SaveAs, Discard, KeepLocal }

/// <summary>Short confirmation after a recording: upload, copy the file, save it elsewhere, or throw it away.</summary>
public sealed class FinishRecordingDialog : Window
{
    public FinishChoice Choice { get; private set; } = FinishChoice.KeepLocal;

    public FinishRecordingDialog(string file, TimeSpan length, Settings s)
    {
        Title = "Peergos Snap – recording finished";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Theme.Attach(this);

        var size = new FileInfo(file).Length;
        var info = new TextBlock
        {
            Text = $"{Path.GetFileName(file)}\n{length:mm\\:ss} · {size / 1024.0 / 1024.0:0.0} MB",
            Margin = new Thickness(0, 0, 0, 12),
        };
        var open = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("▶ Play it first"));
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } };
        open.Inlines.Add(link);

        bool linkMode = s.Output == OutputMode.SecretLink;
        Button B(string text, FinishChoice c, bool isDefault = false, string? tip = null)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = isDefault, ToolTip = tip };
            b.Click += (_, _) => { Choice = c; DialogResult = true; };
            return b;
        }
        var buttons = new WrapPanel();
        buttons.Children.Add(B("Upload & copy link", FinishChoice.Upload, linkMode, "Upload to Peergos and copy the secret link"));
        buttons.Children.Add(B("Copy video", FinishChoice.Clipboard, !linkMode, "Copy the video file to the clipboard (no upload)"));
        buttons.Children.Add(B("Save as…", FinishChoice.SaveAs, false, "Save a copy somewhere (no upload)"));
        buttons.Children.Add(B("Discard", FinishChoice.Discard, false, "Delete this recording"));

        var note = new TextBlock
        {
            Text = "Closing this window keeps the video only in the local captures folder.",
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg3"],
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(info);
        root.Children.Add(open);
        root.Children.Add(buttons);
        root.Children.Add(note);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Choice = FinishChoice.KeepLocal; DialogResult = false; } };
        Loaded += (_, _) => Activate();
    }
}
