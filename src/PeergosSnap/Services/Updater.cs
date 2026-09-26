using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string SetupName, string SetupUrl, string SumsUrl, string PageUrl);

/// <summary>
/// Checks the project's latest GitHub release and installs it: downloads the installer, checks it against the
/// release's SHA256 list, runs it silently and quits; the installer starts the new version when it is done.
/// </summary>
public static class Updater
{
    const string LatestRelease = "https://api.github.com/repos/anderlejan/peergos-snap/releases/latest";

    /// <summary>PEERGOS_SNAP_UPDATE_URL (testing only) points the check at another release description.</summary>
    static string ReleaseUrl => Environment.GetEnvironmentVariable("PEERGOS_SNAP_UPDATE_URL") is { Length: > 0 } u ? u : LatestRelease;

    public static Version Current
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PeergosSnap", Current.ToString()));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    /// <summary>"v2.1.0" / "2.1.0" → 2.1.0; null when it is not a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Reads a GitHub "release" JSON; returns the update when it is newer than <paramref name="current"/>
    /// and has both the installer and its checksum list. Drafts and pre-releases are ignored.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (r.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) return null;
        var v = ParseVersion(r.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
        if (v == null || v <= current) return null;
        string setupName = $"PeergosSnap-Setup-{v}.exe", sumsName = $"SHA256SUMS-{v}.txt";
        string? setup = null, sums = null;
        if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() : null;
                if (name == setupName) setup = url;
                if (name == sumsName) sums = url;
            }
        if (setup == null || sums == null) return null;
        var page = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new UpdateInfo(v, setupName, setup, sums, page);
    }

    /// <summary>The expected SHA256 (lower case) of <paramref name="fileName"/> in a "hash  name" list.</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var sp = line.IndexOf(' ');
            if (sp != 64) continue;
            var name = line[sp..].Trim().TrimStart('*');
            if (name == fileName) return line[..64].ToLowerInvariant();
        }
        return null;
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ReleaseUrl, ct);
        var info = ParseRelease(json, Current);
        Log.Info($"update check: current {Current}, found {info?.Version.ToString() ?? "nothing newer"}");
        return info;
    }

    /// <summary>Downloads the installer next to the app data and verifies its SHA256; returns the file.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo u, Action<int>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppPaths.LocalDir, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }
        var sums = await Http.GetStringAsync(u.SumsUrl, ct);
        var expected = ExpectedHash(sums, u.SetupName) ?? throw new InvalidOperationException("The release has no checksum for " + u.SetupName);

        var file = Path.Combine(dir, u.SetupName);
        using (var resp = await Http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
            int last = -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file + ".part");
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                var pct = total > 0 ? (int)(done * 100 / total) : -1;
                if (pct != last) { last = pct; progress?.Invoke(pct); }
            }
        }
        string actual;
        await using (var fs = File.OpenRead(file + ".part"))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
        if (actual != expected)
        {
            File.Delete(file + ".part");
            throw new InvalidOperationException("The downloaded installer does not match its checksum; nothing was installed");
        }
        File.Move(file + ".part", file, true);
        Log.Info($"update {u.Version} downloaded and verified: {file}");
        return file;
    }

    /// <summary>Runs the verified installer silently; the caller quits right after. The installer closes any running
    /// copy, keeps the options chosen at the first install and starts the new version when done.</summary>
    public static void StartInstaller(string setup)
    {
        var log = Path.Combine(AppPaths.LocalDir, "updates", "install.log");
        Process.Start(new ProcessStartInfo(setup)
        {
            UseShellExecute = false,
            Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{log}\"",
        });
        Log.Info("update installer started: " + setup);
    }
}
