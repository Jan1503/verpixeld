using System.Text.Json;
using System.Text.Json.Nodes;
using CanvasManagement;
using CanvasManagement.BdfFontManager;
using CanvasManagement.Interfaces;
using verpixeld.Configuration;
using verpixeld.Hardware;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Live output / per-backend / Home Assistant settings. Complements <see cref="SettingsEndpoints"/>
///     with a single GET of the full settings tree and PUT endpoints that apply immediately when the
///     running backend can take them (and persist without wiping sibling appsettings.json sections).
/// </summary>
public static class OutputSettingsEndpoints
{
    public static void MapOutputSettingsEndpoints(this WebApplication app)
    {
        var output = app.Services.GetRequiredService<OutputRuntime>();
        var canvasManager = app.Services.GetRequiredService<CanvasManager>();
        var homeAssistant = app.Services.GetRequiredService<HomeAssistantService>();
        var group = app.MapGroup("/api/settings");

        group.MapGet("/outputs", () =>
        {
            try { return ApiResponse.Ok(BuildSnapshot(output, homeAssistant)); }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        // Body: { mode: "network"|"hdmi"|"spi"|"gpio"|"simulation" }
        group.MapPut("/output", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadObject(ctx);
                var mode = GetString(body, "mode", output.Mode);
                var result = output.SetMode(mode);
                return ApiResponse.Ok(new
                {
                    success = result.Success,
                    activeMode = result.ActiveMode,
                    savedMode = result.SavedMode,
                    requiresRestart = result.RequiresRestart,
                    message = result.Message
                }, result.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] output switch: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        group.MapPut("/hdmi", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadObject(ctx);
                var h = output.Hdmi;
                h.FramebufferDevice = GetString(body, "framebufferDevice", h.FramebufferDevice);
                h.WallWidth = GetInt(body, "wallWidth", h.WallWidth);
                h.WallHeight = GetInt(body, "wallHeight", h.WallHeight);
                h.OffsetX = GetInt(body, "offsetX", h.OffsetX);
                h.OffsetY = GetInt(body, "offsetY", h.OffsetY);
                h.Scale = Math.Max(1, GetInt(body, "scale", h.Scale));
                h.ClearScreenOnStart = GetBool(body, "clearScreenOnStart", h.ClearScreenOnStart);
                h.SwapRedBlue = GetBool(body, "swapRedBlue", h.SwapRedBlue);

                output.As<HdmiMatrixRenderer>()?.Reconfigure(h);
                PersistSection("Hdmi", new Dictionary<string, JsonNode?>
                {
                    ["FramebufferDevice"] = h.FramebufferDevice,
                    ["WallWidth"] = h.WallWidth,
                    ["WallHeight"] = h.WallHeight,
                    ["OffsetX"] = h.OffsetX,
                    ["OffsetY"] = h.OffsetY,
                    ["Scale"] = h.Scale,
                    ["ClearScreenOnStart"] = h.ClearScreenOnStart,
                    ["SwapRedBlue"] = h.SwapRedBlue
                });

                var live = output.As<HdmiMatrixRenderer>() != null;
                return ApiResponse.Ok(new { appliedLive = live, hdmi = h },
                    live ? "HDMI settings applied live." : "HDMI settings saved. Switch output to HDMI (or restart) to apply device/size.");
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        group.MapPut("/spi", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadObject(ctx);
                var s = output.Spi;
                s.Device = GetString(body, "device", s.Device);
                s.SpeedHz = Math.Max(1, GetInt(body, "speedHz", s.SpeedHz));
                s.WallWidth = GetInt(body, "wallWidth", s.WallWidth);
                s.WallHeight = GetInt(body, "wallHeight", s.WallHeight);
                s.SwapRedBlue = GetBool(body, "swapRedBlue", s.SwapRedBlue);

                output.As<SpiMatrixRenderer>()?.Reconfigure(s);
                PersistSection("Spi", new Dictionary<string, JsonNode?>
                {
                    ["Device"] = s.Device,
                    ["SpeedHz"] = s.SpeedHz,
                    ["WallWidth"] = s.WallWidth,
                    ["WallHeight"] = s.WallHeight,
                    ["SwapRedBlue"] = s.SwapRedBlue
                });

                var live = output.As<SpiMatrixRenderer>() != null;
                return ApiResponse.Ok(new { appliedLive = live, spi = s },
                    live ? "SPI settings applied live." : "SPI settings saved.");
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        group.MapPut("/hardware", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadObject(ctx);
                var m = output.Matrix;
                var oldW = m.Cols * m.ChainLength;
                var oldH = m.Rows * m.Parallel;

                var rows = Clamp(GetInt(body, "rows", m.Rows), 8, 128);
                var cols = Clamp(GetInt(body, "cols", m.Cols), 8, 256);
                var chain = Clamp(GetInt(body, "chainLength", m.ChainLength), 1, 16);
                var parallel = Clamp(GetInt(body, "parallel", m.Parallel), 1, 3);
                var panelType = GetString(body, "panelType", m.PanelType);
                var geometryError = RgbMatrixRenderer.ValidateMatrixOptions(new MatrixOptions
                {
                    Rows = rows,
                    Cols = cols,
                    ChainLength = chain,
                    Parallel = parallel,
                    PanelType = panelType
                });
                if (geometryError != null)
                    return ApiResponse.Fail(geometryError);

                m.Rows = rows;
                m.Cols = cols;
                m.ChainLength = chain;
                m.Parallel = parallel;
                m.PanelType = panelType;
                m.GpioSlowdown = Clamp(GetInt(body, "gpioSlowdown", m.GpioSlowdown), 0, 10);
                m.PwmBits = Clamp(GetInt(body, "pwmBits", m.PwmBits), 1, 11);
                m.PwmLsbNanoseconds = Clamp(GetInt(body, "pwmLsbNanoseconds", m.PwmLsbNanoseconds), 50, 3000);
                m.PwmDitherBits = Clamp(GetInt(body, "pwmDitherBits", m.PwmDitherBits), 0, 2);
                m.Brightness = Clamp(GetInt(body, "brightness", m.Brightness), 1, 100);
                m.RowAddressType = Clamp(GetInt(body, "rowAddressType", m.RowAddressType), 0, 5);
                m.ScanMode = Clamp(GetInt(body, "scanMode", m.ScanMode), 0, 1);
                m.Multiplexing = Clamp(GetInt(body, "multiplexing", m.Multiplexing), 0, 17);
                m.LimitRefreshRateHz = Math.Max(0, GetInt(body, "limitRefreshRateHz", m.LimitRefreshRateHz));
                m.LedRgbSequence = GetString(body, "ledRgbSequence", m.LedRgbSequence ?? "");
                m.PixelMapperConfig = GetString(body, "pixelMapperConfig", m.PixelMapperConfig ?? "");
                m.DisableHardwarePulsing = GetBool(body, "disableHardwarePulsing", m.DisableHardwarePulsing);
                m.DisableBusyWaiting = GetBool(body, "disableBusyWaiting", m.DisableBusyWaiting);
                m.ShowRefreshRate = GetBool(body, "showRefreshRate", m.ShowRefreshRate);
                m.InverseColors = GetBool(body, "inverseColors", m.InverseColors);
                m.HardwareMapping = GetString(body, "hardwareMapping", m.HardwareMapping);

                var gpio = output.As<RgbMatrixRenderer>();
                gpio?.ApplyBrightness(m.Brightness);

                var newW = m.Cols * m.ChainLength;
                var newH = m.Rows * m.Parallel;
                var geometryChanged = newW != oldW || newH != oldH;

                PersistSection("Matrix", new Dictionary<string, JsonNode?>
                {
                    ["Rows"] = m.Rows,
                    ["Cols"] = m.Cols,
                    ["ChainLength"] = m.ChainLength,
                    ["Parallel"] = m.Parallel,
                    ["GpioSlowdown"] = m.GpioSlowdown,
                    ["PwmBits"] = m.PwmBits,
                    ["PwmLsbNanoseconds"] = m.PwmLsbNanoseconds,
                    ["PwmDitherBits"] = m.PwmDitherBits,
                    ["Brightness"] = m.Brightness,
                    ["RowAddressType"] = m.RowAddressType,
                    ["ScanMode"] = m.ScanMode,
                    ["Multiplexing"] = m.Multiplexing,
                    ["LimitRefreshRateHz"] = m.LimitRefreshRateHz,
                    ["LedRgbSequence"] = m.LedRgbSequence,
                    ["PixelMapperConfig"] = m.PixelMapperConfig,
                    ["DisableHardwarePulsing"] = m.DisableHardwarePulsing,
                    ["DisableBusyWaiting"] = m.DisableBusyWaiting,
                    ["ShowRefreshRate"] = m.ShowRefreshRate,
                    ["InverseColors"] = m.InverseColors,
                    ["PanelType"] = m.PanelType,
                    ["HardwareMapping"] = m.HardwareMapping
                });

                var requiresRestart = gpio == null || geometryChanged ||
                                      Has(body, "rows") || Has(body, "cols") || Has(body, "chainLength") ||
                                      Has(body, "parallel") || Has(body, "panelType") || Has(body, "hardwareMapping") ||
                                      Has(body, "pwmBits") || Has(body, "rowAddressType") || Has(body, "multiplexing") ||
                                      Has(body, "pixelMapperConfig") || Has(body, "gpioSlowdown");

                return ApiResponse.Ok(new
                {
                    appliedLive = gpio != null,
                    requiresRestart,
                    matrix = m
                }, requiresRestart
                    ? "Hardware settings saved. Geometry / panel / PWM changes need a restart."
                    : "Hardware brightness applied live; remaining fields saved.");
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        group.MapPut("/homeassistant", async (HttpContext ctx) =>
        {
            try
            {
                var body = await ReadObject(ctx);
                var current = homeAssistant?.Snapshot() ?? new HomeAssistantOptions();
                var next = new HomeAssistantOptions
                {
                    Enabled = GetBool(body, "enabled", current.Enabled),
                    BaseUrl = GetString(body, "baseUrl", current.BaseUrl),
                    Token = GetString(body, "token", current.Token),
                    Toast = ReadToast(body, current.Toast ?? new HomeAssistantToastOptions()),
                    ExposeDevice = GetBool(body, "exposeDevice", current.ExposeDevice)
                };

                var reconnected = homeAssistant?.Apply(next) ?? false;
                PersistSection("HomeAssistant", new Dictionary<string, JsonNode?>
                {
                    ["Enabled"] = next.Enabled,
                    ["BaseUrl"] = next.BaseUrl,
                    ["Token"] = next.Token,
                    ["Toast"] = ToastNode(next.Toast),
                    ["ExposeDevice"] = next.ExposeDevice
                });

                return ApiResponse.Ok(new
                {
                    enabled = next.Enabled,
                    baseUrl = next.BaseUrl,
                    connected = HomeAssistantBridge.Connected,
                    entityCount = HomeAssistantBridge.All().Count,
                    toast = ToastDto(next.Toast),
                    exposeDevice = next.ExposeDevice,
                    mqtt = homeAssistant?.MqttAvailable ?? false,
                    device = homeAssistant?.WallDevice?.Status(),
                    reconnected
                }, reconnected
                    ? "Home Assistant reconnecting with the new settings."
                    : "Home Assistant settings saved.");
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        group.MapGet("/homeassistant", () =>
        {
            try
            {
                var ha = homeAssistant?.Snapshot() ?? new HomeAssistantOptions();
                return ApiResponse.Ok(new
                {
                    enabled = ha.Enabled,
                    baseUrl = ha.BaseUrl,
                    token = ha.Token,
                    connected = HomeAssistantBridge.Connected,
                    entityCount = HomeAssistantBridge.All().Count,
                    toast = ToastDto(ha.Toast),
                    fonts = BdfFonts(),
                    exposeDevice = ha.ExposeDevice,
                    mqtt = homeAssistant?.MqttAvailable ?? false,
                    device = homeAssistant?.WallDevice?.Status()
                });
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });
    }

    internal static object BuildSnapshot(OutputRuntime output, HomeAssistantService? homeAssistant)
    {
        var ha = homeAssistant?.Snapshot() ?? new HomeAssistantOptions();
        var net = output.As<NetworkMatrixRenderer>();
        return new
        {
            activeMode = output.Mode,
            savedMode = string.IsNullOrWhiteSpace(output.App.OutputMode) ? output.Mode : output.App.OutputMode,
            canvas = new { width = output.Width, height = output.Height },
            app = new
            {
                displayWidth = output.App.DisplayWidth,
                displayHeight = output.App.DisplayHeight,
                targetFps = output.App.TargetFps,
                verboseLogging = output.App.VerboseLogging,
                simulationMode = output.App.SimulationMode,
                outputMode = output.App.OutputMode
            },
            matrix = output.Matrix,
            hdmi = output.Hdmi,
            spi = output.Spi,
            network = new
            {
                host = net?.Host ?? output.Network.Host,
                port = net?.Port ?? output.Network.Port,
                targetMbps = net?.TargetMbps ?? output.Network.TargetMbps,
                colorBits = net?.ColorBits ?? output.Network.ColorBits,
                wallWidth = output.Network.WallWidth,
                wallHeight = output.Network.WallHeight,
                swapRedBlue = net?.SwapRedBlue ?? output.Network.SwapRedBlue,
                seamCorrectionFile = output.Network.SeamCorrectionFile,
                panelId = output.Network.PanelId
            },
            homeAssistant = new
            {
                enabled = ha.Enabled,
                baseUrl = ha.BaseUrl,
                token = ha.Token,
                connected = HomeAssistantBridge.Connected,
                entityCount = HomeAssistantBridge.All().Count,
                toast = ToastDto(ha.Toast),
                fonts = BdfFonts(),
                exposeDevice = ha.ExposeDevice,
                mqtt = homeAssistant?.MqttAvailable ?? false,
                device = homeAssistant?.WallDevice?.Status()
            }
        };
    }

    private static object ToastDto(HomeAssistantToastOptions? t)
    {
        t ??= new HomeAssistantToastOptions();
        return new
        {
            enabled = t.Enabled,
            durationMs = t.DurationMs,
            font = t.Font,
            background = t.Background,
            titleColor = t.TitleColor,
            messageColor = t.MessageColor,
            infoAccent = t.InfoAccent,
            warningAccent = t.WarningAccent,
            errorAccent = t.ErrorAccent,
            successAccent = t.SuccessAccent,
            defaultSeverity = t.DefaultSeverity
        };
    }

    private static JsonObject ToastNode(HomeAssistantToastOptions t) => new()
    {
        ["Enabled"] = t.Enabled,
        ["DurationMs"] = t.DurationMs,
        ["Font"] = t.Font,
        ["Background"] = t.Background,
        ["TitleColor"] = t.TitleColor,
        ["MessageColor"] = t.MessageColor,
        ["InfoAccent"] = t.InfoAccent,
        ["WarningAccent"] = t.WarningAccent,
        ["ErrorAccent"] = t.ErrorAccent,
        ["SuccessAccent"] = t.SuccessAccent,
        ["DefaultSeverity"] = t.DefaultSeverity
    };

    private static string[] BdfFonts() =>
        BdfFontRegistry.RegisteredFonts.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    private static HomeAssistantToastOptions ReadToast(JsonElement root, HomeAssistantToastOptions current)
    {
        if (!root.TryGetProperty("toast", out var src) || src.ValueKind != JsonValueKind.Object)
            return current.Clone();

        var next = current.Clone();
        next.Enabled = GetBool(src, "enabled", current.Enabled);
        next.DurationMs = Clamp(GetInt(src, "durationMs", current.DurationMs), 1000, 60_000);
        next.Font = GetString(src, "font", current.Font);
        next.Background = HaToastService.NormalizeHex(GetString(src, "background", current.Background), current.Background);
        next.TitleColor = HaToastService.NormalizeHex(GetString(src, "titleColor", current.TitleColor), current.TitleColor);
        next.MessageColor = HaToastService.NormalizeHex(GetString(src, "messageColor", current.MessageColor), current.MessageColor);
        next.InfoAccent = HaToastService.NormalizeHex(GetString(src, "infoAccent", current.InfoAccent), current.InfoAccent);
        next.WarningAccent = HaToastService.NormalizeHex(GetString(src, "warningAccent", current.WarningAccent), current.WarningAccent);
        next.ErrorAccent = HaToastService.NormalizeHex(GetString(src, "errorAccent", current.ErrorAccent), current.ErrorAccent);
        next.SuccessAccent = HaToastService.NormalizeHex(GetString(src, "successAccent", current.SuccessAccent), current.SuccessAccent);
        var sev = GetString(src, "defaultSeverity", current.DefaultSeverity);
        next.DefaultSeverity = HaToastService.TryParseSeverity(sev, out var parsed) ? parsed : current.DefaultSeverity;
        return next;
    }

    internal static void PersistSection(string name, Dictionary<string, JsonNode?> values)
    {
        var root = AppSettingsStore.Load();
        var section = AppSettingsStore.Section(root, name);
        foreach (var (k, v) in values)
            section[k] = v;
        AppSettingsStore.Save(root);
    }

    private static async Task<JsonElement> ReadObject(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
        return doc.RootElement.Clone();
    }

    private static bool Has(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);

    private static string GetString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }

    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        return fallback;
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
}
