using RPiRgbLEDMatrix;
using SkiaSharp;
using verpixeld.Configuration;

namespace verpixeld.Hardware;

/// <summary>
///     RGB LED Matrix renderer implementation using rpi-rgb-led-matrix library
/// </summary>
public class RgbMatrixRenderer : IMatrixRenderer, IDisposable
{
    private RGBLedCanvas? _canvas;
    private bool _disposed;
    private bool _isInitialized;
    private RGBLedMatrix? _matrix;
    private MatrixOptions _config = new();

    /// <summary>
    ///     Creates a new RGB matrix renderer with default 384x192 configuration
    /// </summary>
    public RgbMatrixRenderer() : this(CreateDefaultOptions())
    {
    }

    /// <summary>
    ///     Creates a new RGB matrix renderer from application <see cref="MatrixOptions" /> configuration
    ///     (bound from the "Matrix" section of appsettings.json). Image correction (gamma/contrast/etc.)
    ///     is applied globally in the render loop for every output mode, so this renderer only uploads.
    /// </summary>
    public RgbMatrixRenderer(MatrixOptions config) : this(MapOptions(config))
    {
        _config = config;
    }

    /// <summary>Live brightness (1..100). Other matrix options need a recreate / restart.</summary>
    public void ApplyBrightness(int percent)
    {
        var v = Math.Clamp(percent, 1, 100);
        _config.Brightness = v;
        if (_matrix != null)
            _matrix.Brightness = (byte)v;
    }

    public MatrixOptions Snapshot() => _config;

    /// <summary>
    ///     Creates a new RGB matrix renderer with custom options
    /// </summary>
    public RgbMatrixRenderer(RGBLedMatrixOptions options)
    {
        Options = options;

        // Calculate dimensions based on options
        // Width = Cols * ChainLength, Height = Rows * Parallel
        Width = options.Cols * options.ChainLength;
        Height = options.Rows * options.Parallel;
    }

    /// <summary>
    ///     Matrix configuration options
    /// </summary>
    public RGBLedMatrixOptions Options { get; }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    // Raw config dimensions. The ACTUAL visible canvas can differ when a pixel mapper reshapes the layout
    // (e.g. StackToRow / V-mapper turning 2 vertically-stacked parallel chains into a 256x128 row), so
    // Width/Height are refreshed from the real canvas in Initialize().
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    ///     Initialize the matrix hardware
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized)
            return;

        Console.WriteLine("[MATRIX] Initializing RGB LED Matrix...");
        Console.WriteLine(
            $"[MATRIX] Configuration: {Options.Cols}x{Options.Rows} × {Options.ChainLength} chains × {Options.Parallel} parallel");

        EnsureNativeSafe(Options);
        _matrix = new RGBLedMatrix(Options);
        _canvas = _matrix.CreateOffscreenCanvas();

        // Adopt the real (post-pixel-mapper) canvas size so the compositor renders at the visible resolution.
        var mappedW = _canvas.Width;
        var mappedH = _canvas.Height;
        if (mappedW > 0 && mappedH > 0 && (mappedW != Width || mappedH != Height))
        {
            Console.WriteLine(
                $"[MATRIX] Pixel mapper reshaped canvas: {Width}x{Height} (raw) -> {mappedW}x{mappedH} (visible)");
            Width = mappedW;
            Height = mappedH;
        }

        Console.WriteLine($"[MATRIX] Total resolution: {Width}x{Height}");

        _isInitialized = true;
        Console.WriteLine("[MATRIX] Hardware initialized successfully");
    }

    /// <summary>
    ///     Render a frame to the matrix
    /// </summary>
    public void RenderFrame(SKBitmap bitmap)
    {
        if (!_isInitialized || _matrix == null || _canvas == null)
            throw new InvalidOperationException("Matrix not initialized. Call Initialize() first.");

        if (bitmap.Width != Width || bitmap.Height != Height)
            throw new ArgumentException(
                $"Bitmap size ({bitmap.Width}x{bitmap.Height}) doesn't match matrix size ({Width}x{Height})");

        // Hand the (already globally corrected) BGRA buffer straight to the native bulk upload.
        _canvas.SetPixelsBgra(0, 0, bitmap.Width, bitmap.Height, bitmap.GetPixels());
        _matrix.SwapOnVsync(_canvas);
    }

    /// <summary>
    ///     Shutdown the matrix hardware
    /// </summary>
    public void Shutdown()
    {
        Console.WriteLine("[MATRIX] Shutting down...");
        Dispose();
    }

    private static RGBLedMatrixOptions CreateDefaultOptions()
    {
        return new RGBLedMatrixOptions
        {
            ChainLength = 3,
            Cols = 128,
            Parallel = 3,
            RowAddressType = 3,
            Rows = 64,
            GpioSlowdown = 5,
            PwmLsbNanoseconds = 60 // 60 for Raspbian, 55 for DietPi
        };
    }

    /// <summary>
    ///     FM6126A / standard HUB75 max out at 64 scan rows. 128 is only valid for
    ///     SPWM chips (FM6363, ICND1065L, …). Passing 128 + FM6126A into native
    ///     used to SIGSEGV on older librgbmatrix.so instead of returning an error.
    /// </summary>
    internal static bool PanelAllowsExtendedRows(string? panelType)
    {
        if (string.IsNullOrWhiteSpace(panelType)) return false;
        var p = panelType.Trim();
        return p.StartsWith("FM6363", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("FM6353", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("ICND1065", StringComparison.OrdinalIgnoreCase)
               || p.StartsWith("SM16380", StringComparison.OrdinalIgnoreCase);
    }

    internal static int MaxRowsForPanel(string? panelType) =>
        PanelAllowsExtendedRows(panelType) ? 128 : 64;

    internal static string? ValidateMatrixOptions(MatrixOptions config)
    {
        try
        {
            EnsureNativeSafe(MapOptions(config));
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    internal static void EnsureNativeSafe(RGBLedMatrixOptions options)
    {
        var maxRows = MaxRowsForPanel(options.PanelType);
        if (options.Rows < 8 || options.Rows > maxRows || options.Rows % 2 != 0)
        {
            var hint = options.Cols >= 128 && options.ChainLength <= 1 && options.Parallel <= 1
                ? " This looks like a canvas/network wall size (e.g. 256×128). GPIO needs the HUB75 panel layout — typically 64×64 × 6 chains × 3 parallel."
                : "";
            throw new ArgumentException(
                $"GPIO Matrix.Rows={options.Rows} is invalid for panel '{options.PanelType ?? "default"}' (allowed 8..{maxRows}, even).{hint}");
        }

        if (options.Cols < 16)
            throw new ArgumentException($"GPIO Matrix.Cols={options.Cols} is too small (minimum 16).");
        if (options.ChainLength < 1)
            throw new ArgumentException("GPIO Matrix.ChainLength must be >= 1.");
        if (options.Parallel < 1 || options.Parallel > 3)
            throw new ArgumentException($"GPIO Matrix.Parallel={options.Parallel} is invalid (1..3).");
    }

    /// <summary>
    ///     Maps the application <see cref="MatrixOptions" /> (appsettings.json "Matrix" section)
    ///     onto the native <see cref="RGBLedMatrixOptions" />. Empty strings are treated as
    ///     "use library default" (null).
    /// </summary>
    internal static RGBLedMatrixOptions MapOptions(MatrixOptions config)
    {
        return new RGBLedMatrixOptions
        {
            Rows = config.Rows,
            Cols = config.Cols,
            ChainLength = config.ChainLength,
            Parallel = config.Parallel,
            GpioSlowdown = config.GpioSlowdown,
            PwmBits = config.PwmBits,
            PwmLsbNanoseconds = config.PwmLsbNanoseconds,
            PwmDitherBits = config.PwmDitherBits,
            Brightness = config.Brightness,
            RowAddressType = config.RowAddressType,
            ScanMode = (ScanModes)config.ScanMode,
            Multiplexing = (Multiplexing)config.Multiplexing,
            LimitRefreshRateHz = config.LimitRefreshRateHz,
            LedRgbSequence = string.IsNullOrWhiteSpace(config.LedRgbSequence) ? null : config.LedRgbSequence,
            PixelMapperConfig = string.IsNullOrWhiteSpace(config.PixelMapperConfig) ? null : config.PixelMapperConfig,
            PanelType = string.IsNullOrWhiteSpace(config.PanelType) ? null : config.PanelType,
            HardwareMapping = string.IsNullOrWhiteSpace(config.HardwareMapping) ? null : config.HardwareMapping,
            DisableHardwarePulsing = config.DisableHardwarePulsing,
            DisableBusyWaiting = config.DisableBusyWaiting,
            ShowRefreshRate = config.ShowRefreshRate,
            InverseColors = config.InverseColors
        };
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Matrix library handles its own cleanup
            _matrix = null;
            _canvas = null;
        }

        _disposed = true;
        _isInitialized = false;
    }
}
