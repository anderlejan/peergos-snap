using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PeergosSnap.Core;

/// <summary>
/// A colour scheme shared by every window of the app, including the User notes and Help pages.
/// Acc = accent for text, links and lines on the backgrounds; Acc2 = accent fill (primary buttons, check boxes,
/// selection); OnAcc = text on Acc2. Every scheme is checked for readable contrast by the unit tests.
/// </summary>
public sealed record Scheme(string Id, string Name, string Group, bool Dark, string Bg, string Bg2, string Bg3, string Fg, string Fg2, string Fg3,
    string Line, string Acc, string Acc2, string OnAcc)
{
    public override string ToString() => Name;
}

/// <summary>
/// Applies the chosen colour scheme and font size to all windows at once (WPF Fluent style for the controls,
/// scheme colours for backgrounds and accents) and tells the web pages (User notes, Help) the same values.
/// </summary>
public static class Theme
{
    public static readonly Scheme[] Schemes =
    [
        new("system", "Follow Windows (Peergos light / dark)", "Automatic", false, "", "", "", "", "", "", "", "", "", ""),
        // Dark
        new("peergos-dark", "Peergos dark", "Dark", true, "#16201D", "#1D2A26", "#243430", "#E8F1EE", "#B9CAC4", "#97ABA4", "#34483F", "#4FD1A5", "#1E7A5D", "#FFFFFF"),
        new("graphite", "Graphite", "Dark", true, "#1E1F22", "#26282C", "#2F3136", "#ECEDEF", "#C3C6CC", "#A0A5AD", "#3C3F45", "#78BDFF", "#1F66C4", "#FFFFFF"),
        new("midnight", "Midnight", "Dark", true, "#121826", "#1A2233", "#222C40", "#E6EBF5", "#B4BDD1", "#939EB8", "#303C55", "#A3B0FF", "#4655CC", "#FFFFFF"),
        new("forest-night", "Forest night", "Dark", true, "#141B14", "#1B241B", "#233023", "#E4EEE0", "#B8C8B2", "#96AB90", "#30412F", "#96D982", "#37702A", "#FFFFFF"),
        new("plum-night", "Plum night", "Dark", true, "#1C1622", "#251D2D", "#2E2438", "#EFE6F5", "#C9BAD5", "#A994B8", "#3E3049", "#D9ABFF", "#7443A8", "#FFFFFF"),
        new("contrast", "High contrast", "Dark", true, "#000000", "#0A0A0A", "#161616", "#FFFFFF", "#FFFFFF", "#D6D6D6", "#FFFFFF", "#FFD400", "#FFD400", "#000000"),
        // Medium brightness
        new("slate", "Slate", "Medium", true, "#4A5563", "#505B69", "#434D5A", "#F7F9FB", "#E2E7ED", "#CCD4DD", "#6C7989", "#B3DEFF", "#B3DEFF", "#0E2238"),
        new("storm", "Storm blue", "Medium", true, "#3E5470", "#445A77", "#384C66", "#F5F8FC", "#DDE5EF", "#C4D0DE", "#61789A", "#B0DCFF", "#B0DCFF", "#0E2238"),
        new("moss", "Moss", "Medium", true, "#4B5A45", "#51614B", "#44523F", "#F5F8F2", "#E0E7DA", "#C8D2C0", "#6C7D64", "#CDEBA6", "#CDEBA6", "#1C2B10"),
        new("mocha", "Mocha", "Medium", true, "#5A4A40", "#615046", "#51433A", "#FBF6F2", "#EDE2D9", "#D8C9BC", "#7E6A5D", "#FFD2A1", "#FFD2A1", "#3A2210"),
        new("dusk", "Dusk violet", "Medium", true, "#534A66", "#5A506E", "#4B435C", "#F8F5FC", "#E6DFEF", "#CFC5DE", "#74698A", "#DECBFF", "#DECBFF", "#2A1A4A"),
        new("teal-stone", "Teal stone", "Medium", true, "#3F5B5B", "#456262", "#395353", "#F3F9F8", "#DBEAE8", "#C1D6D3", "#5F7E7D", "#A6EFE3", "#A6EFE3", "#0E2E2A"),
        // Bright, soft on the eyes (no pure white)
        new("peergos-light", "Peergos light", "Bright", false, "#EEF4F1", "#F7FAF8", "#E2ECE7", "#1E2B26", "#44544D", "#5A6B63", "#C6D6CE", "#17684F", "#1F7457", "#FFFFFF"),
        new("paper", "Paper", "Bright", false, "#F3F1EC", "#FAF9F6", "#E8E5DE", "#2A2825", "#4F4B45", "#65605A", "#D3CEC4", "#1D5A9E", "#1F5FA8", "#FFFFFF"),
        new("sepia", "Sepia", "Bright", false, "#F2E9D8", "#F8F2E6", "#E8DCC6", "#3A2E22", "#5A4A3A", "#6E5C4A", "#D3C2A4", "#834A1C", "#8A4E1F", "#FFFFFF"),
        new("mint", "Mint", "Bright", false, "#E8F4EE", "#F3FAF6", "#D9ECE2", "#1C2E26", "#3D5449", "#546B60", "#BDD7C9", "#11643F", "#157A52", "#FFFFFF"),
        new("sky", "Sky", "Bright", false, "#EAF1F8", "#F4F8FC", "#DCE7F2", "#1B2733", "#3D4F61", "#56697C", "#C1D1E2", "#1A5896", "#1D62A6", "#FFFFFF"),
        new("lavender", "Lavender mist", "Bright", false, "#F0EDF6", "#F8F6FB", "#E4DFEE", "#2A2433", "#4C435C", "#62587A", "#D0C8DE", "#56379A", "#6441A8", "#FFFFFF"),
    ];

    public const double BaseFontSize = 14;
    public static Scheme Current { get; private set; } = Schemes[1];
    public static double FontScale { get; private set; } = 1;
    public static double FontSize => Math.Round(BaseFontSize * FontScale, 1);
    public static event Action? Changed;

    /// <summary>Old scheme ids from earlier versions map to their closest current scheme.</summary>
    static readonly Dictionary<string, string> Renamed = new() { ["dark"] = "graphite", ["light"] = "paper" };

    public static bool Known(string? id) => id != null && (Schemes.Any(x => x.Id == id) || Renamed.ContainsKey(id));

    public static Scheme Resolve(string id)
    {
        if (Renamed.TryGetValue(id, out var newId)) id = newId;
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
    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Blends a towards b (t = 0..1).</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>The accent fill a little lighter (dark schemes) or darker (bright schemes), for hover.</summary>
    public static Color Hover(Scheme s) => Mix(C(s.Acc2), s.Dark ? Colors.White : Colors.Black, 0.12);

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
        // Accent of the Fluent controls (buttons, check boxes, sliders, focus lines): Fluent binds these inside its
        // own dictionary, so its brushes are recoloured there.
        var fill = C(Current.Acc2);
        var text = C(Current.Acc);
        var onFill = C(Current.OnAcc);
        foreach (var d in app.Resources.MergedDictionaries) Recolor(d, fill, text, onFill, Hover(Current));
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
            r[ck] = fill;
            r[bk] = new SolidColorBrush(fill);
        }
        r["TextFillColorPrimaryBrush"] = B(Current.Fg);
        r["TextFillColorSecondaryBrush"] = B(Current.Fg2);
        r["TextFillColorTertiaryBrush"] = B(Current.Fg3);
        r["ControlContentThemeFontSize"] = FontSize;
        foreach (Window w in app.Windows) Style(w);
        if (Environment.GetEnvironmentVariable("PEERGOS_SNAP_THEME_KEYS") == "1")
            foreach (var d in app.Resources.MergedDictionaries)
                Log.Info("keys: " + string.Join(" ", AllKeys(d).Where(k => k.Contains("Accent")).Distinct().OrderBy(k => k)));
        Changed?.Invoke();
    }
#pragma warning restore WPF0001

    static void Recolor(ResourceDictionary d, Color fill, Color text, Color onFill, Color hover)
    {
        foreach (var k in d.Keys.OfType<string>().ToList())
        {
            if (!k.Contains("Accent") || k.Contains("Disabled") || k.Contains("StrokeColorOnAccent")) continue;
            Color target = k.StartsWith("TextOnAccent") || k.StartsWith("AccentButtonForeground") ? onFill
                : k.Contains("AccentText") ? text
                : k.Contains("PointerOver") ? hover
                : fill;
            if (k.StartsWith("SystemAccentColor")) { d[k] = fill; continue; }
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
        foreach (var m in d.MergedDictionaries) Recolor(m, fill, text, onFill, hover);
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

    /// <summary>The same scheme for the web pages (CSS variables).</summary>
    public static Dictionary<string, string> CssVariables() => new()
    {
        ["--bg"] = Current.Bg, ["--bg2"] = Current.Bg2, ["--bg3"] = Current.Bg3,
        ["--fg"] = Current.Fg, ["--fg2"] = Current.Fg2, ["--fg3"] = Current.Fg3,
        ["--line"] = Current.Line, ["--acc"] = Current.Acc, ["--acc2"] = Current.Acc2, ["--on-acc"] = Current.OnAcc,
        ["--sel-bg"] = Current.Bg3,
        ["--base-size"] = FontSize.ToString(CultureInfo.InvariantCulture) + "px",
        ["color-scheme"] = Current.Dark ? "dark" : "light",
    };

    // ---------- contrast (WCAG 2) ----------

    public static double Luminance(string hex)
    {
        var c = C(hex);
        static double F(byte v) { var s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    public static double Contrast(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
