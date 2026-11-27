using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;

namespace PCCast2TV.Helpers;

// SHOW CURSOR: Windows' screen copy leaves the mouse pointer out, so its shape is drawn onto a second copy of the screen, which the converter reads instead.
public sealed partial class ScreenGrabber
{
    ID2D1Factory1 _d2dFactory;
    ID2D1Device _d2dDevice;
    ID2D1DeviceContext _d2d;
    ID3D11Texture2D _shown; // the screen copy with the pointer drawn on it
    ID3D11VideoProcessorInputView _shownInput;
    ID2D1Bitmap1 _shownTarget, _paint, _invert; // _invert: the pixels that flip the screen under them, like a text cursor's
    byte[] _shape = Array.Empty<byte>();
    int _px, _py;
    bool _cursorOn, _pointerSeen, _toldTurned, _toldShape, _toldShapeFailed, _toldDrawFailed;

    // Direct2D on the card's own device; without it the cast simply has no pointer.
    void SetUpDrawing()
    {
        _cursorOn = AppSettings.ShowCursor;
        try
        {
            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            _d2dDevice = _d2dFactory.CreateDevice(dxgi);
            _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            Log.Write("grab: the pointer can't be drawn on this card: " + ex.Message);
            _d2d = null;
        }
    }

    void BuildPointerInput(uint w, uint h)
    {
        _shownTarget?.Dispose();
        _shownInput?.Dispose();
        _shown?.Dispose();
        (_shownTarget, _shownInput, _shown) = (null, null, null);
        if (_d2d == null) return;
        _shown = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        _shownInput = _video.CreateVideoProcessorInputView(_shown, _vpEnum, new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
        using var surface = _shown.QueryInterface<IDXGISurface>();
        _shownTarget = _d2d.CreateBitmapFromDxgiSurface(surface,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    // Every tick: turning SHOW CURSOR on or off in Settings redraws the picture right away.
    void NoteCursorSetting()
    {
        if (AppSettings.ShowCursor == _cursorOn) return;
        _cursorOn = !_cursorOn;
        _unsent = true;
        _shownAt = Now(); // the clock paces from the moment the picture changed
        Log.Write("grab: pointer " + (_cursorOn ? "shown" : "hidden"));
    }

    // While Windows' frame is held: where the pointer is, whether it's on this screen, and its shape when Windows sends a new one.
    void TrackPointer(in OutduplFrameInfo info)
    {
        if (info.LastMouseUpdateTime != 0)
        {
            bool seen = info.PointerPosition.Visible;
            int x = info.PointerPosition.Position.X, y = info.PointerPosition.Position.Y;
            if (seen != _pointerSeen || x != _px || y != _py)
            {
                (_pointerSeen, _px, _py) = (seen, x, y);
                if (_cursorOn)
                {
                    _unsent = true;
                    _shownAt = info.LastMouseUpdateTime / (double)System.Diagnostics.Stopwatch.Frequency; // paced like a screen update, never faster than the cast
                }
            }
        }
        if (info.PointerShapeBufferSize == 0 || _d2d == null) return;
        if (_shape.Length < info.PointerShapeBufferSize) _shape = new byte[info.PointerShapeBufferSize];
        var pin = GCHandle.Alloc(_shape, GCHandleType.Pinned);
        try
        {
            var got = _dup.GetFramePointerShape(info.PointerShapeBufferSize, pin.AddrOfPinnedObject(), out _, out var s);
            if (got.Failure)
            {
                if (!_toldShapeFailed) Log.Write($"grab: Windows didn't hand over the pointer's shape ({got.Code:X8}), the TV keeps the last one");
                _toldShapeFailed = true;
                return;
            }
            MakeShape(s);
        }
        finally { pin.Free(); }
        if (_cursorOn) _unsent = true;
    }

    // Windows' three kinds of pointer, as two pictures: what's painted (colors with see-through edges), and what flips the screen under it.
    void MakeShape(OutduplPointerShapeInfo s)
    {
        int type = (int)s.Type, w = (int)s.Width, h = type == 1 ? (int)s.Height / 2 : (int)s.Height, pitch = (int)s.Pitch;
        var paint = new byte[w * h * 4];
        var flip = new byte[w * h * 4];
        bool flips = false;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (type == 1) // black and white: an AND mask, then an XOR mask under it
                {
                    int bit = 0x80 >> (x % 8);
                    bool and = (_shape[y * pitch + x / 8] & bit) != 0, xor = (_shape[(y + h) * pitch + x / 8] & bit) != 0;
                    if (!and) Gray(paint, o, xor ? (byte)255 : (byte)0);
                    else if (xor) flips = Gray(flip, o, 255);
                    continue;
                }
                int i = y * pitch + x * 4;
                byte b = _shape[i], g = _shape[i + 1], r = _shape[i + 2], a = _shape[i + 3];
                if (type == 2) (paint[o], paint[o + 1], paint[o + 2], paint[o + 3]) = ((byte)(b * a / 255), (byte)(g * a / 255), (byte)(r * a / 255), a);
                else if (a == 0) (paint[o], paint[o + 1], paint[o + 2], paint[o + 3]) = (b, g, r, 255); // masked color: shown as is
                else if ((b | g | r) != 0) flips = Gray(flip, o, 255); // masked color: flips the screen
            }
        _paint?.Dispose();
        _invert?.Dispose();
        _paint = ShapeBitmap(paint, w, h);
        _invert = flips ? ShapeBitmap(flip, w, h) : null;
        if (_toldShape) return;
        _toldShape = true;
        Log.Write($"grab: pointer shape {w}x{h}, " + (type == 1 ? "black and white" : type == 2 ? "color" : "masked color"));
    }

    static bool Gray(byte[] p, int o, byte v)
    {
        (p[o], p[o + 1], p[o + 2], p[o + 3]) = (v, v, v, 255);
        return true;
    }

    ID2D1Bitmap1 ShapeBitmap(byte[] pixels, int w, int h)
    {
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return _d2d.CreateBitmap(new SizeI(w, h), pin.AddrOfPinnedObject(), (uint)(w * 4), new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied))); }
        finally { pin.Free(); }
    }

    // The clean copy, or with SHOW CURSOR on and the pointer on this screen, a second copy with the pointer drawn where Windows says it is.
    ID3D11VideoProcessorInputView PickInput()
    {
        if (!_cursorOn || !_pointerSeen || _paint == null || _shownTarget == null) return _input;
        if (_inTurn > ModeRotation.Identity)
        {
            if (!_toldTurned) Log.Write("grab: SHOW CURSOR skips a turned monitor for now");
            _toldTurned = true;
            return _input;
        }
        _ctx.CopyResource(_shown, _copy);
        _d2d.Target = _shownTarget;
        _d2d.BeginDraw();
        _d2d.DrawImage(_paint, new Vector2(_px, _py), null, InterpolationMode.NearestNeighbor, CompositeMode.SourceOver);
        if (_invert != null) _d2d.DrawImage(_invert, new Vector2(_px, _py), null, InterpolationMode.NearestNeighbor, CompositeMode.MaskInverseErt);
        var drawn = _d2d.EndDraw(out _, out _);
        if (drawn.Success) return _shownInput;
        if (!_toldDrawFailed) Log.Write($"grab: drawing the pointer failed ({drawn.Code:X8}), that picture goes without it");
        _toldDrawFailed = true;
        return _input;
    }

    void DisposePointer()
    {
        _paint?.Dispose();
        _invert?.Dispose();
        _shownTarget?.Dispose();
        _shownInput?.Dispose();
        _shown?.Dispose();
        _d2d?.Dispose();
        _d2dDevice?.Dispose();
        _d2dFactory?.Dispose();
    }
}
