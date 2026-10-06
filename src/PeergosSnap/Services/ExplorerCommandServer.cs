using System.Runtime.InteropServices;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Peergos Snap at the top of Windows 11's right-click menu. Windows 11 shows only packaged menu handlers there (the
/// classic registry entries go under "Show more options"), so a small package (<see cref="ExplorerPackage"/>) names
/// this program as the handler: Explorer starts "PeergosSnap.exe --explorer-com" and asks it for the entry and its
/// submenu. It runs outside Explorer and shows no window. A click hands the chosen files and folders to the running
/// Peergos Snap exactly like the classic menu, so the same question comes before anything leaves this PC. It ends two
/// minutes after Explorer's last question (the next menu starts it again).
/// </summary>
public static class ExplorerCommandServer
{
    public const string Argument = "--explorer-com";
    public static readonly Guid Clsid = new("9faba0ae-a097-4fc9-b920-480ca4a392c6");
    static long lastCall = Environment.TickCount64;

    static void Touch() => Interlocked.Exchange(ref lastCall, Environment.TickCount64);

    public static int Run()
    {
        int code = 0;
        // COM calls arrive on its own threads (multi-threaded apartment); the start thread only waits.
        var t = new Thread(() => code = Serve());
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return code;
    }

    static int Serve()
    {
        var clsid = Clsid;
        int hr = CoRegisterClassObject(ref clsid, new Factory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out uint cookie);
        if (hr < 0)
        {
            Log.Error($"explorer menu: the handler could not register (0x{hr:X8})");
            return 1;
        }
        try
        {
            while (Environment.TickCount64 - Interlocked.Read(ref lastCall) < 120_000) Thread.Sleep(1000);
        }
        finally { CoRevokeClassObject(cookie); }
        return 0;
    }

    /// <summary>Shown only while Explorer's menu is on and wanted at the top (Settings → General); otherwise the
    /// classic entries are there instead. Read again at most every two seconds.</summary>
    static bool Visible()
    {
        lock (VisibleGate)
        {
            if (Environment.TickCount64 - visibleAt > 2000)
            {
                try
                {
                    var s = Settings.Load(AppPaths.SettingsFile);
                    visible = s.ExplorerMenu && s.ExplorerMenuTop;
                }
                catch { visible = false; }
                visibleAt = Environment.TickCount64;
            }
            return visible;
        }
    }

    static readonly object VisibleGate = new();
    static bool visible;
    static long visibleAt = long.MinValue / 2;

    /// <summary>The paths of the files and folders the menu is for (other shell items, e.g. "This PC", have none).</summary>
    static List<string> Paths(IShellItemArray? items)
    {
        var list = new List<string>();
        if (items == null) return list;
        items.GetCount(out uint n);
        for (uint i = 0; i < n; i++)
        {
            items.GetItemAt(i, out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var p);
                var path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(path)) list.Add(path);
            }
            catch (COMException) { }
            finally { Marshal.ReleaseComObject(item); }
        }
        return list;
    }

    /// <summary>To the running Peergos Snap, one "command|path" line each (as from the classic menu); when it is not
    /// running it is started with the first, and gets the others once it listens.</summary>
    static void Hand(string command, List<string> paths)
    {
        if (paths.Count == 0) return;
        if (SingleInstance.IsRunning() && SingleInstance.Send(command + "|" + paths[0]))
        {
            for (int i = 1; i < paths.Count; i++) SingleInstance.Send(command + "|" + paths[i]);
            return;
        }
        StartOutsidePackage(Environment.ProcessPath ?? Path.Combine(AppPaths.AppDir, "PeergosSnap.exe"), $"{command} \"{paths[0]}\"");
        var until = DateTime.Now.AddSeconds(20);
        for (int i = 1; i < paths.Count; i++)
            while (!SingleInstance.Send(command + "|" + paths[i]) && DateTime.Now < until) Thread.Sleep(300);
    }

    /// <summary>Starts Peergos Snap as an ordinary program. This handler runs with the menu package's identity, which a
    /// plain start would hand on: the tray app would then belong to the package (and end whenever it is updated). So
    /// it starts as a child of Windows' shell (Explorer), like a program started from the Start menu.</summary>
    static void StartOutsidePackage(string exe, string arguments)
    {
        GetWindowThreadProcessId(GetShellWindow(), out int shellPid);
        var parent = shellPid > 0 ? OpenProcess(PROCESS_CREATE_PROCESS, false, shellPid) : IntPtr.Zero;
        if (parent != IntPtr.Zero)
        {
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            var list = Marshal.AllocHGlobal(size);
            var value = Marshal.AllocHGlobal(IntPtr.Size);
            bool ready = false;
            try
            {
                ready = InitializeProcThreadAttributeList(list, 1, 0, ref size);
                Marshal.WriteIntPtr(value, parent);
                if (ready && UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, value, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    var si = new STARTUPINFOEX { lpAttributeList = list };
                    si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    var cmd = new StringBuilder($"\"{exe}\" {arguments}");
                    if (CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
                            Path.GetDirectoryName(exe), ref si, out var pi))
                    {
                        CloseHandle(pi.hProcess);
                        CloseHandle(pi.hThread);
                        return;
                    }
                }
                Log.Error($"explorer menu: starting Peergos Snap under the shell failed ({Marshal.GetLastWin32Error()})");
            }
            finally
            {
                if (ready) DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(value);
                Marshal.FreeHGlobal(list);
                CloseHandle(parent);
            }
        }
        // Not possible (no shell running): an ordinary start.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = false });
    }

    // ---------- the COM objects ----------

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Factory : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            Touch();
            if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
            var unk = Marshal.GetIUnknownForObject(new Command(null));
            try { return Marshal.QueryInterface(unk, ref riid, out ppv); }
            finally { Marshal.Release(unk); }
        }

        public int LockServer(bool fLock)
        {
            Touch();
            return 0;
        }
    }

    /// <summary>"Peergos Snap" (with <paramref name="command"/> null) and its two entries: upload, send to a friend.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Command(string? command) : IExplorerCommand
    {
        public int GetTitle(IShellItemArray? items, out IntPtr name)
        {
            Touch();
            name = Marshal.StringToCoTaskMemUni(command switch
            {
                null => "Peergos Snap",
                ExplorerCommand.Upload => FolderFirst(items) ? "Upload the folder to Peergos and copy the link" : "Upload to Peergos and copy the link",
                _ => FolderFirst(items) ? "Send the folder to a friend…" : "Send to a friend…",
            });
            return S_OK;
        }

        public int GetIcon(IShellItemArray? items, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            if (command != null) return E_NOTIMPL;
            icon = Marshal.StringToCoTaskMemUni((Environment.ProcessPath ?? "") + ",0");
            return S_OK;
        }

        public int GetToolTip(IShellItemArray? items, out IntPtr tip)
        {
            tip = IntPtr.Zero;
            return E_NOTIMPL;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = Guid.Empty;
            return E_NOTIMPL;
        }

        public int GetState(IShellItemArray? items, bool okToBeSlow, out uint state)
        {
            Touch();
            state = Visible() ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        }

        public int Invoke(IShellItemArray? items, IntPtr bindCtx)
        {
            Touch();
            if (command == null) return S_OK;
            try { Hand(command, Paths(items)); }
            catch (Exception e) { Log.Error("explorer menu: " + command, e); }
            return S_OK;
        }

        public int GetFlags(out uint flags)
        {
            flags = command == null ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
            return S_OK;
        }

        public int EnumSubCommands(out IEnumExplorerCommand? commands)
        {
            Touch();
            commands = command == null ? new Commands([new Command(ExplorerCommand.Upload), new Command(ExplorerCommand.Send)]) : null;
            return command == null ? S_OK : E_NOTIMPL;
        }

        static bool FolderFirst(IShellItemArray? items)
        {
            try { return Paths(items) is [var first, ..] && Directory.Exists(first); }
            catch { return false; }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Commands(Command[] all) : IEnumExplorerCommand
    {
        int at;

        public int Next(uint count, IntPtr commands, IntPtr fetched)
        {
            uint n = 0;
            for (; n < count && at < all.Length; n++, at++)
                Marshal.WriteIntPtr(commands, (int)n * IntPtr.Size, Marshal.GetComInterfaceForObject(all[at], typeof(IExplorerCommand)));
            if (fetched != IntPtr.Zero) Marshal.WriteInt32(fetched, (int)n);
            return n == count ? S_OK : S_FALSE;
        }

        public int Skip(uint count)
        {
            at = (int)Math.Min(all.Length, at + count);
            return at < all.Length ? S_OK : S_FALSE;
        }

        public int Reset()
        {
            at = 0;
            return S_OK;
        }

        public int Clone(out IEnumExplorerCommand? copy)
        {
            copy = new Commands(all) { at = at };
            return S_OK;
        }
    }

    // ---------- Windows ----------

    const int S_OK = 0, S_FALSE = 1, E_NOTIMPL = unchecked((int)0x80004001), CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    const uint CLSCTX_LOCAL_SERVER = 4, REGCLS_MULTIPLEUSE = 1;
    const uint ECS_ENABLED = 0, ECS_HIDDEN = 2, ECF_DEFAULT = 0, ECF_HASSUBCOMMANDS = 1;
    const uint SIGDN_FILESYSPATH = 0x80058000;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, PROCESS_CREATE_PROCESS = 0x0080;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;

    [DllImport("ole32.dll")]
    static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    static extern int CoRevokeClassObject(uint cookie);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("user32.dll")]
    static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? items, out IntPtr name);
    [PreserveSig] int GetIcon(IShellItemArray? items, out IntPtr icon);
    [PreserveSig] int GetToolTip(IShellItemArray? items, out IntPtr tip);
    [PreserveSig] int GetCanonicalName(out Guid name);
    [PreserveSig] int GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
    [PreserveSig] int Invoke(IShellItemArray? items, IntPtr bindCtx);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand? commands);
}

[ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint count, IntPtr commands, IntPtr fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumExplorerCommand? copy);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    void GetAttributes(int attribFlags, uint mask, out uint attribs);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out IntPtr items);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(IntPtr bindCtx, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem parent);
    void GetDisplayName(uint sigdn, out IntPtr name);
    void GetAttributes(uint mask, out uint attribs);
    void Compare(IShellItem other, uint hint, out int order);
}
