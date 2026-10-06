using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// The small package that puts Peergos Snap at the top of Windows 11's right-click menu: a package "with an external
/// location" – it holds no program, only a description naming PeergosSnap.exe in the installation folder as the
/// menu's handler (<see cref="ExplorerCommandServer"/>). It is unsigned and marked as such (Windows then never takes
/// it for a signed package), which Windows accepts from an administrator: the installer registers it when Peergos Snap
/// is installed for all users, Settings → General can do it later (one administrator prompt), uninstalling removes it.
/// It is made on the spot with Windows' own packaging API – no developer tools needed.
/// </summary>
public static class ExplorerPackage
{
    public const string Argument = "--explorer-package";
    public const string Name = "PeergosSnap.ExplorerMenu";
    /// <summary>The OID marks an unsigned package; Windows registers no unsigned package without it.</summary>
    public const string Publisher = "CN=Peergos Snap, OID.2.25.311729368913984317654407730594956997722=1";
    /// <summary>Changes only when the description below does (not with the app's version).</summary>
    public const string Version = "1.0.0.0";

    /// <summary>Windows 11: the only Windows with the new menu.</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static string Manifest()
    {
        var clsid = ExplorerCommandServer.Clsid.ToString().ToUpperInvariant();
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
              xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
              xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10"
              xmlns:desktop4="http://schemas.microsoft.com/appx/manifest/desktop/windows10/4"
              xmlns:desktop5="http://schemas.microsoft.com/appx/manifest/desktop/windows10/5"
              xmlns:com="http://schemas.microsoft.com/appx/manifest/com/windows10"
              xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
              IgnorableNamespaces="uap uap10 desktop4 desktop5 com rescap">
              <Identity Name="{Name}" Publisher="{Publisher}" Version="{Version}" ProcessorArchitecture="x64"/>
              <Properties>
                <DisplayName>Peergos Snap in Explorer's menu</DisplayName>
                <PublisherDisplayName>Peergos Snap</PublisherDisplayName>
                <Logo>Assets\Logo.png</Logo>
                <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
              </Properties>
              <Resources>
                <Resource Language="en-us"/>
              </Resources>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26200.0"/>
              </Dependencies>
              <Capabilities>
                <rescap:Capability Name="runFullTrust"/>
              </Capabilities>
              <Applications>
                <Application Id="PeergosSnap" Executable="PeergosSnap.exe" uap10:TrustLevel="mediumIL" uap10:RuntimeBehavior="win32App">
                  <uap:VisualElements AppListEntry="none" DisplayName="Peergos Snap" Description="Peergos Snap in Explorer's right-click menu"
                    BackgroundColor="transparent" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png"/>
                  <Extensions>
                    <desktop4:Extension Category="windows.fileExplorerContextMenus">
                      <desktop4:FileExplorerContextMenus>
                        <desktop5:ItemType Type="*">
                          <desktop5:Verb Id="PeergosSnap" Clsid="{clsid}"/>
                        </desktop5:ItemType>
                        <desktop5:ItemType Type="Directory">
                          <desktop5:Verb Id="PeergosSnap" Clsid="{clsid}"/>
                        </desktop5:ItemType>
                      </desktop4:FileExplorerContextMenus>
                    </desktop4:Extension>
                    <com:Extension Category="windows.comServer">
                      <com:ComServer>
                        <com:ExeServer Executable="PeergosSnap.exe" Arguments="{ExplorerCommandServer.Argument}" DisplayName="Peergos Snap menu">
                          <com:Class Id="{clsid}" DisplayName="Peergos Snap"/>
                        </com:ExeServer>
                      </com:ComServer>
                    </com:Extension>
                  </Extensions>
                </Application>
              </Applications>
            </Package>
            """;
    }

    /// <summary>"--explorer-package install | remove | make FILE" (the installer, Settings, the build's self-test).
    /// Returns the exit code; what happened goes to the log.</summary>
    public static int Command(string what, string? file = null)
    {
        try
        {
            switch (what)
            {
                case "install": return Install() is { } e ? Fail(e) : 0;
                case "remove": return Remove() is { } e2 ? Fail(e2) : 0;
                case "make" when file != null: Make(file); return 0;
                default: return 2;
            }
        }
        catch (Exception e) { return Fail(e.Message); }

        static int Fail(string why)
        {
            Log.Error("explorer menu package: " + why);
            return 1;
        }
    }

    /// <summary>Makes the package and registers it with this program's folder as its external location. Null when it
    /// worked (or was registered already), else why not.</summary>
    public static string? Install()
    {
        if (!Supported) return "Windows 11 is needed";
        var dir = AppPaths.AppDir.TrimEnd('\\');
        // Registered for this folder already (an update): nothing to do. Registered for another one (a copy elsewhere):
        // removed first – a package keeps the folder it was registered with.
        if (Registered())
        {
            if (string.Equals(RegisteredFor(), dir, StringComparison.OrdinalIgnoreCase)) { Log.Info("explorer menu package: already registered"); return null; }
            if (Remove() is { } e) return e;
        }
        var msix = Path.Combine(Path.GetTempPath(), $"PeergosSnap-ExplorerMenu-{Guid.NewGuid():N}.msix");
        try
        {
            Make(msix);
            var (code, output) = PowerShell($"Add-AppxPackage -Path '{Quote(msix)}' -ExternalLocation '{Quote(dir)}' -AllowUnsigned");
            if (code != 0 || !Registered()) return "registering failed: " + output.Trim();
            try
            {
                Directory.CreateDirectory(AppPaths.LocalDir);
                File.WriteAllText(MarkerFile, dir);
            }
            catch { }
            Log.Info($"explorer menu package registered ({dir})");
            return null;
        }
        finally { try { File.Delete(msix); } catch { } }
    }

    /// <summary>The folder the package was registered with (noted when registering it), or "".</summary>
    static string RegisteredFor()
    {
        try { return File.Exists(MarkerFile) ? File.ReadAllText(MarkerFile).Trim() : ""; }
        catch { return ""; }
    }

    static string MarkerFile => Path.Combine(AppPaths.LocalDir, "explorer-menu-package.txt");

    /// <summary>Removes the package: for every user when run as administrator (the uninstaller), else for this user.</summary>
    public static string? Remove()
    {
        if (!Supported) return null;
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        var (code, output) = PowerShell(admin
            ? $"Get-AppxPackage -AllUsers -Name {Name} | Remove-AppxPackage -AllUsers"
            : $"Get-AppxPackage -Name {Name} | Remove-AppxPackage");
        if (code != 0) return "removing failed: " + output.Trim();
        try { File.Delete(MarkerFile); } catch { }
        Log.Info("explorer menu package removed");
        return null;
    }

    /// <summary>Whether this version of the package is registered for this Windows user (a quick look, no PowerShell).</summary>
    public static bool Registered()
    {
        if (!Supported) return false;
        try
        {
            var id = new PACKAGE_ID { name = Name, publisher = Publisher, processorArchitecture = 9 /* x64 */ };
            uint length = 130;
            var family = new StringBuilder((int)length);
            if (PackageFamilyNameFromId(ref id, ref length, family) != 0) return false;
            uint count = 0, chars = 0;
            if (GetPackagesByPackageFamily(family.ToString(), ref count, IntPtr.Zero, ref chars, IntPtr.Zero) != ERROR_INSUFFICIENT_BUFFER || count == 0)
                return false;
            var names = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal((int)chars * 2);
            try
            {
                if (GetPackagesByPackageFamily(family.ToString(), ref count, names, ref chars, buffer) != 0) return false;
                // Full names are NAME_VERSION_ARCHITECTURE__PUBLISHERID.
                for (int i = 0; i < count; i++)
                    if (Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size))?.StartsWith($"{Name}_{Version}_", StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(names);
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Writes the package (the description and its logo) with Windows' packaging API, which also checks the
    /// description against Windows' rules.</summary>
    public static void Make(string msix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(msix))!);
        var factory = (IAppxFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781"))!)!;
        Marshal.ThrowExceptionForHR(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, UIntPtr.Zero, out var hash));
        var output = SHCreateStreamOnFileEx(msix, STGM_CREATE | STGM_WRITE | STGM_SHARE_EXCLUSIVE, FILE_ATTRIBUTE_NORMAL, true, IntPtr.Zero);
        try
        {
            var settings = new APPX_PACKAGE_SETTINGS { forceZip32 = 1, hashMethod = hash };
            var writer = factory.CreatePackageWriter(output, ref settings);
            var logo = Logo();
            writer.AddPayloadFile(@"Assets\Logo.png", "image/png", 0 /* no compression */, SHCreateMemStream(logo, (uint)logo.Length));
            var manifest = Encoding.UTF8.GetBytes(Manifest());
            writer.Close(SHCreateMemStream(manifest, (uint)manifest.Length));
            Marshal.ReleaseComObject(writer);
        }
        finally
        {
            Marshal.ReleaseComObject(output);
            Marshal.Release(hash);
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>The app's tray icon as the package's logo (150 × 150).</summary>
    static byte[] Logo()
    {
        using var icon = TrayController.MakeIcon(System.Drawing.Color.FromArgb(43, 138, 110), null, 150);
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    static string Quote(string s) => s.Replace("'", "''");

    static (int Code, string Output) PowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command + "; exit $(if ($?) { 0 } else { 1 })" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outText = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (outText + "\n" + err.Result).Trim());
    }

    // ---------- Windows ----------

    const uint STGM_CREATE = 0x1000, STGM_WRITE = 0x1, STGM_SHARE_EXCLUSIVE = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
    const int ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    struct APPX_PACKAGE_SETTINGS
    {
        public int forceZip32;
        public IntPtr hashMethod;
    }

    [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAppxFactory
    {
        IAppxPackageWriter CreatePackageWriter(IStream outputStream, ref APPX_PACKAGE_SETTINGS settings);
    }

    [ComImport, Guid("9099e33b-246f-41e4-881a-008eb613f858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAppxPackageWriter
    {
        void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.LPWStr)] string contentType,
            int compressionOption, IStream inputStream);
        void Close(IStream manifest);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PACKAGE_ID
    {
        public uint reserved;
        public uint processorArchitecture;
        public ulong version;
        public string name;
        public string publisher;
        public string? resourceId;
        public string? publisherId;
    }

    [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
    static extern int CreateUri(string uri, uint flags, UIntPtr reserved, out IntPtr result);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern IStream SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template);

    [DllImport("shlwapi.dll")]
    static extern IStream SHCreateMemStream(byte[] data, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int PackageFamilyNameFromId(ref PACKAGE_ID id, ref uint length, StringBuilder familyName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr fullNames, ref uint bufferLength, IntPtr buffer);
}
