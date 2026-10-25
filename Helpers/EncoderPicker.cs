namespace PCCast2TV.Helpers;

// Vendor is the main screen's card ("VEN_10DE" style), so the cast uses the encoder on that same card.
public sealed record EncoderChoice(string Label, bool OnCard, string Vendor)
{
    // Card encoders keep up at 60 frames a second; the processor gets 30 so it never falls behind.
    public int Fps => OnCard ? 60 : 30;
}

// Finds the H.264 encoder Windows has for the card that drives the main screen; the processor only when there's none.
public static class EncoderPicker
{
    // Null when Windows' media parts are missing (Windows N without its Media Feature Pack) or no screen is found.
    public static Task<EncoderChoice> PickAsync() => Task.Run(() =>
    {
        try { return Pick(); }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidOperationException or DllNotFoundException)
        {
            Log.Write("encoder: none found: " + ex.Message);
            return null;
        }
    });

    static EncoderChoice Pick()
    {
        VideoEncoder.EnsureStarted();
        var (adapter, output) = ScreenGrabber.MainScreen();
        uint id = adapter.Description1.VendorId;
        string card = adapter.Description1.Description;
        adapter.Dispose();
        output.Dispose();
        string vendor = $"VEN_{id:X4}";
        var found = VideoEncoder.CardEncoders(vendor);
        if (found.Count > 0)
        {
            Log.Write($"encoder: picked {found[0]} for {card}");
            string label = id switch { 0x10DE => "NVIDIA NVENC", 0x1002 => "AMD AMF", 0x8086 => "INTEL QSV", _ => "CARD H.264" };
            return new EncoderChoice(label, true, vendor);
        }
        Log.Write($"encoder: {card} has no H.264 encoder, using Windows' own on the processor");
        return Processor(vendor);
    }

    public static EncoderChoice Processor(string vendor) => new("CPU H.264", false, vendor);
}
