using SkiaSharp;
using verpixeld.Configuration;

namespace verpixeld.Services;

/// <summary>
///     Global, live-adjustable image correction shared by ALL output modes. Applied once per frame in the
///     render loop to the composed BGRA bitmap (in place), so network, gpio/hardware, hdmi, spi and the web
///     preview all see the same corrected picture. Rebuilt on demand (gamma / contrast / brightness / white
///     balance) and swapped by reference so the render thread never reads a half-written table.
///     Identity settings disable the path entirely (zero per-pixel cost).
/// </summary>
public sealed class ImageCorrectionService
{
    private readonly object _lock = new();
    private byte[] _lutR = BuildIdentity();
    private byte[] _lutG = BuildIdentity();
    private byte[] _lutB = BuildIdentity();
    private volatile bool _active;

    public string Curve { get; private set; } = "none";
    public double Gamma { get; private set; } = 2.2;
    public double Contrast { get; private set; } = 1.0;
    public double Brightness { get; private set; } = 1.0;
    public double GainR { get; private set; } = 1.0;
    public double GainG { get; private set; } = 1.0;
    public double GainB { get; private set; } = 1.0;

    /// <summary>Whether a non-identity correction is currently applied.</summary>
    public bool Active => _active;

    /// <summary>Raised whenever the settings change, so renderers that bake the correction into their own
    ///     (higher-depth) pipeline (e.g. the network path) can rebuild their LUT live.</summary>
    public event Action<ImageCorrectionService>? Changed;

    public ImageCorrectionService(ImageCorrectionOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        Set(o.Curve, o.Gamma, o.Contrast, o.Brightness, o.GainR, o.GainG, o.GainB);
    }

    /// <summary>Update the correction at runtime (live, no restart). Thread-safe w.r.t. the render thread.</summary>
    public void Set(string curve, double gamma, double contrast, double brightness,
                    double gainR, double gainG, double gainB)
    {
        Curve = string.IsNullOrWhiteSpace(curve) ? "none" : curve.Trim().ToLowerInvariant();
        Gamma = gamma <= 0 ? 2.2 : gamma;
        Contrast = contrast <= 0 ? 1.0 : contrast;
        Brightness = Math.Clamp(brightness <= 0 ? 1.0 : brightness, 0.0, 4.0);
        GainR = Math.Clamp(gainR, 0.0, 4.0);
        GainG = Math.Clamp(gainG, 0.0, 4.0);
        GainB = Math.Clamp(gainB, 0.0, 4.0);

        var r = BuildLut(GainR);
        var g = BuildLut(GainG);
        var b = BuildLut(GainB);
        lock (_lock) { _lutR = r; _lutG = g; _lutB = b; }

        _active = !(Curve == "none"
                    && Math.Abs(Contrast - 1.0) < 1e-6
                    && Math.Abs(Brightness - 1.0) < 1e-6
                    && Math.Abs(GainR - 1.0) < 1e-6
                    && Math.Abs(GainG - 1.0) < 1e-6
                    && Math.Abs(GainB - 1.0) < 1e-6);

        Changed?.Invoke(this);
    }

    /// <summary>Apply the correction to a BGRA8888 bitmap in place. No-op when identity.</summary>
    public unsafe void Apply(SKBitmap bitmap)
    {
        if (!_active) return;
        var pixels = bitmap.GetPixels();
        if (pixels == IntPtr.Zero) return;

        byte[] lr, lg, lb;
        lock (_lock) { lr = _lutR; lg = _lutG; lb = _lutB; }

        var total = bitmap.Width * bitmap.Height * 4;
        var p = (byte*)pixels.ToPointer();
        // SKBitmap is BGRA8888: [0]=B [1]=G [2]=R [3]=A (alpha untouched).
        for (var i = 0; i < total; i += 4)
        {
            p[i] = lb[p[i]];
            p[i + 1] = lg[p[i + 1]];
            p[i + 2] = lr[p[i + 2]];
        }
    }

    private byte[] BuildLut(double gain)
    {
        var scale = gain * Brightness;
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            double y = Curve switch
            {
                "gamma" => Math.Pow(i / 255.0, Gamma),
                "cie1931" => CieLuminance(i / 255.0 * 100.0),
                _ => i / 255.0
            };
            y = (y - 0.5) * Contrast + 0.5;
            if (y < 0) y = 0; else if (y > 1) y = 1;
            lut[i] = (byte)Math.Clamp(Math.Round(y * scale * 255.0), 0, 255);
        }
        return lut;
    }

    private static double CieLuminance(double l) =>
        l <= 8.0 ? l / 903.3 : Math.Pow((l + 16.0) / 116.0, 3.0);

    private static byte[] BuildIdentity()
    {
        var lut = new byte[256];
        for (var v = 0; v < 256; v++) lut[v] = (byte)v;
        return lut;
    }
}
