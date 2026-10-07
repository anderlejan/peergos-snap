using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>Parts of right-click menus (since 2.7).</summary>
public static class MenuParts
{
    /// <summary>A small, quiet heading over a group of entries that do similar things ("Delete", "Open"): not
    /// clickable, skipped by the keyboard.</summary>
    public static MenuItem Heading(string text)
    {
        var t = new TextBlock { Text = text, FontSize = Math.Round(Theme.FontSize * 0.85, 1), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
        var item = new MenuItem { Header = t, IsHitTestVisible = false, Focusable = false, IsTabStop = false, Tag = "heading" };
        // Screen readers read it as the group's heading.
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetItemType(item, "heading");
        return item;
    }

    /// <summary>An entry; <paramref name="enabled"/> false shows it greyed (it applies, but not right now).</summary>
    public static MenuItem Entry(string text, Action run, bool enabled = true, string? tip = null)
    {
        var i = new MenuItem { Header = text, IsEnabled = enabled, ToolTip = tip };
        i.Click += (_, _) => run();
        return i;
    }

    /// <summary>A group: its heading and entries, after a separator when something comes before it. Empty groups are
    /// left out.</summary>
    public static void Group(ItemsControl menu, string heading, params MenuItem?[] entries)
    {
        var shown = entries.Where(e => e != null).ToList();
        if (shown.Count == 0) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Heading(heading));
        foreach (var e in shown) menu.Items.Add(e!);
    }
}
