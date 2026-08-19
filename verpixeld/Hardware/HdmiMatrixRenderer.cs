using System.Runtime.InteropServices;
using SkiaSharp;
using verpixeld.Configuration;

namespace verpixeld.Hardware;

/// <summary>
///     Renders the composed frame into the Linux framebuffer (/dev/fb0), i.e. the Pi's HDMI output.
///     An external LED sender card (Colorlight/Novastar-style) captures a rectangular region of that HDMI
///     signal and maps it 1:1 to the LED wall. This is the "HDMI" output path, an alternative to the direct
///     GPIO <see cref="RgbMatrixRenderer" />.
///
///     Implementation notes:
///     - Framebuffer geometry (resolution, stride, bpp) is read from sysfs (/sys/class/graphics/&lt;fb&gt;/…),
///       which avoids fragile ioctl struct marshalling and works on the Pi's fbdev/KMS emulation.
///     - Pixels are written per row via a plain seek+write on the device (no mmap); LED walls are small so the
///       per-frame byte volume is tiny even at 60 fps.
///     - The compositor hands us a BGRA8888 SKBitmap. A 32bpp XRGB/BGRX framebuffer (the common Pi format)
///       therefore takes a near-direct byte copy; 16bpp RGB565 is converted per pixel.
/// </summary>
public sealed class HdmiMatrixRenderer : IMatrixRenderer, IDisposable
{
    private string _device;
    private int _offsetX;
    private int _offsetY;
    private int _scale;
    private bool _clearOnStart;
    private bool _swapRedBlue;
    private readonly object _lock = new();

    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int MAP_SHARED = 0x1;
    private static readonly IntPtr MAP_FAILED = new(-1);

    private FileStream? _fb;
    private IntPtr _map = IntPtr.Zero;
    private nuint _mapLen;
    private long _stride;
    private int _fbWidth;
    private int _fbHeight;
    private int _bytesPerPixel;
    private byte[] _srcBuf = [];
    private byte[] _rowBuf = [];
    private bool _cursorHidden;

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, nuint length);

    public HdmiMatrixRenderer(int wallWidth, int wallHeight, HdmiOptions options)
    {
        Width = wallWidth;
        Height = wallHeight;
        _device = string.IsNullOrWhiteSpace(options.FramebufferDevice) ? "/dev/fb0" : options.FramebufferDevice;
        _offsetX = Math.Max(0, options.OffsetX);
        _offsetY = Math.Max(0, options.OffsetY);
        _scale = Math.Max(1, options.Scale);
        _clearOnStart = options.ClearScreenOnStart;
        _swapRedBlue = options.SwapRedBlue;
    }

    public int Width { get; }
    public int Height { get; }

    public HdmiOptions Snapshot()
    {
        lock (_lock)
        {
            return new HdmiOptions
            {
                FramebufferDevice = _device,
                WallWidth = Width,
                WallHeight = Height,
                OffsetX = _offsetX,
                OffsetY = _offsetY,
                Scale = _scale,
                ClearScreenOnStart = _clearOnStart,
                SwapRedBlue = _swapRedBlue
            };
        }
    }

    /// <summary>
    ///     Live update of offset / scale / channel-swap / clear-on-start. Changing the framebuffer
    ///     device requires a restart (the mapping is opened once).
    /// </summary>
    public void Reconfigure(HdmiOptions options)
    {
        lock (_lock)
        {
            _offsetX = Math.Max(0, options.OffsetX);
            _offsetY = Math.Max(0, options.OffsetY);
            _scale = Math.Max(1, options.Scale);
            _clearOnStart = options.ClearScreenOnStart;
            _swapRedBlue = options.SwapRedBlue;
            if (!string.IsNullOrWhiteSpace(options.FramebufferDevice))
                _device = options.FramebufferDevice;
        }
    }

    public void Initialize()
    {
        if (_fb != null)
            return; // already initialized (Initialize is called idempotently)

        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "HDMI/framebuffer output is only available on Linux (the Raspberry Pi).");

        ReadFramebufferGeometry();

        _fb = new FileStream(_device, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Map the whole visible framebuffer once. Writing into the mapping is a plain memory store — no
        // per-frame seek/write/flush syscalls, which removes the periodic hitch and avoids the sender card
        // capturing a half-written frame mid-syscall.
        _mapLen = (nuint)(_stride * _fbHeight);
        var fd = (int)_fb.SafeFileHandle.DangerousGetHandle();
        _map = mmap(IntPtr.Zero, _mapLen, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
        if (_map == MAP_FAILED || _map == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _map = IntPtr.Zero;
            throw new InvalidOperationException($"mmap({_device}) failed (errno {err}).");
        }

        Console.WriteLine(
            $"[HDMI] Framebuffer {_device}: {_fbWidth}x{_fbHeight} @ {_bytesPerPixel * 8}bpp, stride={_stride}B (mmap)");
        Console.WriteLine(
            $"[HDMI] Wall region: {Width}x{Height} (scale {_scale}) at offset ({_offsetX},{_offsetY})");

        var scaledW = Width * _scale;
        var scaledH = Height * _scale;
        if (_offsetX + scaledW > _fbWidth || _offsetY + scaledH > _fbHeight)
            Console.WriteLine(
                $"[HDMI] WARNING: wall region ({_offsetX}+{scaledW} x {_offsetY}+{scaledH}) exceeds the framebuffer " +
                $"({_fbWidth}x{_fbHeight}); output will be clipped. Set the HDMI resolution >= the wall or adjust Scale/Offset.");

        // Stop the TTY console cursor from blinking over the mapped region.
        TryHideConsoleCursor();

        if (_clearOnStart)
            ClearScreen();
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        lock (_lock)
        {
        if (_map == IntPtr.Zero)
            return;

        if (bitmap.Width != Width || bitmap.Height != Height)
            throw new ArgumentException(
                $"Bitmap size ({bitmap.Width}x{bitmap.Height}) doesn't match wall size ({Width}x{Height})");

        var src = bitmap.GetPixels();
        if (src == IntPtr.Zero)
            return;

        var srcRowBytes = bitmap.RowBytes; // BGRA8888 => Width * 4
        var needed = srcRowBytes * Height;
        if (_srcBuf.Length != needed)
            _srcBuf = new byte[needed];
        Marshal.Copy(src, _srcBuf, 0, needed);

        var scaledWidthPixels = Math.Min(Width * _scale, Math.Max(0, _fbWidth - _offsetX));
        if (scaledWidthPixels <= 0)
            return;
        var destRowLen = scaledWidthPixels * _bytesPerPixel;
        if (_rowBuf.Length < destRowLen)
            _rowBuf = new byte[destRowLen];

        var mapBase = _map.ToInt64();
        for (var sy = 0; sy < Height; sy++)
        {
            BuildDestRow(sy, srcRowBytes, scaledWidthPixels);

            for (var k = 0; k < _scale; k++)
            {
                var dy = _offsetY + sy * _scale + k;
                if (dy < 0 || dy >= _fbHeight)
                    continue;

                var pos = dy * _stride + (long)_offsetX * _bytesPerPixel;
                Marshal.Copy(_rowBuf, 0, new IntPtr(mapBase + pos), destRowLen);
            }
        }
        }
    }

    public void Shutdown()
    {
        Dispose();
        Console.WriteLine("[HDMI] Framebuffer renderer shut down");
    }

    public void Dispose()
    {
        TryRestoreConsoleCursor();
        if (_map != IntPtr.Zero)
        {
            munmap(_map, _mapLen);
            _map = IntPtr.Zero;
        }

        _fb?.Dispose();
        _fb = null;
    }

    /// <summary>
    ///     Expands one source row into <see cref="_rowBuf" /> for the target bpp, applying the integer scale
    ///     factor (each source pixel is replicated <see cref="_scale" /> times horizontally).
    /// </summary>
    private void BuildDestRow(int sy, int srcRowBytes, int scaledWidthPixels)
    {
        var srcBase = sy * srcRowBytes;
        var d = 0;

        for (var px = 0; px < scaledWidthPixels; px++)
        {
            var sx = px / _scale;
            var o = srcBase + sx * 4; // BGRA
            var b = _srcBuf[o];
            var g = _srcBuf[o + 1];
            var r = _srcBuf[o + 2];
            if (_swapRedBlue)
                (r, b) = (b, r);

            if (_bytesPerPixel == 4)
            {
                _rowBuf[d] = b;
                _rowBuf[d + 1] = g;
                _rowBuf[d + 2] = r;
                _rowBuf[d + 3] = 0xFF;
                d += 4;
            }
            else // 16bpp RGB565, little-endian
            {
                var val = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
                _rowBuf[d] = (byte)(val & 0xFF);
                _rowBuf[d + 1] = (byte)(val >> 8);
                d += 2;
            }
        }
    }

    private void ClearScreen()
    {
        if (_map == IntPtr.Zero)
            return;

        var zero = new byte[_stride];
        var mapBase = _map.ToInt64();
        for (var y = 0; y < _fbHeight; y++)
            Marshal.Copy(zero, 0, new IntPtr(mapBase + y * _stride), (int)_stride);
    }

    /// <summary>
    ///     Best-effort hiding of the Linux VT text cursor so it does not blink over the mapped wall region.
    ///     Writes the ANSI hide-cursor sequence to the current VT and disables fbcon cursor blink via sysfs.
    ///     For a permanent fix add <c>vt.global_cursor_default=0</c> to the kernel cmdline.
    /// </summary>
    private void TryHideConsoleCursor()
    {
        TryWrite("/sys/class/graphics/fbcon/cursor_blink", "0");
        // ESC[?25l = hide cursor. /dev/tty0 is the current foreground VT.
        _cursorHidden = TryWriteTty("/dev/tty0", "\u001b[?25l") | TryWriteTty("/dev/tty1", "\u001b[?25l");
        if (!_cursorHidden)
            Console.WriteLine(
                "[HDMI] Could not hide the console cursor (permission?). Add 'vt.global_cursor_default=0' to the " +
                "kernel cmdline for a permanent fix.");
    }

    private void TryRestoreConsoleCursor()
    {
        if (!_cursorHidden)
            return;
        TryWriteTty("/dev/tty0", "\u001b[?25h"); // show cursor again
        TryWriteTty("/dev/tty1", "\u001b[?25h");
        _cursorHidden = false;
    }

    private static void TryWrite(string path, string value)
    {
        try
        {
            if (File.Exists(path))
                File.WriteAllText(path, value);
        }
        catch
        {
            // best effort
        }
    }

    private static bool TryWriteTty(string path, string escape)
    {
        try
        {
            using var tty = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.ASCII.GetBytes(escape);
            tty.Write(bytes, 0, bytes.Length);
            tty.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads resolution / stride / bpp from sysfs for the configured framebuffer node.</summary>
    private void ReadFramebufferGeometry()
    {
        var node = Path.GetFileName(_device); // "/dev/fb0" -> "fb0"
        var sysfs = $"/sys/class/graphics/{node}";

        // virtual_size: "WIDTH,HEIGHT"
        var size = ReadSysfs(Path.Combine(sysfs, "virtual_size"));
        var parts = size?.Split(',');
        if (parts is { Length: 2 } &&
            int.TryParse(parts[0].Trim(), out var w) &&
            int.TryParse(parts[1].Trim(), out var h))
        {
            _fbWidth = w;
            _fbHeight = h;
        }
        else
        {
            throw new InvalidOperationException(
                $"Could not read framebuffer size from {sysfs}/virtual_size (got '{size}'). Is {_device} an fbdev device?");
        }

        var bppText = ReadSysfs(Path.Combine(sysfs, "bits_per_pixel"));
        var bpp = int.TryParse(bppText?.Trim(), out var b) ? b : 32;
        _bytesPerPixel = Math.Max(2, bpp / 8);
        if (_bytesPerPixel is not (2 or 4))
            throw new InvalidOperationException(
                $"Unsupported framebuffer depth {bpp}bpp on {_device}; only 16bpp and 32bpp are supported.");

        var strideText = ReadSysfs(Path.Combine(sysfs, "stride"));
        if (long.TryParse(strideText?.Trim(), out var s) && s > 0)
            _stride = s;
        else
            _stride = (long)_fbWidth * _bytesPerPixel; // no padding fallback
    }

    private static string? ReadSysfs(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
