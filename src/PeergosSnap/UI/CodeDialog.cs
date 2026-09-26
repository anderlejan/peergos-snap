using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>Asks for the code from the user's authenticator app during sign-in (two-factor login).</summary>
public sealed class CodeDialog : Window
{
    readonly TextBox box = new() { Width = 160, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Left, MaxLength = 12, Margin = new Thickness(0, 8, 0, 12) };

    CodeDialog()
    {
        Title = "Peergos Snap – two-factor code";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Theme.Attach(this);

        var ok = new Button { Content = "Sign in", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        ok.Style = (Style)Application.Current.TryFindResource("AccentButtonStyle") ?? ok.Style;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { if (box.Text.Trim().Length > 0) DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(18), MaxWidth = 380 };
        root.Children.Add(new TextBlock
        {
            Text = "Your Peergos account uses two-factor login. Enter the code from your authenticator app.",
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(box);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
    }

    /// <summary>The code, or null when cancelled.</summary>
    public static string? Ask(Window? owner)
    {
        var d = new CodeDialog();
        if (owner is { IsVisible: true }) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? d.box.Text.Trim().Replace(" ", "") : null;
    }
}
