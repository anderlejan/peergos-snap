using PeergosSnap.Services;

namespace PeergosSnap;

/// <summary>
/// The start: the tray app (WPF). Started by Explorer through the menu package it is instead the handler of Windows
/// 11's right-click menu, and started by the installer it registers or removes that package – both without a window.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(ExplorerCommandServer.Argument, StringComparer.OrdinalIgnoreCase)) return ExplorerCommandServer.Run();
        if (args.Length >= 2 && args[0] == ExplorerPackage.Argument) return ExplorerPackage.Command(args[1], args.Length > 2 ? args[2] : null);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
