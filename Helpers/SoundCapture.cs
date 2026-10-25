using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PCCast2TV.Helpers;

// Grabs whatever the PC is playing (WASAPI loopback) and hands it on as 16-bit stereo at a steady real-time pace.
// Windows sends nothing while the PC is silent, so the gaps get filled with silence to keep sound in step with the picture.
// When Windows switches speakers mid-cast (headphones turned on, say), the copy follows to the new ones.
public sealed class SoundCapture : IDisposable
{
    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    const int HandOverMs = 35, BlockAlign = 4; // 16-bit stereo

    WasapiRecorder _cap;
    MMDeviceEnumerator _devices;
    MMDeviceNotificationClient _watch;
    readonly ConcurrentQueue<byte[]> _queue = new();
    Thread _writer;
    readonly ManualResetEventSlim _clockGo = new(false);
    volatile bool _running, _reopen;
    Action<byte[], int> _out;

    // The cast's sound rate, 44.1 or 48 kHz, kept for the whole cast whatever speakers come later.
    public int Rate { get; private set; }

    // Opens the default speakers for loopback; false when there is no sound device to copy.
    public bool Open()
    {
        if (!OpenSpeakers()) return false;
        _devices = new MMDeviceEnumerator();
        _watch = _devices.CreateNotificationClient(false);
        _watch.DefaultDeviceChanged += (_, e) => { if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia) _reopen = true; };
        return true;
    }

    // quiet: a retry while there are no speakers, already logged once.
    bool OpenSpeakers(bool quiet = false)
    {
        WasapiRecorder cap;
        try { cap = new WasapiRecorderBuilder().WithLoopbackCapture().Build(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            if (!quiet) Log.Write("sound: no loopback device: " + ex.Message);
            return false;
        }
        var wf = cap.WaveFormat;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf is WaveFormatExtensible x && x.SubFormat == FloatSubFormat);
        if (isFloat ? wf.BitsPerSample != 32 : wf.BitsPerSample is not (16 or 24 or 32))
        {
            Log.Write($"sound: unsupported format {wf.Encoding} {wf.BitsPerSample}-bit");
            cap.Dispose();
            return false;
        }
        if (Rate == 0) Rate = wf.SampleRate is 44100 or 48000 ? wf.SampleRate : 48000;
        int speakers = wf is WaveFormatExtensible ext ? (int)ext.ChannelMask : 0;
        var conv = new SoundConverter(wf.SampleRate, wf.Channels, wf.BitsPerSample, isFloat, speakers, Rate);
        // A buffer Windows marks silent may hold junk, so it goes out as real zeros.
        cap.DataAvailable += (buffer, flags, _, _) =>
        {
            if (buffer.Length == 0) return;
            _queue.Enqueue(conv.ToStereo16(flags.HasFlag(AudioClientBufferFlags.Silent) ? new byte[buffer.Length] : buffer));
        };
        cap.RecordingStopped += (_, e) =>
        {
            if (e.Exception == null) return;
            Log.Write("sound: capture stopped: " + e.Exception.Message);
            _reopen = true; // the speakers went away or changed format; try them again
        };
        if (_running)
        {
            try { cap.StartRecording(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                if (!quiet) Log.Write("sound: the speakers would not start: " + ex.Message);
                cap.Dispose();
                return false;
            }
        }
        Log.Write($"sound: {(isFloat ? "float" : "pcm")} {wf.BitsPerSample}-bit {wf.SampleRate} Hz {wf.Channels} ch, sent as {Rate} Hz stereo");
        _cap = cap;
        return true;
    }

    void CloseSpeakers()
    {
        if (_cap == null) return;
        try { _cap.StopRecording(); }
        catch (InvalidOperationException) { } // never started
        _cap.Dispose();
        _cap = null;
    }

    // The sound goes out in about 10 ms steps, already 16-bit stereo.
    public void Start(Action<byte[], int> onSound)
    {
        _out = onSound;
        _running = true;
        try { _cap.StartRecording(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Write("sound: the speakers would not start: " + ex.Message);
            _reopen = true; // silence goes out until the writer opens them again
        }
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "sound writer" };
        _writer.Start();
    }

    // The screen clock's first picture is time zero, so the sound's time zero has to start then too.
    public void StartClock() => _clockGo.Set();

    void WriteLoop()
    {
        try
        {
            if (!_clockGo.Wait(TimeSpan.FromSeconds(3)))
                Log.Write("sound: the picture's start was never seen, sound clock started without it");
            // Windows hands loopback sound over a little after it plays, so the sound clock starts that much later to line
            // the two back up.
            Thread.Sleep(HandOverMs);
            while (_queue.TryDequeue(out _)) { } // sound from before the picture started has no place in the cast
            var clock = Stopwatch.StartNew();
            long written = 0, bytesPerSec = Rate * BlockAlign;
            long ahead = Align(bytesPerSec / 10), behind = Align(bytesPerSec / 100); // 100 ms and 10 ms
            var silence = new byte[Align(bytesPerSec / 10)];
            var nextTry = TimeSpan.Zero;
            bool retrying = false;
            while (_running)
            {
                long due = Align((long)(clock.Elapsed.TotalSeconds * bytesPerSec));
                while (_queue.TryDequeue(out var chunk))
                {
                    if (written + chunk.Length > due + ahead) continue; // the sound card's clock ran ahead: drop to stay in step
                    _out(chunk, chunk.Length);
                    written += chunk.Length;
                }
                while (written < due - behind)
                {
                    int n = (int)Math.Min(silence.Length, Align(due - written));
                    _out(silence, n);
                    written += n;
                }
                // Other speakers: silence covers the moment it takes to open them; no speakers yet, try again each second.
                if (_reopen && clock.Elapsed >= nextTry)
                {
                    _reopen = false;
                    if (!retrying) Log.Write("sound: Windows switched speakers, following them");
                    CloseSpeakers();
                    retrying = !OpenSpeakers(quiet: retrying);
                    if (retrying)
                    {
                        _reopen = true;
                        nextTry = clock.Elapsed + TimeSpan.FromSeconds(1);
                    }
                }
                Thread.Sleep(10);
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or ObjectDisposedException)
        {
            if (_running) Log.Write("sound: the sound encoder stopped: " + ex.Message);
        }
    }

    static long Align(long n) => n / BlockAlign * BlockAlign;

    public void Dispose()
    {
        _running = false;
        _clockGo.Set();
        _writer?.Join(1000);
        CloseSpeakers();
        _watch?.Dispose();
        _devices?.Dispose();
    }
}
