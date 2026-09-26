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
        var cmd = e.Args.FirstOrDefault(a => a.StartsWith("--")) ?? "";

        if (cmd == "--selftest")
        {
            // Off the UI thread: the self-test waits on async code that must not need the dispatcher.
            Shutdown(Task.Run(() => SelfTest.Run(e.Args)).GetAwaiter().GetResult());
            return;
        }

        instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // Pass the command to the running copy ("--quit", "--picture", "--video", "--settings", "--notes").
            SingleInstance.Send(cmd == "" || cmd == "--tray" ? "--settings" : cmd);
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
        if (cmd is "--picture" or "--video" or "--settings" or "--notes") tray.Command(cmd);
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
