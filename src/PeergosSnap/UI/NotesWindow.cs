using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap.UI;

/// <summary>
/// Hosts the reusable "User notes" module (usernotes\*.js, unchanged) in WebView2. The page talks to this
/// window through postMessage; <see cref="NotesStore"/> replaces the module's Electron store.
/// The window is hidden, not closed, so the module's delayed save always completes.
/// </summary>
public sealed class NotesWindow : Window
{
    readonly WebView2 web = new();
    readonly NotesStore store = new(AppPaths.NotesFile);
    bool ready;
    DateTime lastUse = DateTime.MinValue;

    public bool RecentlyUsed => DateTime.Now - lastUse < TimeSpan.FromSeconds(1);

    readonly Func<Settings> settings;

    public NotesWindow(Func<Settings> settings)
    {
        this.settings = settings;
        Theme.Attach(this);
        Theme.Changed += () => Dispatcher.BeginInvoke(PushTheme);
        Title = "Peergos Snap – User notes";
        Width = 1200;
        Height = 760;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = web;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public async void ShowNotes()
    {
        Show();
        Activate();
        if (ready) { await web.ExecuteScriptAsync("window.__openNotes && window.__openNotes()"); return; }
        try
        {
            await WebHost.Init(web);
            var core = web.CoreWebView2;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, _) => PushTheme();
            core.Navigate(WebHost.Origin + "/notes-host/index.html");
            ready = true;
        }
        catch (Exception e)
        {
            Log.Error("notes webview", e);
            MessageBox.Show(this, "User notes need the Microsoft Edge WebView2 runtime (part of Windows 11).\n\n" + e.Message, "Peergos Snap");
            Hide();
        }
    }

    void PushTheme() => WebHost.PushTheme(web);

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        lastUse = DateTime.Now;
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); } catch { return; }
        var id = msg?["id"]?.GetValue<int>() ?? 0;
        var method = msg?["m"]?.GetValue<string>() ?? "";
        var args = msg?["a"] as JsonArray ?? [];
        JsonNode? value = null;
        string? error = null;
        try
        {
            switch (method)
            {
                case "config": value = Config(settings().PromptSourceLocation); break;
                case "get": value = store.Load(); break;
                case "save": store.Save(args[0] as JsonArray ?? []); value = true; break;
                case "saveText": value = new JsonObject { ["file"] = NotesStore.SaveText(AppPaths.PromptsDir, args[0]?.GetValue<string>() ?? "", args[1]?.GetValue<string>() ?? "prompt") }; break;
                case "copy": ClipboardService.SetText(args[0]?.GetValue<string>() ?? ""); value = true; break;
                case "openFolder":
                    var f = args[0]?.GetValue<string>() ?? "";
                    if (File.Exists(f)) Process.Start("explorer.exe", "/select,\"" + f + "\"");
                    else if (Directory.Exists(f)) Process.Start("explorer.exe", "\"" + f + "\"");
                    value = true;
                    break;
                case "print": value = await Print(args[0]?.GetValue<string>() ?? "", args[1]?["pdf"]?.GetValue<bool>() == true); break;
                case "close": Hide(); value = true; break;
                default: error = "unknown method " + method; break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("notes " + method, ex);
            error = ex.Message;
        }
        var reply = new JsonObject { ["id"] = id, ["ok"] = error == null, ["v"] = value, ["err"] = error };
        web.CoreWebView2.PostWebMessageAsJson(reply.ToJsonString());
    }

    /// <summary>
    /// Texts for the generated prompts. Nothing about the original developer: no names, local paths or
    /// repository owners. A source location appears only if the user typed one in Settings (kept on this PC).
    /// </summary>
    public static JsonObject Config(string? sourceLocation)
    {
        var ver = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var loc = string.IsNullOrWhiteSpace(sourceLocation)
            ? "Peergos Snap is free software (GPL-3.0-or-later). Work in a checkout of its source code: C#/.NET WPF app in src/PeergosSnap, Java Peergos bridge in bridge/, Inno Setup installer in installer/, build script build/build.ps1."
            : "The source is here: " + sourceLocation.Trim() + ". C#/.NET WPF app in src/PeergosSnap, Java Peergos bridge in bridge/, Inno Setup installer in installer/, build script build/build.ps1.";
        return new JsonObject
        {
            ["appName"] = "Peergos Snap",
            ["version"] = ver,
            ["appDescription"] = "Windows 11 tray app that captures pictures and region videos (live, non-freezing overlay) and shares them through Peergos secret links or the clipboard.",
            ["docs"] = new JsonArray("README.md", "src/PeergosSnap/help/index.html", "docs/PEERGOS-INTEGRATION.md", "CHANGELOG.md"),
            ["location"] = loc,
            ["rules"] = new JsonArray(
                "Keep every file GPL-3.0-compatible; never add code or binaries under licences that forbid public or GPL distribution. Record every new third-party component in THIRD-PARTY-NOTICES.md.",
                "Implement only what the notes ask; ask before widening a change.",
                "Test on copies only; never touch real captures or notes in tests.",
                "Bump the version (semantic versioning) and add a CHANGELOG entry; installers are never overwritten."),
            ["deliver"] = new JsonArray(
                @"Run build\build.ps1 (tests, bridge, runtime, publish, installer, source zip, SHA256).",
                "Install the new installer and verify the tray app runs."),
            ["extraShort"] = new JsonArray("Bump the version, build, install and verify as usual."),
        };
    }

    async Task<JsonNode?> Print(string html, bool pdf)
    {
        var w = new Window { Width = 800, Height = 900, Title = "Print", ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this };
        var wv = new WebView2();
        w.Content = wv;
        try
        {
            if (!pdf) w.Show();
            else { w.Left = -10000; w.Show(); }
            await wv.EnsureCoreWebView2Async(web.CoreWebView2.Environment);
            wv.CoreWebView2.Settings.IsScriptEnabled = false;
            var loaded = new TaskCompletionSource();
            wv.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            wv.CoreWebView2.NavigateToString(html);
            await loaded.Task;
            if (pdf)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = "Peergos Snap - work done.pdf" };
                if (dlg.ShowDialog(this) != true) return null;
                var ok = await wv.CoreWebView2.PrintToPdfAsync(dlg.FileName);
                if (!ok) throw new InvalidOperationException("The PDF could not be written");
                return new JsonObject { ["file"] = dlg.FileName };
            }
            wv.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            return true;
        }
        finally
        {
            if (pdf) w.Close();
            else w.Closed += (_, _) => wv.Dispose();
        }
    }
}

/// <summary>User notes storage (same format as the module's Electron store): { version: 2, notes: [...] }.</summary>
public sealed class NotesStore(string file)
{
    bool backedUp;

    public JsonNode Load()
    {
        if (!backedUp && File.Exists(file)) { Backup(15); backedUp = true; }
        try
        {
            if (File.Exists(file)) return JsonNode.Parse(File.ReadAllText(file).TrimStart('\uFEFF')) ?? new JsonObject();
        }
        catch
        {
            try { File.Copy(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
        }
        return new JsonObject { ["version"] = 2, ["notes"] = new JsonArray() };
    }

    public void Save(JsonArray notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var data = new JsonObject { ["version"] = 2, ["notes"] = notes.DeepClone() };
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, file, true);
    }

    void Backup(int keep)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(file)!, "backups");
            Directory.CreateDirectory(dir);
            var baseName = Path.GetFileNameWithoutExtension(file);
            File.Copy(file, Path.Combine(dir, $"{baseName}-{DateTime.Now:yyyy-MM-dd_HHmmss}.json"), true);
            var old = Directory.GetFiles(dir, baseName + "-*.json").OrderBy(f => f).ToList();
            while (old.Count > keep) { File.Delete(old[0]); old.RemoveAt(0); }
        }
        catch (Exception e) { Log.Error("notes backup", e); }
    }

    public static string SaveText(string folder, string text, string kind)
    {
        Directory.CreateDirectory(folder);
        var safe = new string((kind ?? "prompt").Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        if (safe.Length == 0) safe = "prompt";
        var f = Path.Combine(folder, FileNames.Unique(folder, $"{safe}-{DateTime.Now:yyyy-MM-dd_HHmmss}.md"));
        File.WriteAllText(f, text, new UTF8Encoding(false));
        return f;
    }
}
