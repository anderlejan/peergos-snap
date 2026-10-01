using System.Collections.Specialized;
using System.Runtime.InteropServices;
using PeergosSnap.Core;
using WinForms = System.Windows.Forms;

namespace PeergosSnap.Services;

/// <summary>Clipboard writes, retried because other apps sometimes hold the clipboard open for a moment.</summary>
public static class ClipboardService
{
    public static void SetText(string text) => Retry(() => WinForms.Clipboard.SetText(text));

    /// <summary>Puts a picture on the clipboard as a bitmap and as PNG, plus the file itself for apps that paste files.</summary>
    public static void SetImage(string file)
    {
        var png = File.ReadAllBytes(file);
        Retry(() =>
        {
            using var ms = new MemoryStream(png);
            using var img = System.Drawing.Image.FromStream(ms);
            using var bmp = new System.Drawing.Bitmap(img);
            var data = new WinForms.DataObject();
            data.SetData(WinForms.DataFormats.Bitmap, true, bmp);
            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                data.SetData("PNG", false, new MemoryStream(png));
            data.SetFileDropList(new StringCollection { file });
            WinForms.Clipboard.SetDataObject(data, true);
        });
    }

    /// <summary>Puts a file (e.g. a video) on the clipboard, as Explorer's Copy does.</summary>
    public static void SetFile(string file) =>
        Retry(() => WinForms.Clipboard.SetFileDropList(new StringCollection { file }));

    static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (ExternalException) when (i < 10) { Thread.Sleep(60); }
        }
    }

    /// <summary>Clipboard access needs an STA thread; run on the UI thread when called from a worker.</summary>
    public static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    /// <summary>Empties the clipboard if it still holds this link or this file (a discarded capture must not be pasted
    /// later). Anything else the user copied meanwhile stays.</summary>
    public static bool ClearIfOurs(string? text, string? file)
    {
        bool cleared = false;
        OnUi(() =>
        {
            try
            {
                bool ours = text != null && WinForms.Clipboard.ContainsText() && WinForms.Clipboard.GetText() == text;
                if (!ours && file != null && WinForms.Clipboard.ContainsFileDropList())
                    ours = WinForms.Clipboard.GetFileDropList().Cast<string>().Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
                if (ours) { Retry(WinForms.Clipboard.Clear); cleared = true; }
            }
            catch (Exception e) { Log.Error("clipboard clear", e); }
        });
        return cleared;
    }

    public static void MediaToClipboard(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        OnUi(() =>
        {
            if (ext is ".png" or ".jpg" or ".jpeg") SetImage(file);
            else SetFile(file);
        });
        Log.Info("clipboard: media " + file);
    }
}
