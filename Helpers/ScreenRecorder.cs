using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PCCast2TV.Helpers;

public enum OutputKind { Hls, LiveTs }

// The whole cast, inside the app: the screen grab on its own steady clock, the card's H.264 encoder, Windows' AAC
// encoder for the PC sound, and the transport stream: 1-second pieces for a Roku, or one endless stream for a DLNA TV.
public sealed class ScreenRecorder : IDisposable
{
    public const string PlaylistName = "index.m3u8";
    public static readonly Regex PieceName = new(@"^seg_(\d{5,9})\.ts$"); // 6 digits from piece 100000, 27 h 46 min in

    ScreenGrabber _grab;
    VideoEncoder _video;
    SoundCapture _sound;
    SoundEncoder _aac;
    TsWriter _ts;
    volatile bool _stopping, _dead;
    TimeSpan _cpuSeen;
    DateTime _cpuSeenAt;

    public bool HasSound { get; private set; }
    public EncoderChoice Running { get; private set; } // the encoder actually in use: the processor's if the card's wouldn't start
    public event Action Died;                    // the screen grab or the encoder stopped on its own
    public event Action<byte[], int> LiveChunk;  // live stream bytes as they come out (buffer is reused)

    public long EncodedBytes => _ts?.Bytes ?? 0;

    // The pieces and the live list, for the server; null until there is one.
    public string Playlist => _ts?.Playlist;
    public byte[] Piece(int index) => _ts?.Piece(index);
    public int? NewestPiece => _ts?.Newest;

    // The screen clock's counts since the last call: ticks, new pictures, late ticks, and whether the encoder kept up.
    public (int Ticks, int NewPictures, int Late, double LongestWakeMs, int ScreenUpdates, int HeldBack, int MostWaiting) TakeGrabCounts()
        => _grab?.TakeCounts() ?? default;

    // This app's share of the whole processor since the last call, in percent (the grab, sound and pieces all run here).
    public double TakeCpuPercent()
    {
        var used = Process.GetCurrentProcess().TotalProcessorTime;
        var now = DateTime.UtcNow;
        double pct = _cpuSeenAt == default ? 0
            : (used - _cpuSeen).TotalMilliseconds / (now - _cpuSeenAt).TotalMilliseconds / Environment.ProcessorCount * 100;
        (_cpuSeen, _cpuSeenAt) = (used, now);
        return pct;
    }

    // Called off the window's thread, so Windows' media parts are set up the way they're used later.
    // Throws InvalidOperationException with the reason when the card or the encoder won't start.
    public void Start(EncoderChoice enc, OutputKind kind)
    {
        try
        {
            VideoEncoder.EnsureStarted();
            _grab = new ScreenGrabber(enc.Fps);
            _video = new VideoEncoder(_grab, enc.Fps, (f, pts, key) => _ts.AddVideo(f, pts, key));
            if (enc.OnCard && !_video.Open(enc.Vendor, true))
            {
                // The processor's encoder can't keep up at 60, so the screen grab starts over at its 30.
                Log.Write("encoder: the card's encoder would not start, the processor takes over");
                _video.Dispose();
                _grab.Dispose();
                enc = EncoderPicker.Processor(enc.Vendor);
                _grab = new ScreenGrabber(enc.Fps);
                _video = new VideoEncoder(_grab, enc.Fps, (f, pts, key) => _ts.AddVideo(f, pts, key));
            }
            if (!enc.OnCard && !_video.Open(null, false)) throw new InvalidOperationException("no H.264 encoder would start");
            Running = enc;
            _sound = new SoundCapture();
            HasSound = _sound.Open();
            if (HasSound)
            {
                try { _aac = new SoundEncoder(_sound.Rate, (f, pts) => _ts.AddAudio(f, pts)); }
                catch (InvalidOperationException ex)
                {
                    Log.Write("sound: " + ex.Message);
                    _sound.Dispose();
                    _sound = null;
                    HasSound = false;
                }
            }
            _ts = new TsWriter(kind, HasSound);
            _ts.Live += (b, n) => LiveChunk?.Invoke(b, n);
            _video.Died += OnDied;
            _grab.Died += OnDied;
            _grab.FirstPicture += () => _sound?.StartClock();
            if (HasSound) _sound.Start((d, n) => _aac.Add(d, n));
            _video.Start();
            _grab.Start();
        }
        catch (SharpGen.Runtime.SharpGenException ex) { throw new InvalidOperationException(ex.Message, ex); }
    }

    void OnDied()
    {
        if (_stopping) return;
        _dead = true;
        Died?.Invoke();
    }

    // True once there's video for the player: 2 finished pieces listed (Roku, Chromecast), or live bytes flowing (DLNA).
    public async Task<bool> WaitForVideoAsync(bool hls, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !_dead)
        {
            if (hls ? (_ts?.Listed ?? 0) >= 2 : (_ts?.Bytes ?? 0) > 0) return true;
            await Task.Delay(200);
        }
        return false;
    }

    // Stops the clock first, then the sound, then the encoder (it finishes what's in line), then lets the card go.
    public void Dispose()
    {
        _stopping = true;
        _grab?.Stop();
        _sound?.Dispose();
        _video?.Dispose();
        _aac?.Dispose();
        _ts?.Close();
        _grab?.Dispose();
    }
}
