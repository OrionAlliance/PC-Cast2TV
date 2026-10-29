using System.Net;
using System.Text.Json;

namespace PCCast2TV.Helpers;

// The TV's own screen mirroring (what Chrome casts a screen to): offer the picture and sound, then send them live.
public sealed partial class CastPlayer
{
    const string Mirroring = "0F5096E8";
    const string NsWebRtc = "urn:x-cast:com.google.cast.webrtc";

    // What MirrorAsync says when mirroring didn't work (turned down, or no picture); the caller casts the pieces instead.
    public const string UsePieces = "MIRRORING DID NOT WORK";

    MirrorSender _mirror;

    public bool Mirrored => _mirror != null;
    public DateTime LastHeardUtc => _mirror?.LastHeardUtc ?? default;
    public long SentBytes => _mirror?.SentBytes ?? 0;
    public bool TvTookSound => _mirror?.HasSound ?? true;
    public string TakeMirrorReport() => _mirror?.TakeReport();

    // Opens the TV's mirroring and sends it the recorder's frames; returns the player, or what went wrong.
    public static async Task<(CastPlayer Player, string Error)> MirrorAsync(Device d, ScreenRecorder rec)
    {
        var c = new CastPlayer(d) { _appId = Mirroring };
        MirrorSender m = null;
        try
        {
            await c.OpenAsync();
            await c.SendAsync(NsReceiver, "receiver-0", new { type = "LAUNCH", appId = Mirroring, requestId = c.NextId() });
            var status = await c.WaitAsync(NsReceiver, x => Type(x) == "LAUNCH_ERROR" || Type(x) == "RECEIVER_STATUS" && c.App(x) != null, 15);
            if (status == null || Type(status.Value) == "LAUNCH_ERROR")
            {
                Log.Write("cast: the TV did not open its mirroring" + (status == null ? "" : ": " + MirrorSender.Flat(status.Value)));
                c.Dispose();
                return (null, UsePieces);
            }
            (c._sessionId, c._transportId) = c.App(status.Value).Value;
            Log.Write("cast: the TV's mirroring is open");
            await c.SendAsync(NsConnection, c._transportId, new { type = "CONNECT" });

            var (w, h) = rec.Size;
            m = new MirrorSender(rec.Running.Fps, w, h, rec.HasSound ? rec.SoundRate : 0, rec.ForceKey);
            int seq = c.NextId();
            await c.SendAsync(NsWebRtc, c._transportId, m.Offer(seq));
            var answer = await c.WaitAsync(NsWebRtc, x => Type(x) == "ANSWER" && SeqOf(x) == seq, 10);
            var why = answer == null ? "it never answered" : m.Accept(answer.Value);
            if (why != null)
            {
                Log.Write("cast: mirroring turned down: " + why);
                m.Dispose();
                await c.StopAsync();
                return (null, UsePieces);
            }

            c._mirror = m;
            m.Start(IPAddress.Parse(d.Ip));
            rec.MirrorTo(m, piecesToo: true);
            if (await m.HeardAsync(8))
            {
                rec.MirrorTo(m, piecesToo: false);
                Log.Write("cast: the TV has the first picture");
                _ = c.WatchAsync();
                return (c, null);
            }
            Log.Write("cast: the TV never said it got a picture");
            rec.MirrorTo(null, piecesToo: true);
            await c.StopAsync();
            return (null, UsePieces);
        }
        catch (Exception ex) when (IsNetError(ex))
        {
            Log.Write("cast: connection failed: " + ex.GetType().Name + " " + ex.Message);
            m?.Dispose();
            c.Dispose();
            return (null, "STOPPED ANSWERING");
        }
    }

    static int? SeqOf(JsonElement m) =>
        m.TryGetProperty("seqNum", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int n) ? n : null;
}
