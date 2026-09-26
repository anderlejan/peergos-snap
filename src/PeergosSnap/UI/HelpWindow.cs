using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap.UI;

/// <summary>The built-in Help (help\index.html) in a themed window. Hidden, not closed, so it opens instantly again.</summary>
public sealed class HelpWindow : Window
{
    readonly WebView2 web = new();
    bool ready;
    string pending = "";

    public HelpWindow()
    {
        Title = "Peergos Snap – Help";
        Width = 1000;
        Height = 780;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = web;
        Theme.Attach(this);
        Theme.Changed += () => Dispatcher.BeginInvoke(() => WebHost.PushTheme(web));
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    /// <summary>Shows the help, optionally at a section (the id of a heading, e.g. "settings-peergos").</summary>
    public async void ShowHelp(string section = "")
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (ready)
        {
            if (section.Length > 0) await web.ExecuteScriptAsync($"window.__goto && window.__goto({JsonValue.Create(section).ToJsonString()})");
            return;
        }
        pending = section;
        try
        {
            await WebHost.Init(web);
            web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                WebHost.PushTheme(web);
                web.CoreWebView2.PostWebMessageAsJson(new JsonObject { ["type"] = "info", ["version"] = Updater.Current.ToString() }.ToJsonString());
                if (pending.Length > 0) _ = web.ExecuteScriptAsync($"window.__goto && window.__goto({JsonValue.Create(pending).ToJsonString()})");
                pending = "";
            };
            web.CoreWebView2.Navigate(WebHost.Origin + "/help/index.html");
            ready = true;
        }
        catch (Exception e)
        {
            Log.Error("help webview", e);
            MessageBox.Show(this, "The Help needs the Microsoft Edge WebView2 runtime (part of Windows 11).\n\n" + e.Message, "Peergos Snap");
            Hide();
        }
    }
}
