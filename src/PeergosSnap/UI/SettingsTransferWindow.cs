using System.Windows;
using System.Windows.Controls;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>
/// Export or import settings (Settings → General): tick the groups. Personal details (account name, friends, folders
/// on this PC) are only included when ticked; the sign-in is never in a settings file.
/// </summary>
public sealed class SettingsTransferWindow : Window
{
    readonly TrayController app;
    readonly SettingsTransfer.Imported? file;
    readonly List<(SettingsGroup Group, CheckBox Box)> boxes = [];
    readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    public bool Imported { get; private set; }

    /// <summary>Export when <paramref name="file"/> is null; otherwise import from it.</summary>
    public SettingsTransferWindow(TrayController app, SettingsTransfer.Imported? file, string? fileName)
    {
        this.app = app;
        this.file = file;
        bool export = file == null;
        Title = export ? "Peergos Snap – export settings" : "Peergos Snap – import settings";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Attach(this);

        var root = new StackPanel { Margin = new Thickness(18), Width = 520 };
        root.Children.Add(new TextBlock
        {
            Text = export ? "Which settings go into the file?" : $"Import from {fileName}",
            FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(Hint(export
            ? "The file can be kept, given to a friend, or shown to an AI. Your sign-in, passwords and links are never in it."
            : (file!.App.Length > 0 ? $"Exported by Peergos Snap {file.App}. " : "") + "Only the ticked groups change; everything else, and your sign-in, stays as it is."));
        var groups = export ? SettingsTransfer.Groups.ToList() : file!.Groups.ToList();
        foreach (var g in groups)
        {
            var box = new CheckBox { IsChecked = !g.Personal, Margin = new Thickness(0, 6, 0, 0) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold });
            var c = new TextBlock { Text = g.Contents, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
            c.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
            text.Children.Add(c);
            if (g.Personal)
            {
                var warn = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 480,
                    Text = export ? "Leave this out when the file is for someone else." : "These are the details of whoever exported the file. While you are signed in, the account (server and username) stays yours.",
                };
                warn.SetResourceReference(TextBlock.ForegroundProperty, "Acc");
                text.Children.Add(warn);
            }
            box.Content = text;
            box.Click += (_, _) => Mark();
            boxes.Add((g, box));
            root.Children.Add(box);
        }
        root.Children.Add(result);

        var go = new Button { Content = export ? "Export…" : "Import", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 14, 8, 0) };
        go.SetResourceReference(StyleProperty, "AccentButtonStyle");
        var close = new Button { Content = "Cancel", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
        go.Click += (_, _) =>
        {
            if (export) Export();
            else Import(go, close);
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(go);
        buttons.Children.Add(close);
        root.Children.Add(buttons);
        Content = root;
        goButton = go;
        Mark();
    }

    readonly Button goButton;

    static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
        return t;
    }

    List<string> Chosen() => boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Group.Key).ToList();

    void Mark() => goButton.IsEnabled = Chosen().Count > 0;

    void Export()
    {
        var d = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the settings", FileName = "Peergos Snap settings.json", DefaultExt = ".json",
            Filter = "Settings file (*.json)|*.json|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (d.ShowDialog(this) != true) return;
        try
        {
            var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
            File.WriteAllText(d.FileName, SettingsTransfer.Export(app.Settings, Chosen(), version, DateTime.UtcNow), new System.Text.UTF8Encoding(false));
            Log.Info($"settings exported ({string.Join(", ", Chosen())})");
            result.Text = "Saved: " + d.FileName;
            app.Notify(ToastKind.Ok, "Settings exported", d.FileName, file: null);
            Close();
        }
        catch (Exception e) { result.Text = "Not saved: " + e.Message; }
    }

    void Import(Button go, Button close)
    {
        var chosen = Chosen();
        (List<string> Applied, List<string> Skipped) r = (new List<string>(), new List<string>());
        app.UpdateSettings(s => r = SettingsTransfer.ApplyTo(s, file!, chosen));
        Log.Info($"settings imported: {r.Applied.Count} applied, {r.Skipped.Count} skipped ({string.Join(", ", chosen)})");
        Imported = true;
        result.Text = $"{r.Applied.Count} settings imported." + (r.Skipped.Count > 0
            ? $" Left as they were: {string.Join(", ", r.Skipped)}" + (r.Skipped.Any(k => k is "Server" or "Username") ? " (the account stays yours while you are signed in)." : " (values Peergos Snap cannot use).")
            : "");
        go.IsEnabled = false;
        close.Content = "Close";
        foreach (var b in boxes) b.Box.IsEnabled = false;
    }
}
