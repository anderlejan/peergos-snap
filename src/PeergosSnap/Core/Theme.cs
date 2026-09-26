using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PeergosSnap.Core;

/// <summary>A colour scheme shared by every window of the app, including the User notes page.</summary>
public sealed record Scheme(string Id, string Name, bool Dark, string Bg, string Bg2, string Bg3, string Fg, string Fg2, string Fg3,
    string Line, string Acc, string Acc2, string OnAcc)
{
    public override string ToString() => Name;
}

/// <summary>
/// Applies the chosen colour scheme and font size to all windows at once (WPF Fluent style for the controls,
/// scheme colours for backgrounds and accents) and tells the User notes page the same values.
/// </summary>
public static class Theme
{
    public static readonly Scheme[] Schemes =
    [
        new("system", "Follow Windows (light / dark)", false, "", "", "", "", "", "", "", "", "", ""),
        new("peergos-dark", "Peergos dark", true, "#16201D", "#1D2A26", "#243430", "#E8F1EE", "#B3C4BE", "#7F948D", "#33463F", "#3FB68F", "#2B8A6E", "#FFFFFF"),
        new("peergos-light", "Peergos light", false, "#F2F7F5", "#FFFFFF", "#E6EFEB", "#15201C", "#3E4F49", "#6A7C75", "#CBDAD4", "#2B8A6E", "#237359", "#FFFFFF"),
        new("dark", "Dark", true, "#1F1F1F", "#2A2A2A", "#333333", "#F2F2F2", "#C4C4C4", "#8F8F8F", "#3E3E3E", "#4CA9F5", "#1F6FD1", "#FFFFFF"),
        new("light", "Light", false, "#F4F4F4", "#FFFFFF", "#EBEBEB", "#1A1A1A", "#474747", "#737373", "#D4D4D4", "#1F6FD1", "#185AB0", "#FFFFFF"),
        new("midnight", "Midnight (dark blue)", true, "#121826", "#1A2233", "#222C40", "#E6EBF5", "#AAB4CA", "#76819A", "#2E3A52", "#8C9EFF", "#5566D8", "#FFFFFF"),
        new("sepia", "Sepia (warm light)", false, "#F4ECDD", "#FBF6EC", "#EADFCB", "#3A2E22", "#5E4E3D", "#85735F", "#D8C8AE", "#9A5B2A", "#834A1F", "#FFFFFF"),
        new("contrast", "High contrast dark", true, "#000000", "#0A0A0A", "#161616", "#FFFFFF", "#FFFFFF", "#D0D0D0", "#FFFFFF", "#FFD400", "#FFD400", "#000000"),
    ];

    public const double BaseFontSize = 14;
    public static Scheme Current { get; private set; } = Schemes[1];
    public static double FontScale { get; private set; } = 1;
    public static double FontSize => Math.Round(BaseFontSize * FontScale, 1);
    public static event Action? Changed;

    public static Scheme Resolve(string id)
    {
        var s = Schemes.FirstOrDefault(x => x.Id == id) ?? Schemes[1];
        if (s.Id != "system") return s;
        return WindowsUsesDark() ? Schemes.First(x => x.Id == "peergos-dark") : Schemes.First(x => x.Id == "peergos-light");
    }

    static bool WindowsUsesDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }

#pragma warning disable WPF0001 // Fluent theme API is marked experimental
    /// <summary>Applies scheme + font size to the application and all open windows.</summary>
    public static void Apply(string schemeId, int fontPercent)
    {
        Current = Resolve(schemeId);
        FontScale = Math.Clamp(fontPercent, 80, 160) / 100.0;
        var app = Application.Current;
        if (app == null) return;
        app.ThemeMode = Current.Dark ? ThemeMode.Dark : ThemeMode.Light;
        ApplyResources(app);
        // Switching light <-> dark swaps the Fluent dictionaries and restyles windows a moment later; apply again then.
        app.Dispatcher.BeginInvoke(() => ApplyResources(app), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    static void ApplyResources(Application app)
    {
        var r = app.Resources;
        // Scheme brushes for our own windows.
        r["Bg"] = B(Current.Bg); r["Bg2"] = B(Current.Bg2); r["Bg3"] = B(Current.Bg3);
        r["Fg"] = B(Current.Fg); r["Fg2"] = B(Current.Fg2); r["Fg3"] = B(Current.Fg3);
        r["Line"] = B(Current.Line); r["Acc"] = B(Current.Acc); r["Acc2"] = B(Current.Acc2); r["OnAcc"] = B(Current.OnAcc);
        // Accent of the Fluent controls (buttons, checkboxes, sliders, focus lines): Fluent binds these inside its
        // own dictionary, so its brushes are recoloured there.
        var accent = C(Current.Dark ? Current.Acc : Current.Acc2);
        foreach (var d in app.Resources.MergedDictionaries) Recolor(d, accent, C(Current.OnAcc));
        // The Fluent brushes chain to the Windows accent (SystemColors.AccentColor*); override those keys app-wide.
        foreach (var (ck, bk) in new (object, object)[]
                 {
                     (SystemColors.AccentColorKey, SystemColors.AccentColorBrushKey),
                     (SystemColors.AccentColorLight1Key, SystemColors.AccentColorLight1BrushKey),
                     (SystemColors.AccentColorLight2Key, SystemColors.AccentColorLight2BrushKey),
                     (SystemColors.AccentColorLight3Key, SystemColors.AccentColorLight3BrushKey),
                     (SystemColors.AccentColorDark1Key, SystemColors.AccentColorDark1BrushKey),
                     (SystemColors.AccentColorDark2Key, SystemColors.AccentColorDark2BrushKey),
                     (SystemColors.AccentColorDark3Key, SystemColors.AccentColorDark3BrushKey),
                 })
        {
            r[ck] = accent;
            r[bk] = new SolidColorBrush(accent);
        }
        r["TextFillColorPrimaryBrush"] = B(Current.Fg);
        r["TextFillColorSecondaryBrush"] = B(Current.Fg2);
        r["TextFillColorTertiaryBrush"] = B(Current.Fg3);
        r["ControlContentThemeFontSize"] = FontSize;
        foreach (Window w in app.Windows) Style(w);
        if (Environment.GetEnvironmentVariable("PEERGOS_SNAP_THEME_KEYS") == "1")
            foreach (var d in app.Resources.MergedDictionaries)
                Log.Info("keys: " + string.Join(" ", AllKeys(d).Where(k => k.Contains("Accent")).Distinct().OrderBy(k => k)));
        Log.Info($"theme {Current.Id} font {FontSize} mode={app.ThemeMode.Value} merged=[{string.Join(", ", app.Resources.MergedDictionaries.Select(d => d.Source?.ToString() ?? "?"))}]");
        Changed?.Invoke();
    }
#pragma warning restore WPF0001

    static void Recolor(ResourceDictionary d, Color accent, Color onAccent)
    {
        foreach (var k in d.Keys.OfType<string>().ToList())
        {
            if (!k.Contains("Accent") || k.Contains("Disabled") || k.Contains("StrokeColorOnAccent")) continue;
            bool onAcc = k.StartsWith("TextOnAccent") || k.StartsWith("AccentButtonForeground");
            var target = onAcc ? onAccent : accent;
            if (k.StartsWith("SystemAccentColor")) { d[k] = accent; continue; }
            switch (d[k])
            {
                case SolidColorBrush b:
                    // Replace, never change in place: the brush objects are shared with the other Fluent dictionary.
                    d[k] = new SolidColorBrush(Color.FromArgb(b.Color.A, target.R, target.G, target.B)) { Opacity = b.Opacity };
                    break;
                case Color c:
                    d[k] = Color.FromArgb(c.A, target.R, target.G, target.B);
                    break;
            }
        }
        foreach (var m in d.MergedDictionaries) Recolor(m, accent, onAccent);
    }

    static IEnumerable<string> AllKeys(ResourceDictionary d)
    {
        foreach (var k in d.Keys) if (k is string ks) yield return ks + (d[k] is Color ? "(C)" : d[k] is Brush ? "(B)" : "");
        foreach (var m in d.MergedDictionaries) foreach (var k in AllKeys(m)) yield return k;
    }

    /// <summary>Gives a normal (non-overlay) window the scheme background and font size, now and on every change.</summary>
    public static void Attach(Window w)
    {
        w.Tag ??= "themed";
        Style(w);
        void OnChange() => w.Dispatcher.BeginInvoke(() => Style(w));
        Changed += OnChange;
        w.Closed += (_, _) => Changed -= OnChange;
    }

    static void Style(Window w)
    {
        if (w.Tag as string != "themed") return;
        w.Background = B(Current.Bg);
        w.Foreground = B(Current.Fg);
        w.FontSize = FontSize;
        w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
    }

    /// <summary>The same scheme for the User notes page (CSS variables).</summary>
    public static Dictionary<string, string> CssVariables() => new()
    {
        ["--bg"] = Current.Bg, ["--bg2"] = Current.Bg2, ["--bg3"] = Current.Bg3,
        ["--fg"] = Current.Fg, ["--fg2"] = Current.Fg2, ["--fg3"] = Current.Fg3,
        ["--line"] = Current.Line, ["--acc"] = Current.Acc, ["--acc2"] = Current.Acc2, ["--on-acc"] = Current.OnAcc,
        ["--sel-bg"] = Current.Bg3,
        ["--base-size"] = FontSize.ToString(CultureInfo.InvariantCulture) + "px",
        ["color-scheme"] = Current.Dark ? "dark" : "light",
    };
}
