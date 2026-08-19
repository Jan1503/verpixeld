using System.Runtime.InteropServices;
using SkiaSharp;
using verpixeld.Configuration;

namespace verpixeld.Hardware;

/// <summary>
///     Streams composed frames to an RP2040 "bridge" receiver over SPI (the Pi is SPI master). The Pi
///     does ALL the work here: it packs the exact panel bit-plane buffer (buf16 layout, with the panel
///     geometry baked in) and sends it; the RP2040 just DMAs it into buf16 and uploads it. That keeps the
///     MCU free of per-pixel work, so it sustains high SPI and any colour depth.
///
///     Protocol per frame:  "VPX2" (start) + buf16 payload (LE uint16 planes) + CRC32 (LE, over buf16).
///     buf16 layout: NPLANES planes x DISPL uint16; plane p, cell (rowaddr*WIDTH+col) -> p*DISPL+cell.
///     Geometry = verified v14 (landscape 256x128 -> stacked 128x256, flip_x + flip_y + rot_cw).
///     Colour depth = 8 bit/channel (NPlanes=8, matches the firmware's HUB_NPLANES=8). The extra
///     planes cost the MCU no extra panel-upload time, only SPI bandwidth (payload doubles to 128 KB).
///     The CRC32 trailer lets the MCU verify the WHOLE payload and drop any corrupted frame (keeping
///     the last good image) - this catches framing-preserving SPI bit slips the old end-marker missed.
///
///     Requires SPI enabled (dtparam=spi=on) and spidev bufsiz >= payload size (~131 KB for 8-bit):
///     set "options spidev bufsiz=140000" in /etc/modprobe.d/spidev.conf.
/// </summary>
public sealed class SpiMatrixRenderer : IMatrixRenderer, IDisposable
{
    // Panel constants (must match the MCU firmware: COLOR_4BITS, 256x128, 1/64 scan).
    private const int PanelWidth = 128;   // physical module width / shift columns
    private const int PolDispl = 64;      // pol_displ
    private const int HalfH = 128;        // one module height (2*pol_displ)
    private const int NPlanes = 8;        // 8 bit/channel true colour (must match firmware HUB_NPLANES)
    private const int Displ = PanelWidth * PolDispl; // 8192 uint16 per plane

    private const int O_RDWR = 2;
    private const uint SPI_IOC_WR_MODE = 0x40016b01;
    private const uint SPI_IOC_WR_MAX_SPEED_HZ = 0x40046b04;
    private static readonly byte[] Magic = "VPX2"u8.ToArray();
    private const int CrcLen = 4;
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, int offset, int length)
    {
        var c = 0xFFFFFFFFu;
        for (var i = 0; i < length; i++)
            c = CrcTable[(c ^ data[offset + i]) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private string _device;
    private int _speedHz;
    private bool _swapRedBlue;
    private readonly object _lock = new();

    private int _fd = -1;
    private byte[] _payload = [];   // magic + buf16 + crc32
    private int _buf16Bytes;
    private byte[] _srcBuf = [];    // BGRA copy
    private int[] _cellOff = [];    // per logical pixel: buf16 cell offset (uint16 index within a plane)
    private byte[] _gbase = [];     // per logical pixel: group base bit (0/3/6/9)

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, ref byte arg);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl_u32(int fd, uint request, ref uint arg);

    public SpiMatrixRenderer(int wallWidth, int wallHeight, SpiOptions options)
    {
        Width = wallWidth;
        Height = wallHeight;
        _device = string.IsNullOrWhiteSpace(options.Device) ? "/dev/spidev0.0" : options.Device;
        _speedHz = options.SpeedHz > 0 ? options.SpeedHz : 40_000_000;
        _swapRedBlue = options.SwapRedBlue;
    }

    public int Width { get; }
    public int Height { get; }

    public SpiOptions Snapshot()
    {
        lock (_lock)
        {
            return new SpiOptions
            {
                Device = _device,
                SpeedHz = _speedHz,
                WallWidth = Width,
                WallHeight = Height,
                SwapRedBlue = _swapRedBlue
            };
        }
    }

    /// <summary>
    ///     Live update of clock speed and R/B swap. Device path changes take effect on the next
    ///     Initialize/re-open (speed is applied via ioctl immediately when the bus is open).
    /// </summary>
    public void Reconfigure(SpiOptions options)
    {
        lock (_lock)
        {
            _swapRedBlue = options.SwapRedBlue;
            if (options.SpeedHz > 0 && options.SpeedHz != _speedHz)
            {
                _speedHz = options.SpeedHz;
                if (_fd >= 0)
                {
                    var speed = (uint)_speedHz;
                    ioctl_u32(_fd, SPI_IOC_WR_MAX_SPEED_HZ, ref speed);
                }
            }
            if (!string.IsNullOrWhiteSpace(options.Device))
                _device = options.Device;
        }
    }

    /// <summary>v14 geometry: logical (x,y) in the landscape frame -> (cellOff, gbase) in buf16.</summary>
    private static (int cellOff, int gbase) MapPixel(int x, int y)
    {
        int px = 127 - y;                 // rot_cw
        int py = x;
        int col = 127 - px;               // flip_x  (== y)
        int bottom = py >= HalfH ? 1 : 0;
        int within = bottom == 1 ? py - HalfH : py;
        within = 127 - within;            // flip_y
        int lower = within >= PolDispl ? 1 : 0;
        int rowaddr = lower == 1 ? within - PolDispl : within;
        int gbase = (bottom == 1 ? 0 : 6) + (lower == 1 ? 3 : 0);
        return (rowaddr * PanelWidth + col, gbase);
    }

    public void Initialize()
    {
        if (_fd >= 0)
            return;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("SPI output is only available on Linux (the Raspberry Pi).");
        if (Width != 256 || Height != 128)
            throw new InvalidOperationException(
                $"SPI bridge currently expects a 256x128 wall (got {Width}x{Height}).");

        _fd = open(_device, O_RDWR);
        if (_fd < 0)
            throw new InvalidOperationException(
                $"open({_device}) failed (errno {Marshal.GetLastWin32Error()}). Is SPI enabled (dtparam=spi=on)?");

        byte mode = 0;
        if (ioctl(_fd, SPI_IOC_WR_MODE, ref mode) < 0)
            throw new InvalidOperationException($"SPI_IOC_WR_MODE failed (errno {Marshal.GetLastWin32Error()}).");
        var speed = (uint)_speedHz;
        if (ioctl_u32(_fd, SPI_IOC_WR_MAX_SPEED_HZ, ref speed) < 0)
            throw new InvalidOperationException($"SPI_IOC_WR_MAX_SPEED_HZ failed (errno {Marshal.GetLastWin32Error()}).");

        _buf16Bytes = NPlanes * Displ * 2;
        _payload = new byte[Magic.Length + _buf16Bytes + CrcLen];
        Array.Copy(Magic, _payload, Magic.Length);
        _srcBuf = new byte[Width * Height * 4];

        // Precompute the geometry map once.
        _cellOff = new int[Width * Height];
        _gbase = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var (cell, gb) = MapPixel(x, y);
            _cellOff[y * Width + x] = cell;
            _gbase[y * Width + x] = (byte)gb;
        }

        Console.WriteLine($"[SPI] {_device} @ {_speedHz / 1_000_000.0:0.#} MHz, VPX2 bridge, frame {Width}x{Height}, " +
                          $"payload {_payload.Length} B. Ensure spidev bufsiz >= {_payload.Length}.");
    }

    public void RenderFrame(SKBitmap bitmap)
    {
        lock (_lock)
        {
        if (_fd < 0)
            return;
        if (bitmap.Width != Width || bitmap.Height != Height)
            throw new ArgumentException(
                $"Bitmap size ({bitmap.Width}x{bitmap.Height}) doesn't match wall size ({Width}x{Height})");

        var src = bitmap.GetPixels();
        if (src == IntPtr.Zero)
            return;
        var srcRowBytes = bitmap.RowBytes;
        var needed = srcRowBytes * Height;
        if (_srcBuf.Length != needed)
            _srcBuf = new byte[needed];
        Marshal.Copy(src, _srcBuf, 0, needed);

        // Clear only the buf16 portion (OR-packing below); keep the magic prefix. CRC written after.
        Array.Clear(_payload, Magic.Length, _buf16Bytes);

        var payload = _payload;
        var srcBuf = _srcBuf;
        var cellOffs = _cellOff;
        var gbases = _gbase;
        const int planeStrideBytes = Displ * 2;
        var magicLen = Magic.Length;

        for (var y = 0; y < Height; y++)
        {
            var row = y * srcRowBytes;
            var pixRow = y * Width;
            for (var x = 0; x < Width; x++)
            {
                var o = row + x * 4;
                int b = srcBuf[o];
                int g = srcBuf[o + 1];
                int r = srcBuf[o + 2];
                if (_swapRedBlue)
                    (r, b) = (b, r);
                if ((r | g | b) == 0)
                    continue; // black pixel -> nothing to set

                var pix = pixRow + x;
                var cell = cellOffs[pix];
                var gbase = gbases[pix];
                var cellByte = magicLen + cell * 2;

                for (var p = 0; p < NPlanes; p++)
                {
                    var bit = ((r >> p) & 1) | (((g >> p) & 1) << 1) | (((b >> p) & 1) << 2);
                    if (bit == 0)
                        continue;
                    var val = bit << gbase;
                    var idx = cellByte + p * planeStrideBytes;
                    payload[idx] |= (byte)(val & 0xFF);
                    payload[idx + 1] |= (byte)((val >> 8) & 0xFF);
                }
            }
        }

        // CRC32 over the buf16 payload, appended little-endian so the MCU can drop corrupted frames.
        var crc = Crc32(payload, Magic.Length, _buf16Bytes);
        var crcOff = Magic.Length + _buf16Bytes;
        payload[crcOff] = (byte)(crc & 0xFF);
        payload[crcOff + 1] = (byte)((crc >> 8) & 0xFF);
        payload[crcOff + 2] = (byte)((crc >> 16) & 0xFF);
        payload[crcOff + 3] = (byte)((crc >> 24) & 0xFF);

        var written = write(_fd, payload, (nuint)payload.Length);
        if (written != payload.Length)
            Console.WriteLine(
                $"[SPI] short write ({written}/{payload.Length}). Raise spidev bufsiz to >= {payload.Length}.");
        }
    }

    public void Shutdown()
    {
        Dispose();
        Console.WriteLine("[SPI] renderer shut down");
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            close(_fd);
            _fd = -1;
        }
    }
}
