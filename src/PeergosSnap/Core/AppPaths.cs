namespace PeergosSnap.Core;

/// <summary>Where the app keeps its data and finds its bundled tools.</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeergosSnap");

    public static string LocalDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeergosSnap");

    /// <summary>Tests and self-tests use their own folders.</summary>
    public static void Override(string dataDir, string localDir)
    {
        DataDir = dataDir;
        LocalDir = localDir;
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string NotesFile => Path.Combine(DataDir, "user-notes.json");
    public static string PromptsDir => Path.Combine(DataDir, "prompts");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string ThumbsDir => Path.Combine(LocalDir, "thumbs");
    public static string CacheDir => Path.Combine(LocalDir, "Captures");
    public static string WorkDir => Path.Combine(LocalDir, "work");
    public static string LogFile => Path.Combine(LocalDir, "logs", "peergos-snap.log");
    public static string WebViewDir => Path.Combine(LocalDir, "WebView2");

    public static string AppDir => AppContext.BaseDirectory;
    public static string FfmpegExe => Path.Combine(AppDir, "tools", "ffmpeg", "ffmpeg.exe");
    public static string JavaExe => Path.Combine(AppDir, "runtime", "bin", "java.exe");
    public static string BridgeDir => Path.Combine(AppDir, "bridge");
}

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : msg + ": " + e);

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                var f = AppPaths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                if (File.Exists(f) && new FileInfo(f).Length > 2_000_000)
                    File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
