using System.Windows;
using PeergosSnap.Core;
using PeergosSnap.Services;

namespace PeergosSnap;

public partial class App : Application
{
    SingleInstance? instance;
    TrayController? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // An item from Explorer's right-click menu comes as "--explorer-send <path>" and travels as one line "command|path".
        var cmd = ExplorerCommand.FromArgs(e.Args) ?? e.Args.FirstOrDefault(a => a.StartsWith("--")) ?? "";
        // Testing only: PEERGOS_SNAP_DATA gives this copy its own data folders and its own single-instance lock,
        // so it can run next to the installed app without touching the user's settings, notes or captures.
        var sandbox = Environment.GetEnvironmentVariable("PEERGOS_SNAP_DATA");
        if (!string.IsNullOrWhiteSpace(sandbox))
        {
            AppPaths.Override(Path.Combine(sandbox, "data"), Path.Combine(sandbox, "local"));
            SingleInstance.Suffix = "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sandbox.ToLowerInvariant())))[..12];
        }

        if (cmd == "--selftest")
        {
            // Off the UI thread: the self-test waits on async code that must not need the dispatcher.
            Shutdown(Task.Run(() => SelfTest.Run(e.Args)).GetAwaiter().GetResult());
            return;
        }

        instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // Pass the command to the running copy ("--quit", "--picture", "--video", "--settings", "--notes", "--help", "--history").
            if (ExplorerCommand.Parse(cmd) != null)
            {
                // Explorer starts one copy per selected item, perhaps while the first is still starting: keep trying a while.
                var until = DateTime.Now.AddSeconds(20);
                while (!SingleInstance.Send(cmd) && DateTime.Now < until) Thread.Sleep(300);
            }
            else SingleInstance.Send(cmd == "" || cmd == "--tray" ? "--settings" : cmd);
            instance.Dispose();
            instance = null;
            Shutdown();
            return;
        }
        if (cmd == "--quit") { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("unhandled", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("fatal", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };

        Log.Info($"start {typeof(App).Assembly.GetName().Version} args=[{string.Join(" ", e.Args)}]");
        tray = new TrayController();
        instance.Listen(tray.Command);
        if (cmd is "--picture" or "--video" or "--settings" or "--notes" or "--help" or "--history" || ExplorerCommand.Parse(cmd) != null) tray.Command(cmd);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Give the User notes page time for its delayed save.
        if (tray?.NotesBusy == true) Thread.Sleep(600);
        tray?.Dispose();
        instance?.Dispose();
        Log.Info("exit");
        base.OnExit(e);
    }
}
