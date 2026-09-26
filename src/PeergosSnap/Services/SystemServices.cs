using System.IO.Pipes;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>Global hotkeys through RegisterHotKey on a message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    readonly HwndSource source;
    readonly Dictionary<int, Action> actions = [];
    int nextId = 1;

    public HotkeyManager()
    {
        source = new HwndSource(new HwndSourceParameters("PeergosSnapHotkeys") { ParentWindow = new IntPtr(-3) });
        source.AddHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            a();
        }
        return IntPtr.Zero;
    }

    public void Clear()
    {
        foreach (var id in actions.Keys) Native.UnregisterHotKey(source.Handle, id);
        actions.Clear();
    }

    /// <summary>Registers a shortcut; returns an error text, or null when it worked (or is empty).</summary>
    public string? Register(string? text, Action action)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!Hotkey.TryParse(text, out var hk)) return "Not a valid shortcut";
        Key key;
        try { key = (Key)new KeyConverter().ConvertFromInvariantString(hk.Key)!; }
        catch { return "Unknown key " + hk.Key; }
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        int id = nextId++;
        if (!Native.RegisterHotKey(source.Handle, id, hk.Modifiers | Hotkey.MOD_NOREPEAT, vk))
            return "Already used by another program";
        actions[id] = action;
        return null;
    }

    public void Dispose()
    {
        Clear();
        source.Dispose();
    }
}

/// <summary>One running copy. Later starts pass their command ("--quit", "--picture", …) to the running copy.</summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = "PeergosSnap.SingleInstance.v1";
    static string PipeName => "PeergosSnap.Cmd." + Environment.UserName;
    readonly Mutex mutex;
    readonly CancellationTokenSource cts = new();
    public bool IsFirst { get; }

    public SingleInstance()
    {
        mutex = new Mutex(true, MutexName, out var created);
        IsFirst = created;
    }

    public void Listen(Action<string> onCommand)
    {
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cts.Token);
                    using var r = new StreamReader(server);
                    var cmd = (await r.ReadLineAsync())?.Trim() ?? "";
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => onCommand(cmd));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Error("pipe", e); await Task.Delay(500); }
            }
        });
    }

    public static bool Send(string cmd)
    {
        try
        {
            // We were started by the user, so we may hand the foreground right to the running copy
            // (its overlay / windows can then take the keyboard focus).
            Native.AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            using var w = new StreamWriter(client);
            w.WriteLine(cmd);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { mutex.ReleaseMutex(); } catch { }
        mutex.Dispose();
    }
}

/// <summary>Start with Windows through HKCU\...\Run (the installer's option writes the same value).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "PeergosSnap";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(Name, false);
    }
}
