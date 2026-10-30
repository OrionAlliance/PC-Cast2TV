using System.Text.Json;

namespace PCCast2TV.Helpers;

// Reading the TV's answers: fields of its messages, null or empty when they aren't the shape they should be.
public sealed partial class CastPlayer
{
    static string Type(JsonElement m) => Text(m, "type");

    // A text field of a TV message; null when the message or the field isn't the shape it should be.
    static string Text(JsonElement m, string name) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The first entry of a message's status list, when it has one that is an object.
    static JsonElement? FirstStatus(JsonElement m) =>
        m.ValueKind == JsonValueKind.Object && m.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0
        && s[0].ValueKind == JsonValueKind.Object ? s[0] : null;

    // The session of the TV app this player opened, when the TV's status lists it.
    (string SessionId, string TransportId)? App(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("applications", out var apps) || apps.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in apps.EnumerateArray())
            if (Text(a, "appId") == _appId && Text(a, "sessionId") is { } sid && Text(a, "transportId") is { } tid)
                return (sid, tid);
        return null;
    }

    static string State(JsonElement m) => FirstStatus(m) is { } st ? Text(st, "playerState") : null;

    // Why the TV's player stopped or refused, when it says (idle reason or error code).
    static string Detail(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object) return "";
        if (m.TryGetProperty("detailedErrorCode", out var code)) return "error " + code;
        if (m.TryGetProperty("reason", out var reason)) return reason.ToString();
        return FirstStatus(m) is { } st && Text(st, "idleReason") is { } idle ? "idle " + idle : "";
    }

    // The player's spot and the live stretch it says it can play, in the cast's seconds, for the log.
    static string Where(JsonElement m)
    {
        if (FirstStatus(m) is not { } st) return "";
        var parts = new List<string>();
        if (At(m) is { } at) parts.Add($"at {at:0.00} s");
        if (st.TryGetProperty("liveSeekableRange", out var r) && r.ValueKind == JsonValueKind.Object
            && r.TryGetProperty("start", out var a) && a.ValueKind == JsonValueKind.Number
            && r.TryGetProperty("end", out var b) && b.ValueKind == JsonValueKind.Number)
            parts.Add($"can play {a.GetDouble():0.00} to {b.GetDouble():0.00} s");
        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }

    // The player's spot in the cast, in seconds; null when it doesn't say.
    static double? At(JsonElement m) =>
        FirstStatus(m) is { } st && st.TryGetProperty("currentTime", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() : null;
}
