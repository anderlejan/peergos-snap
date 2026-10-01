using System.Runtime.InteropServices;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>Deletes files into the Windows Recycle Bin, so they can be restored.</summary>
public static class Recycle
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    /// <summary>Deletes the files for good (<paramref name="permanently"/>) or into the Recycle Bin; returns the ones
    /// that are still there.</summary>
    public static List<string> Remove(IEnumerable<string> files, bool permanently)
    {
        if (!permanently) return Delete(files);
        var failed = new List<string>();
        foreach (var f in files)
        {
            try
            {
                if (File.Exists(f)) File.Delete(f);
                Log.Info("deleted " + f);
            }
            catch (Exception e)
            {
                failed.Add(f);
                Log.Error("delete " + f, e);
            }
        }
        return failed;
    }

    /// <summary>Moves the files to the Recycle Bin; returns the ones that could not be moved.</summary>
    public static List<string> Delete(IEnumerable<string> files)
    {
        var failed = new List<string>();
        foreach (var f in files)
        {
            if (!File.Exists(f)) continue;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = Path.GetFullPath(f) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            int rc = SHFileOperation(ref op);
            if (rc != 0 || op.fAnyOperationsAborted != 0 || File.Exists(f))
            {
                failed.Add(f);
                Log.Error($"recycle {f}: code {rc}");
            }
            else Log.Info("recycled " + f);
        }
        return failed;
    }
}
