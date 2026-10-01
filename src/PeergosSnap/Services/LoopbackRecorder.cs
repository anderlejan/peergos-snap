using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PeergosSnap.Core;

namespace PeergosSnap.Services;

/// <summary>
/// Records what Windows plays (WASAPI loopback of the default output device) into a WAV file, for one video segment.
/// Windows sends no data while nothing plays, so gaps are filled with silence by the clock: the file always runs in
/// step with the recording, from <see cref="Start"/> to <see cref="Stop"/>.
/// </summary>
public sealed class LoopbackRecorder : IDisposable
{
    readonly WasapiLoopbackCapture capture;
    readonly WaveFileWriter writer;
    readonly Stopwatch clock = new();
    readonly object gate = new();
    readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly int rate, blockAlign;
    readonly bool isFloat;
    readonly int bits;
    long framesWritten;
    bool running, disposed;

    /// <summary>The loudest sample so far (0…1); below <see cref="AudioMath.SilenceThreshold"/> means silence.</summary>
    public float Peak { get; private set; }
    /// <summary>Time since <see cref="Start"/> (the reference for where the video began).</summary>
    public TimeSpan Elapsed => clock.Elapsed;
    public string File { get; }

    public LoopbackRecorder(string wavFile)
    {
        File = wavFile;
        capture = new WasapiLoopbackCapture(); // throws when there is no output device
        var f = capture.WaveFormat;
        rate = f.SampleRate;
        blockAlign = f.BlockAlign;
        bits = f.BitsPerSample;
        isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                  || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        writer = new WaveFileWriter(wavFile, f);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) Log.Error("sound capture stopped", e.Exception);
            stopped.TrySetResult();
        };
    }

    /// <summary>Starts capturing; call right before the video starts.</summary>
    public void Start()
    {
        lock (gate)
        {
            running = true;
            clock.Start();
        }
        capture.StartRecording();
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (gate)
        {
            if (!running || e.BytesRecorded <= 0) return;
            int frames = e.BytesRecorded / blockAlign;
            // This block ends now, so it began frames/rate ago: silence before that was not delivered by Windows.
            var blockStart = clock.Elapsed - TimeSpan.FromSeconds(frames / (double)rate);
            WriteSilence(AudioMath.PaddingFrames(blockStart, framesWritten, rate));
            writer.Write(e.Buffer, 0, frames * blockAlign);
            framesWritten += frames;
            var p = AudioMath.Peak(e.Buffer.AsSpan(0, frames * blockAlign), isFloat, bits);
            if (p > Peak) Peak = p;
        }
    }

    void WriteSilence(long frames)
    {
        if (frames <= 0) return;
        var zero = new byte[Math.Min(frames, rate) * blockAlign];
        for (long left = frames; left > 0;)
        {
            int n = (int)Math.Min(left, zero.Length / blockAlign);
            writer.Write(zero, 0, n * blockAlign);
            left -= n;
        }
        framesWritten += frames;
    }

    /// <summary>Stops, pads the file to the full length and closes it.</summary>
    public async Task StopAsync()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
            clock.Stop();
            WriteSilence(AudioMath.PaddingFrames(clock.Elapsed, framesWritten, rate, 0));
        }
        try
        {
            capture.StopRecording();
            await Task.WhenAny(stopped.Task, Task.Delay(3000)).ConfigureAwait(false);
        }
        catch (Exception e) { Log.Error("sound stop", e); }
        Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running = false;
            try { writer.Dispose(); } catch (Exception e) { Log.Error("sound file", e); }
        }
        try { capture.Dispose(); } catch { }
    }

    /// <summary>For the self-test: plays a quiet tone on the default output device for the given time.</summary>
    public static async Task PlayToneAsync(TimeSpan length, double frequency = 440)
    {
        using var output = new WasapiOut(AudioClientShareMode.Shared, 100);
        var tone = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2)
        {
            Type = NAudio.Wave.SampleProviders.SignalGeneratorType.Sin, Frequency = frequency, Gain = 0.2,
        };
        output.Init(tone);
        output.Play();
        await Task.Delay(length).ConfigureAwait(false);
        output.Stop();
    }
}
