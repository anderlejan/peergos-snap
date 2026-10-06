using System.Windows;
using System.Windows.Controls;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>
/// The question before something chosen in Explorer's right-click menu leaves this PC: what it is (names, how many,
/// how large) and where it goes (Peergos with links, or which friend). Nothing happens without a click on the action
/// button; Cancel is the default button, so Enter or Esc never upload or send by accident.
/// </summary>
public sealed class ShareConfirmWindow : Window
{
    public bool Confirmed { get; private set; }
    public string? Friend { get; private set; }
    public bool OpenDirect { get; private set; }
    public bool OpenSettings { get; private set; }
    /// <summary>Sending pictures: the "draw first" box was offered (since 2.6), and whether it was ticked.</summary>
    public bool CanDraw { get; private set; }
    public bool DrawFirst { get; private set; }

    public ShareConfirmWindow(IReadOnlyList<string> paths, bool send, IReadOnlyList<string> friends, string lastFriend, bool signedIn,
        bool drawFirst = false)
    {
        Title = send ? "Peergos Snap – send to a friend" : "Peergos Snap – upload to Peergos";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        Theme.Attach(this);

        var files = paths.Where(File.Exists).ToList();
        var folders = paths.Where(Directory.Exists).ToList();
        long bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        var root = new StackPanel { Margin = new Thickness(18), Width = 470 };
        root.Children.Add(new TextBlock
        {
            Text = send ? "Send this to a friend?" : "Upload this to your Peergos?",
            FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(new TextBlock { Text = ShareText.What(files.Count, folders.Count, bytes), Margin = new Thickness(0, 0, 0, 6) });
        var names = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
        names.Text = ShareText.Names(folders.Select(f => Path.GetFileName(f.TrimEnd('\\')) + "\\").Concat(files.Select(Path.GetFileName)).Select(n => n ?? "").ToList());
        root.Children.Add(names);

        var go = new Button { Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        go.SetResourceReference(StyleProperty, "AccentButtonStyle");
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), IsCancel = true, IsDefault = true };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
        ComboBox? to = null;
        CheckBox? draw = null;
        var extra = new Button { Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };

        if (send)
        {
            if (friends.Count == 0)
            {
                note.Text = "You have no friend for direct sharing yet: add one in the direct window first.";
                go.Content = "Send";
                go.IsEnabled = false;
                extra.Content = "Open the direct window";
                extra.Visibility = Visibility.Visible;
                extra.Click += (_, _) => { OpenDirect = true; Close(); };
            }
            else
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                row.Children.Add(new TextBlock { Text = "To", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                to = new ComboBox { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
                foreach (var f in friends) to.Items.Add(f);
                to.SelectedItem = friends.Contains(lastFriend) ? lastFriend : friends[0];
                row.Children.Add(to);
                root.Children.Add(row);
                go.Content = $"Send to {to.SelectedItem}";
                to.SelectionChanged += (_, _) => go.Content = $"Send to {to.SelectedItem}";
                // Pictures can get arrows, numbers or a note first (as "Draw first" in the direct window).
                int pictures = files.Count(f => FileKinds.Of(f) == FileKind.Picture);
                if (pictures > 0)
                {
                    CanDraw = true;
                    draw = new CheckBox
                    {
                        Content = pictures == 1 ? "Draw on the picture first" : $"Draw on the {pictures} pictures first (one after the other)",
                        IsChecked = drawFirst, Margin = new Thickness(0, 0, 0, 10),
                        ToolTip = "The drawing editor opens with the picture: add arrows, numbers or a note – Send to … then sends the drawn copy",
                    };
                    root.Children.Add(draw);
                }
                note.Text = "Your friend sees it within seconds and keeps their own copy." + (folders.Count > 0 ? " A folder travels as one ZIP file; your friend opens it as a folder with one click." : "");
            }
        }
        else if (!signedIn)
        {
            note.Text = "Uploading needs your Peergos account: sign in first (Settings → Peergos).";
            go.Content = "Upload";
            go.IsEnabled = false;
            extra.Content = "Sign in…";
            extra.Visibility = Visibility.Visible;
            extra.Click += (_, _) => { OpenSettings = true; Close(); };
        }
        else
        {
            go.Content = "Upload";
            note.Text = (files.Count > 0 ? "Each file gets its own secret link. " : "") + (folders.Count > 0 ? "A folder gets one secret link for everything in it. " : "")
                        + "The links are copied, and the history keeps them.";
        }
        root.Children.Add(note);
        go.Click += (_, _) =>
        {
            Friend = to?.SelectedItem as string;
            DrawFirst = draw?.IsChecked == true;
            Confirmed = true;
            Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(extra);
        buttons.Children.Add(go);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { Activate(); cancel.Focus(); };
    }
}

/// <summary>The words of the question (unit tested).</summary>
public static class ShareText
{
    public static string What(int files, int folders, long bytes)
    {
        var parts = new List<string>();
        if (files > 0) parts.Add((files == 1 ? "1 file" : $"{files} files") + (bytes > 0 ? $" ({HistoryLogic.Size(bytes)})" : ""));
        if (folders > 0) parts.Add(folders == 1 ? "1 folder" : $"{folders} folders");
        return parts.Count == 0 ? "Nothing that still exists." : string.Join(" and ", parts);
    }

    /// <summary>Up to eight names, then "… and N more".</summary>
    public static string Names(IReadOnlyList<string> names) =>
        names.Count <= 8 ? string.Join("\n", names) : string.Join("\n", names.Take(7)) + $"\n… and {names.Count - 7} more";
}
