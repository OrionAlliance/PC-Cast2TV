using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PCCast2TV.Helpers;

// The screen side of the grab: Windows' copy of the screen, the converter that makes it a video picture, and reading one back.
public sealed partial class ScreenGrabber
{
    // The copy of the screen and the converter that turns it upright, scales it into the cast's size (black bars when the
    // shape differs) and makes it video color. Windows hands the screen over unturned, so a turned screen is turned back.
    void BuildInput(uint w, uint h)
    {
        _input?.Dispose();
        _vp?.Dispose();
        _vpEnum?.Dispose();
        _copy?.Dispose();
        _copy = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _vpEnum = _video.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = w, InputHeight = h, InputFrameRate = new Rational((uint)_fps, 1),
            OutputWidth = (uint)Width, OutputHeight = (uint)Height, OutputFrameRate = new Rational((uint)_fps, 1),
            Usage = VideoUsage.PlaybackNormal,
        });
        _vp = _video.CreateVideoProcessor(_vpEnum, 0);
        _input = _video.CreateVideoProcessorInputView(_copy, _vpEnum, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        foreach (var p in _all)
        {
            p.View?.Dispose();
            p.View = _video.CreateVideoProcessorOutputView(p.Tex, _vpEnum, new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
        }
        // Screen colors are full range; TVs expect video colors (BT.709, 16 to 235), the way the encoder labels them.
        _vctx.VideoProcessorSetStreamColorSpace(_vp, 0, new VideoProcessorColorSpace { RGB_Range = 0 });
        _vctx.VideoProcessorSetOutputColorSpace(_vp, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });
        _vctx.VideoProcessorSetOutputBackgroundColor(_vp, false, new VideoColor { Rgba = new VideoColorRgba { A = 1 } });
        _vctx.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _vctx.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false); // no driver "enhancements"

        bool sideways = _dupTurn is ModeRotation.Rotate90 or ModeRotation.Rotate270;
        if (_dupTurn is ModeRotation.Rotate90 or ModeRotation.Rotate180 or ModeRotation.Rotate270)
            _vctx.VideoProcessorSetStreamRotation(_vp, 0, true, (VideoProcessorRotation)((int)_dupTurn - 1)); // turned clockwise, as OBS does
        var (uw, uh) = sideways ? (h, w) : (w, h);
        double s = Math.Min((double)Width / uw, (double)Height / uh);
        int dw = (int)Math.Round(uw * s) & ~1, dh = (int)Math.Round(uh * s) & ~1;
        int dx = (Width - dw) / 2 & ~1, dy = (Height - dh) / 2 & ~1;
        _vctx.VideoProcessorSetStreamSourceRect(_vp, 0, true, new RawRect(0, 0, (int)w, (int)h));
        _vctx.VideoProcessorSetStreamDestRect(_vp, 0, true, new RawRect(dx, dy, dx + dw, dy + dh));
        _vctx.VideoProcessorSetOutputTargetRect(_vp, true, new RawRect(0, 0, Width, Height));
        (_inW, _inH, _inTurn) = (w, h, _dupTurn);
        Log.Write($"grab: screen picture {w}x{h}" + (_dupTurn > ModeRotation.Identity ? $" turned {_dupTurn}" : "") + $", fills {dw}x{dh} of {Width}x{Height}");
    }

    // The newest picture Windows has, already in video color; null when nothing new came (or only the mouse moved).
    // The first one is taken as is: on a still screen Windows' opening picture is all there is.
    Picture TakeNewest(uint waitMs, bool first = false)
    {
        if (_dupBroken && !Reopen()) return null;
        var r = _dup.AcquireNextFrame(waitMs, out OutduplFrameInfo info, out IDXGIResource res);
        if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return null;
        if (r.Failure)
        {
            if (r.Code != Vortice.DXGI.ResultCode.AccessLost.Code) Log.Write($"grab: Windows' screen copy failed ({r.Code:X8}), reopening it");
            _dupBroken = true; // screen mode changed or a secure screen (UAC) is up; the last picture holds meanwhile
            return null;
        }
        try
        {
            if (!first && (info.LastPresentTime == 0 || info.AccumulatedFrames == 0)) return null;
            lock (_countGate) _screenUpdates += (int)info.AccumulatedFrames;
            using var tex = res.QueryInterface<ID3D11Texture2D>();
            var d = tex.Description;
            if (d.Width != _inW || d.Height != _inH || _dupTurn != _inTurn) BuildInput(d.Width, d.Height); // the screen changed mode mid-cast
            _ctx.CopyResource(_copy, tex);
        }
        finally
        {
            res.Dispose();
            _dup.ReleaseFrame();
        }
        Picture p;
        lock (_gate)
        {
            if (_free.Count == 0) { lock (_countGate) _heldBack++; return null; } // the encoder is behind: hold the last picture
            p = _free.Pop();
            p.Refs = 1;
        }
        _vctx.VideoProcessorBlt(_vp, p.View, 0, 1, new[] { new VideoProcessorStream { Enable = true, InputSurface = _input } }).CheckError();
        return p;
    }

    bool Reopen()
    {
        _dup?.Dispose();
        _dup = null;
        try { _dup = _output.DuplicateOutput(Device); }
        catch (SharpGen.Runtime.SharpGenException) { return false; } // not back yet; try again next tick
        _dupTurn = _dup.Description.Rotation;
        _dupBroken = false;
        Log.Write("grab: screen copy reopened");
        return true;
    }

    // For an encoder that works in main memory: NV12 rows (brightness, then half-height color) packed tight.
    public void ReadBack(Picture p, IntPtr dest)
    {
        _ctx.CopyResource(_staging, p.Tex);
        var map = _ctx.Map(_staging, 0, MapMode.Read);
        try
        {
            for (int y = 0; y < Height; y++) CopyRow(map.DataPointer + (nint)(y * map.RowPitch), dest + y * Width);
            nint uv = map.DataPointer + (nint)(Height * map.RowPitch);
            for (int y = 0; y < Height / 2; y++) CopyRow(uv + (nint)(y * map.RowPitch), dest + (Height + y) * Width);
        }
        finally { _ctx.Unmap(_staging, 0); }
    }

    unsafe void CopyRow(nint from, nint to) => Buffer.MemoryCopy((void*)from, (void*)to, Width, Width);
}
