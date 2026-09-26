using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PeergosSnap.Core;

namespace PeergosSnap.UI;

/// <summary>Shared WebView2 set-up for the app's own pages (User notes, Help): one browser profile, the app
/// folder served as https://notes.peergos-snap.local/, the colour scheme passed in, other links opened outside.</summary>
public static class WebHost
{
    public const string Host = "notes.peergos-snap.local";
    public const string Origin = "https://" + Host;
    static Task<CoreWebView2Environment>? env;

    public static Task<CoreWebView2Environment> Environment() => env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir);

    /// <summary>Prepares a WebView for an app page.</summary>
    public static async Task Init(WebView2 web)
    {
        await web.EnsureCoreWebView2Async(await Environment());
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.SetVirtualHostNameToFolderMapping(Host, AppPaths.AppDir, CoreWebView2HostResourceAccessKind.DenyCors);
        // Anything that is not one of our pages opens in the normal browser.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenOutside(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:") || e.Uri.StartsWith("data:")) return;
            e.Cancel = true;
            OpenOutside(e.Uri);
        };
        web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(Theme.Current.Bg);
    }

    /// <summary>Sends the app's colour scheme and font size to the page (CSS variables).</summary>
    public static void PushTheme(WebView2 web)
    {
        if (web.CoreWebView2 == null) return;
        var vars = new JsonObject();
        foreach (var (k, v) in Theme.CssVariables()) vars[k] = v;
        web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "theme", ["vars"] = vars }.ToJsonString());
        web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(Theme.Current.Bg);
    }

    static void OpenOutside(string uri)
    {
        if (!uri.StartsWith("https://") && !uri.StartsWith("http://") && !uri.StartsWith("mailto:")) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }
}
