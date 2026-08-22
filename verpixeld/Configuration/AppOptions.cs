namespace verpixeld.Configuration;

/// <summary>
///     Application configuration options
/// </summary>
public class AppOptions
{
    public const string SectionName = "App";

    /// <summary>
    ///     Display width in pixels
    /// </summary>
    public int DisplayWidth { get; set; } = 384;

    /// <summary>
    ///     Display height in pixels
    /// </summary>
    public int DisplayHeight { get; set; } = 192;

    /// <summary>
    ///     Target frames per second. Wired to the render loop's drift-free scheduler. 60 matches the
    ///     typical HDMI/sender-card refresh so the output does not judder from a 40-into-60 cadence.
    /// </summary>
    public int TargetFps { get; set; } = 60;

    /// <summary>
    ///     Enable verbose logging
    /// </summary>
    public bool VerboseLogging { get; set; } = false;

    /// <summary>
    ///     Enable simulation mode (no hardware)
    /// </summary>
    public bool SimulationMode { get; set; } = false;

    /// <summary>
    ///     Output backend selection. Overrides <see cref="SimulationMode" /> when set to a non-empty value.
    ///     "gpio" (default) drives the panel directly via rpi-rgb-led-matrix (GPIO bit-bang / SPWM).
    ///     "hdmi" renders the composed frame into the Linux framebuffer (/dev/fb0) so the Pi's HDMI output
    ///     feeds an external LED sender card (Colorlight/Novastar-style) that maps the region 1:1 to the wall.
    ///     "simulation" runs the full pipeline with no hardware output (web preview only).
    /// </summary>
    public string OutputMode { get; set; } = "";

    /// <summary>
    ///     BDF font name to use for the startup/intro screen (e.g. "6x10", "10x20").
    ///     Empty = auto-pick the best-fitting font for the display height.
    /// </summary>
    public string StartupFont { get; set; } = "";

    /// <summary>
    ///     Default BDF font name for text rendering (scrolling text, BDF labels, etc.).
    ///     Empty = let the framework auto-discover / keep its default.
    /// </summary>
    public string DefaultFont { get; set; } = "";
}

/// <summary>
///     HDMI / Linux-framebuffer output configuration (used when App.OutputMode = "hdmi").
///     The Pi outputs a normal HDMI signal; an LED sender card captures a rectangular region of that
///     signal and maps it to the LED wall. verpixeld writes its composed frame into that region of the
///     framebuffer. Requires read/write access to the framebuffer device (run as root or in the "video" group).
/// </summary>
public class HdmiOptions
{
    public const string SectionName = "Hdmi";

    /// <summary>Linux framebuffer device that mirrors the HDMI output. Default "/dev/fb0".</summary>
    public string FramebufferDevice { get; set; } = "/dev/fb0";

    /// <summary>
    ///     Wall width in pixels the sender card expects. &lt;= 0 derives it from the Matrix config
    ///     (Cols × ChainLength), so an existing panel layout is reused.
    /// </summary>
    public int WallWidth { get; set; } = 0;

    /// <summary>Wall height in pixels. &lt;= 0 derives from the Matrix config (Rows × Parallel).</summary>
    public int WallHeight { get; set; } = 0;

    /// <summary>Left offset (px) of the mapped region inside the HDMI frame. Sender cards usually capture at 0,0.</summary>
    public int OffsetX { get; set; } = 0;

    /// <summary>Top offset (px) of the mapped region inside the HDMI frame.</summary>
    public int OffsetY { get; set; } = 0;

    /// <summary>
    ///     Integer upscale factor (nearest-neighbour). 1 = 1:1 (default, correct for a sender card that maps
    ///     pixels directly). Use &gt;1 only to preview a small wall on a large regular monitor.
    /// </summary>
    public int Scale { get; set; } = 1;

    /// <summary>Fill the whole framebuffer with black once on start so nothing but the wall region is lit.</summary>
    public bool ClearScreenOnStart { get; set; } = true;

    /// <summary>
    ///     Swap the red and blue channels when writing pixels. Leave false for the common Pi 32bpp XRGB/BGRX
    ///     framebuffer (Skia's BGRA maps directly). Set true if the wall shows red/blue swapped.
    /// </summary>
    public bool SwapRedBlue { get; set; } = false;
}

/// <summary>
///     SPI output configuration (used when App.OutputMode = "spi"). The Pi is the SPI master and streams
///     each composed frame to an RP2040 "receiver" (PIO SPI slave + DMA) that drives the LED panel. Each
///     frame is sent as the 4-byte magic "VPX1" followed by Width×Height RGB565 pixels (row-major).
///     Requires SPI enabled (dtparam=spi=on) and, to send a whole frame in one transfer, a large spidev
///     buffer: options spidev bufsiz=70000 in /etc/modprobe.d/spidev.conf.
/// </summary>
public class SpiOptions
{
    public const string SectionName = "Spi";

    /// <summary>SPI device the RP2040 receiver hangs on. Default "/dev/spidev0.0" (bus 0, CE0).</summary>
    public string Device { get; set; } = "/dev/spidev0.0";

    /// <summary>SPI clock in Hz. The RP2040 receiver is stable up to ~49 MHz; 40 MHz keeps margin.</summary>
    public int SpeedHz { get; set; } = 40000000;

    /// <summary>Wall width in pixels. &lt;= 0 derives from the Matrix config (Cols × ChainLength).</summary>
    public int WallWidth { get; set; } = 0;

    /// <summary>Wall height in pixels. &lt;= 0 derives from the Matrix config (Rows × Parallel).</summary>
    public int WallHeight { get; set; } = 0;

    /// <summary>Swap the red and blue channels in the RGB565 payload (set if the panel shows R/B swapped).</summary>
    public bool SwapRedBlue { get; set; } = false;
}

/// <summary>
///     Ethernet/UDP output (used when App.OutputMode = "network"). Streams the packed 8-bit panel buffer
///     to an RP2350 + W5500 "udpfast" receiver, fragmented and rate-paced so the W5500's 16 KB RX buffer
///     never overflows. Scales to many panels (one IP per panel). No extra Pi wiring - just the LAN.
/// </summary>
public class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>IP of the RP2350 + W5500 receiver (its static W5_IP).</summary>
    public string Host { get; set; } = "192.168.1.50";

    /// <summary>UDP port the receiver listens on.</summary>
    public int Port { get; set; } = 7777;

    /// <summary>Send pacing in Mbit/s. Keep just below the MCU read rate (~19 at 33 MHz SPI) to avoid loss.</summary>
    public double TargetMbps { get; set; } = 19.0;

    /// <summary>Wall width in pixels. &lt;= 0 derives from the Matrix config.</summary>
    public int WallWidth { get; set; } = 0;

    /// <summary>Wall height in pixels. &lt;= 0 derives from the Matrix config.</summary>
    public int WallHeight { get; set; } = 0;

    /// <summary>Swap red/blue channels if the panel shows them swapped.</summary>
    public bool SwapRedBlue { get; set; } = false;

    /// <summary>
    ///     Colour depth in bits per channel: 8, 10, 13 or 14. MUST match the flashed MCU firmware's
    ///     HUB_NPLANES (the payload size scales with it). Higher depth = finer gradients, at the cost of more
    ///     network data. 14 is the ICND1065L maximum; the W6300 quad-SPI has the bandwidth for it.
    /// </summary>
    public int ColorBits { get; set; } = 14;

    /// <summary>
    ///     RP2350 unique-board-id (16 hex chars) of the bound panel. When set, verpixeld re-resolves
    ///     the current DHCP IP at startup via UDP 7778 / HTTP /status so a lease change does not
    ///     require editing Host by hand.
    /// </summary>
    public string PanelId { get; set; } = "";

    // NOTE: gamma / contrast / brightness / white-balance are now GLOBAL (see ImageCorrectionOptions),
    // applied once in the render loop for every output mode. Only the panel-wiring swap and the per-column
    // seam correction remain network-specific.

    /// <summary>
    ///     Path to the per-column seam-correction file (hot-reloaded live). Corrects the driver-cascade
    ///     boundary columns (e.g. 64/128/192) that render too dark in shadows and too bright in highlights.
    ///     Relative paths resolve next to the executable. A neutral template is created on first run.
    /// </summary>
    public string SeamCorrectionFile { get; set; } = "seam_correction.json";
}

/// <summary>
///     Web server configuration options
/// </summary>
public class WebServerOptions
{
    public const string SectionName = "WebServer";

    /// <summary>
    ///     HTTP port number
    /// </summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>
    ///     HTTPS port number
    /// </summary>
    public int HttpsPort { get; set; } = 5001;

    /// <summary>
    ///     Enable HTTPS
    /// </summary>
    public bool EnableHttps { get; set; } = true;

    /// <summary>
    ///     Path to SSL certificate file
    /// </summary>
    public string CertificatePath { get; set; } = "server.pfx";

    /// <summary>
    ///     Certificate password
    /// </summary>
    public string CertificatePassword { get; set; } = "ledmatrix";
}

/// <summary>
///     Home Assistant connection (WebSocket API + long-lived access token). The token is read here,
///     server-side only, and never exposed to extensions or saved layouts.
/// </summary>
public class HomeAssistantOptions
{
    public const string SectionName = "HomeAssistant";

    /// <summary>Enable the Home Assistant connection.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Base URL of Home Assistant, e.g. "http://homeassistant.local:8123" or "http://192.168.1.10:8123".
    ///     The service derives the WebSocket URL (ws(s)://.../api/websocket) from this.
    /// </summary>
    public string BaseUrl { get; set; } = "http://homeassistant.local:8123";

    /// <summary>Long-lived access token (HA profile → Long-lived access tokens).</summary>
    public string Token { get; set; } = "";

    /// <summary>Appearance of the wall toast overlay for persistent notifications.</summary>
    public HomeAssistantToastOptions Toast { get; set; } = new();

    /// <summary>
    ///     When true, the wall registers as an MQTT device in Home Assistant (notify, toast text,
    ///     last-toast sensor, layout, night-mode schedule, night-active, brightness). Requires the
    ///     MQTT integration in HA (Mosquitto addon is enough).
    /// </summary>
    public bool ExposeDevice { get; set; } = true;
}

/// <summary>
///     Wall-toast overlay (bottom banner). Per-toast severity can still override the accent:
///     prefix the HA <c>notification_id</c> with <c>error:</c>, <c>warning:</c>, <c>success:</c>, or <c>info:</c>.
/// </summary>
public class HomeAssistantToastOptions
{
    public bool Enabled { get; set; } = true;
    public int DurationMs { get; set; } = 8000;
    /// <summary>BDF font name. Empty = pick a size that fits the bar.</summary>
    public string Font { get; set; } = "";
    public string Background { get; set; } = "#121620";
    public string TitleColor { get; set; } = "#FFFFFF";
    public string MessageColor { get; set; } = "#C8D2DC";
    public string InfoAccent { get; set; } = "#03A9F4";
    public string WarningAccent { get; set; } = "#FFC107";
    public string ErrorAccent { get; set; } = "#F44336";
    public string SuccessAccent { get; set; } = "#4CAF50";
    public string DefaultSeverity { get; set; } = "info";

    public HomeAssistantToastOptions Clone() => new()
    {
        Enabled = Enabled,
        DurationMs = DurationMs,
        Font = Font,
        Background = Background,
        TitleColor = TitleColor,
        MessageColor = MessageColor,
        InfoAccent = InfoAccent,
        WarningAccent = WarningAccent,
        ErrorAccent = ErrorAccent,
        SuccessAccent = SuccessAccent,
        DefaultSeverity = DefaultSeverity
    };

    public string AccentFor(string severity) => severity switch
    {
        "error" => ErrorAccent,
        "warning" => WarningAccent,
        "success" => SuccessAccent,
        _ => InfoAccent
    };
}

/// <summary>
///     LED Matrix hardware configuration
/// </summary>
public class MatrixOptions
{
    public const string SectionName = "Matrix";

    /// <summary>
    ///     Number of rows per panel
    /// </summary>
    public int Rows { get; set; } = 64;

    /// <summary>
    ///     Number of columns per panel
    /// </summary>
    public int Cols { get; set; } = 64;

    /// <summary>
    ///     Number of chained panels
    /// </summary>
    public int ChainLength { get; set; } = 6;

    /// <summary>
    ///     Number of parallel chains
    /// </summary>
    public int Parallel { get; set; } = 3;

    /// <summary>
    ///     GPIO slowdown factor (needed for faster Pis / slower panels)
    /// </summary>
    public int GpioSlowdown { get; set; } = 4;

    /// <summary>
    ///     PWM bits for color depth (1..11). Lower uses less CPU and increases refresh rate.
    /// </summary>
    public int PwmBits { get; set; } = 11;

    /// <summary>
    ///     Base time-unit (ns) for the lowest significant bit. Higher = better color quality,
    ///     lower frame rate. Library default is 130.
    /// </summary>
    public int PwmLsbNanoseconds { get; set; } = 130;

    /// <summary>
    ///     Lower bits can be time-dithered for a higher refresh rate.
    /// </summary>
    public int PwmDitherBits { get; set; } = 0;

    /// <summary>
    ///     Initial panel brightness in percent (1..100).
    /// </summary>
    public int Brightness { get; set; } = 100;

    /// <summary>
    ///     Row address type. 0 = direct row select (default), 1 = AB-addressed (some 64x64 panels),
    ///     2/3/4/5 = other addressing schemes (e.g. some outdoor/64x64 panels need 3).
    /// </summary>
    public int RowAddressType { get; set; } = 0;

    /// <summary>
    ///     Scan mode. 0 = Progressive, 1 = Interlaced.
    /// </summary>
    public int ScanMode { get; set; } = 0;

    /// <summary>
    ///     Multiplexing type (0 = Direct/none, 1 = Stripe, 2 = Checker, ... see panel docs).
    /// </summary>
    public int Multiplexing { get; set; } = 0;

    /// <summary>
    ///     Limit the LED panel refresh rate in Hz. &lt;= 0 means no limit.
    /// </summary>
    public int LimitRefreshRateHz { get; set; } = 0;

    /// <summary>
    ///     Real RGB color ordering when a panel mixes up colors (e.g. "RGB", "RBG", "BGR").
    ///     Empty/null uses the library default ("RGB").
    /// </summary>
    public string? LedRgbSequence { get; set; } = null;

    /// <summary>
    ///     Semicolon-separated pixel-mapper configuration (e.g. "U-mapper;Rotate:90").
    /// </summary>
    public string? PixelMapperConfig { get; set; } = null;

    /// <summary>
    ///     Disable hardware pulsing (use when output-enable is not on GPIO 18).
    /// </summary>
    public bool DisableHardwarePulsing { get; set; } = false;

    /// <summary>
    ///     Sleep instead of busy-waiting when limiting the refresh rate. Frees a CPU core for the rest of the
    ///     app on a loaded/CPU-bound Pi (slightly less precise frame timing). Pair with LimitRefreshRateHz.
    /// </summary>
    public bool DisableBusyWaiting { get; set; } = false;

    /// <summary>
    ///     Show the measured refresh rate on the console.
    /// </summary>
    public bool ShowRefreshRate { get; set; } = false;

    /// <summary>
    ///     Invert all colors.
    /// </summary>
    public bool InverseColors { get; set; } = false;

    /// <summary>
    ///     Panel type identifier. Certain panels (e.g. FM6126A / FM6127) need an init sequence.
    ///     Empty/null for standard panels.
    /// </summary>
    public string PanelType { get; set; } = "FM6126A";

    /// <summary>
    ///     Hardware GPIO mapping (e.g. "regular", "adafruit-hat", "adafruit-hat-pwm").
    /// </summary>
    public string HardwareMapping { get; set; } = "regular";
}

/// <summary>
///     Global image correction applied to the composed frame for EVERY output mode (network, gpio/hardware,
///     hdmi, spi, simulation) and the web preview. Applied once in the render loop as an 8-bit-in / 8-bit-out
///     per-channel lookup table, so a single set of controls governs how the picture looks on the wall.
///     All defaults are identity, so the pipeline is byte-for-byte unchanged and free until you tune it.
/// </summary>
public class ImageCorrectionOptions
{
    public const string SectionName = "ImageCorrection";

    /// <summary>Tone curve: "none" (linear, default), "gamma" (power law) or "cie1931" (perceptual L*).</summary>
    public string Curve { get; set; } = "none";

    /// <summary>Gamma exponent used when <see cref="Curve" /> = "gamma".</summary>
    public double Gamma { get; set; } = 2.2;

    /// <summary>Contrast around mid-grey. 1.0 = unchanged, &gt;1 steeper, &lt;1 flatter.</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>Master brightness multiplier (0..4). 1.0 = unchanged.</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>Per-channel white-balance gain (0..4). 1.0 = unchanged.</summary>
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
}
