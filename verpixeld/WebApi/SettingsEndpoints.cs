using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CanvasManagement;
using verpixeld.Configuration;
using verpixeld.Hardware;
using verpixeld.MediaPlayer;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     API endpoints for application and hardware settings
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var canvasManager = app.Services.GetRequiredService<CanvasManager>();
        var output = app.Services.GetRequiredService<OutputRuntime>();
        var homeAssistant = app.Services.GetRequiredService<HomeAssistantService>();
        var group = app.MapGroup("/api/settings");

        MapCertificateEndpoints(group, app);

        // Get all settings
        group.MapGet("/", () =>
        {
            try
            {
                var systemInfo = new
                {
                    ffmpegAvailable = MediaPlayerService.FFmpegAvailable,
                    pulseAudioAvailable = CheckPulseAudio(),
                    bluetoothAvailable = CheckBluetooth(),
                    simulationMode = output?.App.SimulationMode
                                    ?? AppSettingsStore.Get<AppOptions>("App").SimulationMode,
                    configPath = AppSettingsStore.ConfigPath,
                    activeMode = output?.Mode,
                    canvasWidth = output?.Width,
                    canvasHeight = output?.Height
                };

                if (output != null)
                {
                    var snap = JsonSerializer.SerializeToNode(
                        OutputSettingsEndpoints.BuildSnapshot(output, homeAssistant),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                        as JsonObject ?? new JsonObject();
                    snap["systemInfo"] = JsonSerializer.SerializeToNode(systemInfo,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    return Results.Json(new { success = true, data = snap });
                }

                return Results.Json(new
                {
                    success = true,
                    data = new
                    {
                        matrix = AppSettingsStore.Get<MatrixOptions>("Matrix"),
                        app = AppSettingsStore.Get<AppOptions>("App"),
                        systemInfo
                    }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Update matrix settings
        group.MapPut("/matrix", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var newMatrix = JsonSerializer.Deserialize<MatrixSettingsDto>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (newMatrix == null)
                    return Results.Json(new { success = false, error = "Invalid matrix configuration" });

                var geometryError = RgbMatrixRenderer.ValidateMatrixOptions(new MatrixOptions
                {
                    Rows = newMatrix.Rows,
                    Cols = newMatrix.Cols,
                    ChainLength = newMatrix.ChainLength,
                    Parallel = newMatrix.Parallel,
                    PanelType = newMatrix.PanelType ?? ""
                });
                if (geometryError != null)
                    return Results.Json(new { success = false, error = geometryError });
                if (newMatrix.ChainLength < 1 || newMatrix.ChainLength > 16)
                    return Results.Json(new { success = false, error = "Chain length must be between 1 and 16" });
                if (newMatrix.Parallel < 1 || newMatrix.Parallel > 3)
                    return Results.Json(new { success = false, error = "Parallel chains must be between 1 and 3" });

                // Read current config
                var config = ReadConfig();

                // Update the matrix section in-place so advanced fields that the client does
                // not send (RowAddressType, Multiplexing, etc.) are preserved rather than wiped.
                config.Matrix ??= new MatrixConfig();
                config.Matrix.Rows = newMatrix.Rows;
                config.Matrix.Cols = newMatrix.Cols;
                config.Matrix.ChainLength = newMatrix.ChainLength;
                config.Matrix.Parallel = newMatrix.Parallel;
                config.Matrix.GpioSlowdown = Math.Clamp(newMatrix.GpioSlowdown, 0, 5);
                config.Matrix.PwmBits = Math.Clamp(newMatrix.PwmBits, 1, 11);
                config.Matrix.PanelType = newMatrix.PanelType ?? "";
                config.Matrix.HardwareMapping = newMatrix.HardwareMapping ?? "regular";

                // Advanced fields: only overwrite when explicitly provided.
                if (newMatrix.PwmLsbNanoseconds.HasValue)
                    config.Matrix.PwmLsbNanoseconds = Math.Clamp(newMatrix.PwmLsbNanoseconds.Value, 50, 3000);
                if (newMatrix.PwmDitherBits.HasValue)
                    config.Matrix.PwmDitherBits = Math.Clamp(newMatrix.PwmDitherBits.Value, 0, 2);
                if (newMatrix.Brightness.HasValue)
                    config.Matrix.Brightness = Math.Clamp(newMatrix.Brightness.Value, 1, 100);
                if (newMatrix.RowAddressType.HasValue)
                    config.Matrix.RowAddressType = Math.Clamp(newMatrix.RowAddressType.Value, 0, 5);
                if (newMatrix.ScanMode.HasValue)
                    config.Matrix.ScanMode = Math.Clamp(newMatrix.ScanMode.Value, 0, 1);
                if (newMatrix.Multiplexing.HasValue)
                    config.Matrix.Multiplexing = Math.Clamp(newMatrix.Multiplexing.Value, 0, 17);
                if (newMatrix.LimitRefreshRateHz.HasValue)
                    config.Matrix.LimitRefreshRateHz = Math.Max(0, newMatrix.LimitRefreshRateHz.Value);
                if (newMatrix.LedRgbSequence != null)
                    config.Matrix.LedRgbSequence = newMatrix.LedRgbSequence;
                if (newMatrix.PixelMapperConfig != null)
                    config.Matrix.PixelMapperConfig = newMatrix.PixelMapperConfig;
                if (newMatrix.DisableHardwarePulsing.HasValue)
                    config.Matrix.DisableHardwarePulsing = newMatrix.DisableHardwarePulsing.Value;
                if (newMatrix.ShowRefreshRate.HasValue)
                    config.Matrix.ShowRefreshRate = newMatrix.ShowRefreshRate.Value;
                if (newMatrix.InverseColors.HasValue)
                    config.Matrix.InverseColors = newMatrix.InverseColors.Value;

                // Also update App display dimensions to match
                config.App ??= new AppConfig();
                config.App.DisplayWidth = newMatrix.Cols * newMatrix.ChainLength;
                config.App.DisplayHeight = newMatrix.Rows * newMatrix.Parallel;

                OutputSettingsEndpoints.PersistSection("Matrix", new Dictionary<string, JsonNode?>
                {
                    ["Rows"] = config.Matrix.Rows,
                    ["Cols"] = config.Matrix.Cols,
                    ["ChainLength"] = config.Matrix.ChainLength,
                    ["Parallel"] = config.Matrix.Parallel,
                    ["GpioSlowdown"] = config.Matrix.GpioSlowdown,
                    ["PwmBits"] = config.Matrix.PwmBits,
                    ["PanelType"] = config.Matrix.PanelType,
                    ["HardwareMapping"] = config.Matrix.HardwareMapping,
                    ["PwmLsbNanoseconds"] = config.Matrix.PwmLsbNanoseconds,
                    ["PwmDitherBits"] = config.Matrix.PwmDitherBits,
                    ["Brightness"] = config.Matrix.Brightness,
                    ["RowAddressType"] = config.Matrix.RowAddressType,
                    ["ScanMode"] = config.Matrix.ScanMode,
                    ["Multiplexing"] = config.Matrix.Multiplexing,
                    ["LimitRefreshRateHz"] = config.Matrix.LimitRefreshRateHz,
                    ["LedRgbSequence"] = config.Matrix.LedRgbSequence,
                    ["PixelMapperConfig"] = config.Matrix.PixelMapperConfig,
                    ["DisableHardwarePulsing"] = config.Matrix.DisableHardwarePulsing,
                    ["ShowRefreshRate"] = config.Matrix.ShowRefreshRate,
                    ["InverseColors"] = config.Matrix.InverseColors
                });
                OutputSettingsEndpoints.PersistSection("App", new Dictionary<string, JsonNode?>
                {
                    ["DisplayWidth"] = config.App.DisplayWidth,
                    ["DisplayHeight"] = config.App.DisplayHeight
                });

                Console.WriteLine(
                    $"[SETTINGS] Matrix configuration saved: {config.App.DisplayWidth}x{config.App.DisplayHeight}");
                Console.WriteLine("[SETTINGS] Restart required for changes to take effect");

                return Results.Json(new
                {
                    success = true,
                    message = "Matrix configuration saved. Restart required.",
                    requiresManualRestart = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Error saving matrix config: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Update app settings
        group.MapPut("/app", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();
                var newApp = JsonSerializer.Deserialize<AppSettingsDto>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (newApp == null) return Results.Json(new { success = false, error = "Invalid app configuration" });

                // Read current config
                var config = ReadConfig();

                // Update app section (preserve display dimensions)
                config.App ??= new AppConfig();
                config.App.TargetFps = Math.Clamp(newApp.TargetFps, 1, 120);
                config.App.VerboseLogging = newApp.VerboseLogging;
                config.App.SimulationMode = newApp.SimulationMode;

                OutputSettingsEndpoints.PersistSection("App", new Dictionary<string, JsonNode?>
                {
                    ["TargetFps"] = config.App.TargetFps,
                    ["VerboseLogging"] = config.App.VerboseLogging,
                    ["SimulationMode"] = config.App.SimulationMode,
                    ["DisplayWidth"] = newApp.DisplayWidth ?? config.App.DisplayWidth,
                    ["DisplayHeight"] = newApp.DisplayHeight ?? config.App.DisplayHeight
                });

                if (canvasManager != null)
                    canvasManager.TargetFps = config.App.TargetFps;
                if (output != null)
                {
                    output.App.TargetFps = config.App.TargetFps;
                    output.App.VerboseLogging = config.App.VerboseLogging;
                }
                if (output != null)
                {
                    output.App.TargetFps = config.App.TargetFps;
                    output.App.VerboseLogging = config.App.VerboseLogging;
                }

                Console.WriteLine(
                    $"[SETTINGS] App settings saved: FPS={config.App.TargetFps}, Verbose={config.App.VerboseLogging}");

                return Results.Json(new
                {
                    success = true,
                    message = "Application settings saved"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SETTINGS] Error saving app settings: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    private static FullConfig ReadConfig()
    {
        var path = AppSettingsStore.ConfigPath;
        if (!File.Exists(path)) return new FullConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<FullConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? new FullConfig();
    }

    private static void WriteConfig(FullConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var path = AppSettingsStore.ConfigPath;
        if (File.Exists(path))
            File.Copy(path, path + ".backup", true);

        File.WriteAllText(path, json);
    }

    private static bool CheckPulseAudio()
    {
        try
        {
            var psi = new ProcessStartInfo("pactl", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            PulseAudioHelper.ApplyPulseEnv(psi);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckBluetooth()
    {
        try
        {
            var psi = new ProcessStartInfo("bluetoothctl", "show")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["TERM"] = "dumb";

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output.Contains("Powered: yes");
        }
        catch
        {
            return false;
        }
    }

    // DTOs for JSON deserialization
    private class MatrixSettingsDto
    {
        public int Rows { get; set; } = 64;
        public int Cols { get; set; } = 64;
        public int ChainLength { get; set; } = 1;
        public int Parallel { get; set; } = 1;
        public int GpioSlowdown { get; set; } = 4;
        public int PwmBits { get; set; } = 11;
        public string? PanelType { get; set; }
        public string? HardwareMapping { get; set; }

        // Advanced fields are nullable so they are only overwritten when the client
        // explicitly sends them; otherwise existing appsettings.json values are preserved.
        public int? PwmLsbNanoseconds { get; set; }
        public int? PwmDitherBits { get; set; }
        public int? Brightness { get; set; }
        public int? RowAddressType { get; set; }
        public int? ScanMode { get; set; }
        public int? Multiplexing { get; set; }
        public int? LimitRefreshRateHz { get; set; }
        public string? LedRgbSequence { get; set; }
        public string? PixelMapperConfig { get; set; }
        public bool? DisableHardwarePulsing { get; set; }
        public bool? ShowRefreshRate { get; set; }
        public bool? InverseColors { get; set; }
    }

    private class AppSettingsDto
    {
        public int TargetFps { get; set; } = 30;
        public bool VerboseLogging { get; set; }
        public bool SimulationMode { get; set; }
        public int? DisplayWidth { get; set; }
        public int? DisplayHeight { get; set; }
    }

    // Config model classes
    private class FullConfig
    {
        public AppConfig? App { get; set; }
        public WebServerConfig? WebServer { get; set; }
        public MatrixConfig? Matrix { get; set; }
        public LoggingConfig? Logging { get; set; }
    }

    private class AppConfig
    {
        public int DisplayWidth { get; set; } = 384;
        public int DisplayHeight { get; set; } = 192;
        public int TargetFps { get; set; } = 30;
        public bool VerboseLogging { get; set; }
        public bool SimulationMode { get; set; }
    }

    private class WebServerConfig
    {
        public int HttpPort { get; set; } = 5000;
        public int HttpsPort { get; set; } = 5001;
        public bool EnableHttps { get; set; } = true;
        public string CertificatePath { get; set; } = "server.pfx";
        public string CertificatePassword { get; set; } = "rgbdisplay";
    }

    private class MatrixConfig
    {
        public int Rows { get; set; } = 64;
        public int Cols { get; set; } = 64;
        public int ChainLength { get; set; } = 6;
        public int Parallel { get; set; } = 3;
        public int GpioSlowdown { get; set; } = 4;
        public int PwmBits { get; set; } = 11;
        public int PwmLsbNanoseconds { get; set; } = 130;
        public int PwmDitherBits { get; set; } = 0;
        public int Brightness { get; set; } = 100;
        public int RowAddressType { get; set; } = 0;
        public int ScanMode { get; set; } = 0;
        public int Multiplexing { get; set; } = 0;
        public int LimitRefreshRateHz { get; set; } = 0;
        public string? LedRgbSequence { get; set; } = "RGB";
        public string? PixelMapperConfig { get; set; } = "";
        public bool DisableHardwarePulsing { get; set; } = false;
        public bool ShowRefreshRate { get; set; } = false;
        public bool InverseColors { get; set; } = false;
        public string PanelType { get; set; } = "FM6126A";
        public string HardwareMapping { get; set; } = "regular";
    }

    private class LoggingConfig
    {
        public Dictionary<string, string>? LogLevel { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CERTIFICATE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    private static void MapCertificateEndpoints(RouteGroupBuilder group, WebApplication app)
    {
        // Get current certificate info
        group.MapGet("/certificate", () =>
        {
            try
            {
                var certService = app.Services.GetRequiredService<CertificateService>();
                var info = certService.GetCertificateInfo();

                return Results.Json(new
                {
                    success = true,
                    data = info ?? (object)new { available = false }
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Upload a custom certificate (.pfx / .p12)
        group.MapPost("/certificate/upload", async (HttpContext context) =>
        {
            try
            {
                var form = await context.Request.ReadFormAsync();
                var file = form.Files.GetFile("certificate");
                var password = form["password"].ToString();

                if (file == null || file.Length == 0)
                    return Results.Json(new { success = false, error = "No certificate file provided." });

                if (file.Length > 10 * 1024 * 1024) // 10MB max
                    return Results.Json(new { success = false, error = "Certificate file too large (max 10MB)." });

                if (string.IsNullOrEmpty(password))
                    return Results.Json(new { success = false, error = "Certificate password is required." });

                // Read file bytes
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var pfxBytes = ms.ToArray();

                var certService = app.Services.GetRequiredService<CertificateService>();
                var (success, message) = certService.UploadCertificate(pfxBytes, password);

                if (success)
                {
                    // Update appsettings.json with new password
                    UpdateCertificatePasswordInConfig(password);
                }

                return Results.Json(new { success, message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CERT] Upload error: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });

        // Regenerate self-signed certificate
        group.MapPost("/certificate/regenerate", () =>
        {
            try
            {
                var certService = app.Services.GetRequiredService<CertificateService>();
                var (success, message) = certService.RegenerateSelfSigned();

                if (success)
                {
                    // Reset password in config to default
                    UpdateCertificatePasswordInConfig("rgbdisplay");
                }

                return Results.Json(new { success, message });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CERT] Regenerate error: {ex.Message}");
                return Results.Json(new { success = false, error = ex.Message });
            }
        });
    }

    /// <summary>
    ///     Update the CertificatePassword in appsettings.json so the new password
    ///     persists across restarts.
    /// </summary>
    private static void UpdateCertificatePasswordInConfig(string password)
    {
        try
        {
            var root = AppSettingsStore.Load();
            var ws = AppSettingsStore.Section(root, "WebServer");
            ws["CertificatePassword"] = password;
            AppSettingsStore.Save(root);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CERT] Warning: Could not update config password: {ex.Message}");
        }
    }
}
