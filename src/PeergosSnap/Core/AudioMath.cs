using System.Globalization;

namespace PeergosSnap.Core;

/// <summary>Pure helpers for the system sound of videos (unit tested).</summary>
public static class AudioMath
{
    /// <summary>Below this peak (about −60 dBFS) a recording counts as silent and the video gets no sound track.</summary>
    public const float SilenceThreshold = 0.001f;

    /// <summary>
    /// Windows delivers no loopback data while nothing plays. To keep the sound in step with the picture, the gap is
    /// filled with silence: the frames needed so that <paramref name="written"/> reaches the position of
    /// <paramref name="elapsed"/>, or 0 when the difference is within <paramref name="toleranceSeconds"/> (normal jitter).
    /// </summary>
    public static long PaddingFrames(TimeSpan elapsed, long written, int sampleRate, double toleranceSeconds = 0.06)
    {
        if (elapsed <= TimeSpan.Zero || sampleRate <= 0) return 0;
        long expected = (long)Math.Round(elapsed.TotalSeconds * sampleRate);
        long behind = expected - written;
        return behind > toleranceSeconds * sampleRate ? behind : 0;
    }

    /// <summary>The loudest sample (0…1) of a block of audio in the given sample format.</summary>
    public static float Peak(ReadOnlySpan<byte> data, bool isFloat, int bitsPerSample)
    {
        float peak = 0;
        if (isFloat && bitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                var v = Math.Abs(BitConverter.ToSingle(data.Slice(i, 4)));
                if (v > peak && !float.IsNaN(v)) peak = v;
            }
        }
        else if (bitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= data.Length; i += 2)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data.Slice(i, 2)) / 32768f));
        }
        else if (bitsPerSample == 24)
        {
            for (int i = 0; i + 3 <= data.Length; i += 3)
            {
                int v = (data[i] | data[i + 1] << 8 | (sbyte)data[i + 2] << 16);
                peak = Math.Max(peak, Math.Abs(v / 8388608f));
            }
        }
        else if (bitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= data.Length; i += 4)
                peak = Math.Max(peak, Math.Abs(BitConverter.ToInt32(data.Slice(i, 4)) / 2147483648f));
        }
        return Math.Min(peak, 1f);
    }

    /// <summary>FFmpeg's line when it starts grabbing (with "-loglevel level+info"): the moment the video begins.</summary>
    public static bool IsVideoStartLine(string? line) => line != null && line.Contains("Press [q] to stop", StringComparison.Ordinal);

    /// <summary>Only warnings and errors of FFmpeg are worth reporting (info lines are not problems).</summary>
    public static bool IsProblemLine(string? line) =>
        !string.IsNullOrWhiteSpace(line) && !line.Contains("[info]", StringComparison.Ordinal) && !line.Contains("[verbose]", StringComparison.Ordinal);

    /// <summary>
    /// Arguments that add a recorded sound file to a video without re-encoding the picture. The first
    /// <paramref name="skipSeconds"/> of the sound (captured before the video started) are left out; the result is as
    /// long as the video. MP4 gets AAC, WebM gets Opus.
    /// </summary>
    public static List<string> MuxArgs(string video, string wav, double skipSeconds, string format, string output)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-i", video };
        if (skipSeconds > 0.0005) a.AddRange(["-ss", skipSeconds.ToString("0.000", CultureInfo.InvariantCulture)]);
        a.AddRange(["-i", wav, "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy"]);
        if (format == "webm") a.AddRange(["-c:a", "libopus", "-b:a", "128k", "-ar", "48000"]);
        else a.AddRange(["-c:a", "aac", "-b:a", "160k"]);
        // apad + shortest: the sound is padded with silence and cut where the picture ends, so the video keeps its length.
        a.AddRange(["-af", "apad", "-ac", "2", "-shortest"]);
        if (format == "mp4") a.AddRange(["-movflags", "+faststart"]);
        a.Add(output);
        return a;
    }
}
