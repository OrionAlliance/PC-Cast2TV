namespace PCCast2TV.Helpers;

// The once-a-second readout: the dial's numbers, the log's 10-second lines, and ending a cast whose TV went quiet.
public sealed partial class Caster
{
    // The encoder's load, read off the window's thread; dropped if that cast has ended since.
    void OnEncoderLoad(double? load, DateTime cast)
    {
        if (Phase != Phase.Casting || _castStartUtc != cast) return;
        EncoderLoad = load;
        Raise();
    }

    // Once a second while online: feed the readout, and end the cast if the TV went quiet.
    void Tick()
    {
        if (Phase != Phase.Casting) return;
        bool mirrored = _castPlayer?.Mirrored == true;
        Meter.Tick(_rec.EncodedBytes, SentBytes);
        var now = DateTime.UtcNow;
        if (++_ticks % 10 == 0) // the readout, written down every 10 seconds so a cast can be judged from the log
        {
            Log.Write($"cast: made {Meter.EncodedLine[^1]:0.0} Mbps, sent {Meter.Now:0.0}, peak {Meter.Peak:0.0}, signal {Meter.SignalLabel}");
            if (mirrored) Log.Write(_castPlayer.TakeMirrorReport());
            else if (CastingTo.Kind == DeviceKind.Cast) // the Roku reports its own side; this is the Chromecast's
            {
                var a = _server.TakeAsks();
                Log.Write($"chromecast: asked for the playlist {a.Playlists} times and {a.Pieces} pieces (last {a.LastPiece}, newest made {_rec.NewestPiece})" +
                          (a.Gone > 0 ? $", {a.Gone} already gone" : ""));
            }
            // Where a hitch starts: clock ticks (late = slept through), real screen changes, whether the encoder kept up, the card's load.
            var g = _rec.TakeGrabCounts();
            double cpu = _rec.TakeCpuPercent();
            DroppedFrames += g.Late;
            CpuLoad = cpu;
            var cast = _castStartUtc;
            string rate = _rec.EncoderRate;
            _ = Task.Run(() =>
            {
                var gpu = GpuLoad.Sample(); // read off the window's thread: Windows' counters can take a moment
                Log.Write($"cast: timing: {g.Ticks} ticks ({g.NewPictures} new pictures, {g.Late} late, longest wake {g.LongestWakeMs:0.0} ms), " +
                          $"screen changed {g.ScreenUpdates} times, encoder: most waiting {g.MostWaiting}, held back {g.HeldBack}{(rate == null ? "" : ", " + rate)}, " +
                          $"app cpu {cpu:0}%, {gpu.Line}");
                _ui.Post(_ => OnEncoderLoad(gpu.Encoder, cast), null);
            });
        }
        var heard = mirrored ? _castPlayer.LastHeardUtc : _server.LastAskUtc;
        var last = heard > _castStartUtc ? heard : _castStartUtc;
        if (now - last > TimeSpan.FromSeconds(20))
        {
            _ = StopCastAsync("STOPPED PLAYING · CAST ENDED", about: CastingTo);
            return;
        }
        Raise();
    }
}
