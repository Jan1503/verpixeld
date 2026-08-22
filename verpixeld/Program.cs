using System.Net;
using CanvasManagement;
using CanvasManagement.BdfFontManager;
using CanvasManagement.Interfaces;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PixPlane;
using verpixeld.Configuration;
using verpixeld.Hardware;
using verpixeld.Layout;
using verpixeld.MediaPlayer;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;
using verpixeld.WebApi;
using LayoutProfile = verpixeld.Interfaces.LayoutProfile;

namespace verpixeld;

public class Program
{
    // Hardware and rendering services
    private static IMatrixRenderer _matrixRenderer = null!;
    private static CanvasManager _cm = null!;

    private static IRenderService _renderService = null!;
    private static ImageCorrectionService _imageCorrection = null!;
    //private static Canvas _primeCanvas = null!;

    // Layout management
    private static DisplayLayoutManager _layoutManager = null!;
    private static CanvasContentManager _contentManager = null!;
    private static LayoutStorageManager _layoutStorageManager = null!;
    private static NightModeManager _nightModeManager = null!;
    private static LayoutScheduleManager _scheduleManager = null!;

    // Discovery services (DI-ready)
    private static IExtensionDiscovery _extensionDiscovery = null!;
    private static IFilterDiscovery _filterDiscovery = null!;

    // Logging control
    private static bool _verboseLogging;

    // Application options (loaded at startup)
    private static AppOptions _appOptions = new();
    private static OutputRuntime _outputRuntime = null!;

    // Startup and layout services
    private static StartupService _startupService = null!;
    private static ILayoutLoaderService _layoutLoader = null!;

    // Media player
    private static MediaPlayerService _mediaService = null!;

    // Network share service
    private static NetworkShareService _networkShareService = null!;

    // Audio services
    private static AudioOutputService _audioOutputService = null!;
    private static BluetoothAudioService _bluetoothAudioService = null!;
    private static IAudioVisualizerService _audioVisualizerService = null!;
    
    // Favorites service
    private static FavoritesService _favoritesService = null!;
    
    // Log capture service
    private static LogService _logService = null!;
    
    // Alert service
    private static AlertService _alertService = null!;
    
    // AI services
    private static AiImageService _aiImageService = null!;
    private static AiChatService _aiChatService = null!;
    
    // Voice command service
    private static VoiceCommandService _voiceCommandService = null!;

    // Home Assistant connection (WebSocket → entity state bridge)
    private static HomeAssistantService? _homeAssistantService;
    private static HaToastService? _haToastService;
    private static HaWallDevice? _haWallDevice;

    // Layout (scene) playlist rotation
    private static LayoutPlaylistService _layoutPlaylistService = null!;

    // Per-canvas content rotation
    private static CanvasRotationService _canvasRotationService = null!;
    
    // Music search service
    private static MusicSearchService _musicSearchService = null!;
    
    // Internet radio service
    private static RadioBrowserService _radioBrowserService = null!;
    
    // Local camera service
    private static LocalCameraService _localCameraService = null!;

    // Live preview service
    private static FrameStreamService _frameStreamService = null!;

    public static async Task Main(string[] args)
    {
        // Install console log capture as early as possible
        _logService = new LogService();
        _logService.Install();
        
        // Parse command line arguments
        ParseCommandLineArgs(args);

        // Create all data/config/media directories
        Configuration.AppPaths.EnsureDirectories();

        // Initialize hardware, services, and canvas management
        InitializeHardware();
        InitializeCanvas();

        // Bind Kestrel as soon as the API has its services. Intro + default layout still run on the
        // wall, but the GUI is reachable during that wait instead of after it.
        var (app, shutdownCts) = CreateWebApplication();
        await app.StartAsync();
        Console.WriteLine("[WEB] Listening — intro and default layout continue in the background");

        try
        {
            await ShowIntro();
            await StartLocalMode();
            await app.WaitForShutdownAsync(shutdownCts.Token);
        }
        finally
        {
            await app.StopAsync();
            Console.WriteLine("Application shutdown complete.");
        }
    }

    private static void ParseCommandLineArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            switch (args[i].ToLower())
            {
                case "-v":
                case "--verbose":
                    _verboseLogging = true;
                    Console.WriteLine("[CONFIG] Verbose logging enabled");
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("verpixeld - LED Matrix Control System");
        Console.WriteLine();
        Console.WriteLine("Usage: verpixeld [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v, --verbose    Enable verbose web server logging");
        Console.WriteLine("  -h, --help       Show this help message");
        Console.WriteLine();
        Console.WriteLine("Ports:");
        Console.WriteLine("  HTTP:  5000");
        Console.WriteLine("  HTTPS: 5001 (for camera streaming)");
        Console.WriteLine();
        Console.WriteLine("Configuration:");
        Console.WriteLine("  Settings can be configured in appsettings.json");
        Console.WriteLine("  Command line options override config file settings");
        Console.WriteLine();
        Console.WriteLine("Health Check:");
        Console.WriteLine("  GET /health - Returns system and display health status");
    }

    private static void InitializeHardware()
    {
        // Read config early to determine simulation mode and display dimensions
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        var appOptions = config.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
        _appOptions = appOptions;
        var matrixOptions = config.GetSection(MatrixOptions.SectionName).Get<MatrixOptions>() ?? new MatrixOptions();
        var hdmiOptions = config.GetSection(HdmiOptions.SectionName).Get<HdmiOptions>() ?? new HdmiOptions();
        var spiOptions = config.GetSection(SpiOptions.SectionName).Get<SpiOptions>() ?? new SpiOptions();
        var networkOptions = config.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();
        TryResolveBoundPanel(networkOptions);
        var imageCorrectionOptions = config.GetSection(ImageCorrectionOptions.SectionName).Get<ImageCorrectionOptions>()
                                     ?? new ImageCorrectionOptions();
        _imageCorrection = new ImageCorrectionService(imageCorrectionOptions);

        Console.WriteLine("[INIT] Creating matrix renderer...");

        _outputRuntime = new OutputRuntime(appOptions, matrixOptions, hdmiOptions, spiOptions,
            networkOptions, _imageCorrection);
        _outputRuntime.Start();
        _matrixRenderer = _outputRuntime;

        Console.WriteLine($"[INIT] Output mode: {_outputRuntime.Mode}");

        Console.WriteLine(
            $"[INIT] Matrix configured: {_matrixRenderer.Width}x{_matrixRenderer.Height} " +
            $"(panel={matrixOptions.PanelType}, rowAddr={matrixOptions.RowAddressType}, " +
            $"mux={matrixOptions.Multiplexing}, mapping={matrixOptions.HardwareMapping})");
    }

    /// <summary>
    ///     If a panel chip-id is bound, refresh Host from /status, mDNS, or UDP 7778 before the
    ///     streamer opens — so a DHCP lease change does not require editing the IP by hand.
    /// </summary>
    private static void TryResolveBoundPanel(NetworkOptions network)
    {
        var id = PanelDiscovery.NormalizeId(network.PanelId);
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            Console.WriteLine($"[NET] resolving bound panel {id} (last Host={network.Host})...");
            var found = PanelDiscovery.ResolveAsync(id, network.Host, TimeSpan.FromSeconds(2))
                .GetAwaiter().GetResult();
            if (found == null)
            {
                Console.WriteLine($"[NET] panel {id} not found on LAN; keeping Host={network.Host}");
                return;
            }

            var moved = !string.Equals(found.Host, network.Host, StringComparison.OrdinalIgnoreCase);
            if (moved)
                Console.WriteLine($"[NET] panel {id} is now at {found.Host} (was {network.Host})");
            else
                Console.WriteLine($"[NET] panel {id} still at {found.Host}");

            network.Host = found.Host;
            if (found.UdpPort > 0) network.Port = found.UdpPort;
            if (found.ColorBits is 8 or 10 or 13 or 14) network.ColorBits = found.ColorBits;

            if (moved)
            {
                try
                {
                    OutputSettingsEndpoints.PersistSection("Network", new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
                    {
                        ["Host"] = network.Host,
                        ["Port"] = network.Port,
                        ["ColorBits"] = network.ColorBits,
                        ["PanelId"] = network.PanelId
                    });
                }
                catch (Exception persistEx)
                {
                    Console.WriteLine($"[NET] could not persist resolved Host: {persistEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] panel resolve failed: {ex.Message}");
        }
    }

    private static void InitializeCanvas()
    {
        // Initialize the renderer first so its Width/Height reflect the real (post-pixel-mapper) canvas
        // size before the CanvasManager is sized to it. Initialize() is idempotent (guarded), so the later
        // RenderLoopService.Start() call is a no-op.
        _matrixRenderer.Initialize();

        var w = _matrixRenderer.Width;
        var h = _matrixRenderer.Height;

        Console.WriteLine("[INIT] Creating CanvasManager...");
        _cm = new CanvasManager(w, h);
        _cm.TargetFps = _appOptions.TargetFps;
        Console.WriteLine($"[INIT] Render target: {_cm.TargetFps} fps");

        Console.WriteLine("[INIT] Starting render service...");
        _renderService = new RenderLoopService(_cm, _matrixRenderer, _imageCorrection);
        _renderService.Start();
        Console.WriteLine("[INIT] Render service started");

        _frameStreamService = new FrameStreamService(_renderService);

        _extensionDiscovery = ExtensionDiscoveryService.Default;
        _filterDiscovery = FilterDiscoveryService.Default;
        _extensionDiscovery.LoadAssemblies();
        _filterDiscovery.LoadAssemblies();
        Console.WriteLine("[INIT] Discovery services initialized");

        _audioOutputService = new AudioOutputService();
        _bluetoothAudioService = new BluetoothAudioService(_audioOutputService);
        _audioVisualizerService = new AudioVisualizerService();
        Console.WriteLine("[INIT] Audio services initialized");

        Console.WriteLine("[INIT] Initializing layout manager...");
        _layoutManager = new DisplayLayoutManager(_cm, _audioVisualizerService);
        _contentManager = new CanvasContentManager(_layoutManager, _extensionDiscovery);
        _layoutStorageManager = new LayoutStorageManager();
        _nightModeManager = new NightModeManager(_cm);
        _scheduleManager = new LayoutScheduleManager(_layoutStorageManager);
        _layoutManager.ApplyLayout(LayoutProfile.FullScreen);
        Console.WriteLine("[INIT] Layout system initialized");

        _startupService = new StartupService(_cm)
        {
            DisplayWidth = w,
            DisplayHeight = h,
            PreferredFont = _appOptions.StartupFont
        };

        _canvasRotationService = new CanvasRotationService(_cm, _contentManager);

        _layoutLoader = new LayoutLoaderService(_cm, _layoutManager, _contentManager, _nightModeManager,
            _filterDiscovery, _canvasRotationService);

        _layoutPlaylistService = new LayoutPlaylistService(_cm, _layoutStorageManager, _layoutLoader);

        _mediaService = new MediaPlayerService(_cm, _audioOutputService, w, h);

        _alertService = new AlertService(_cm, _mediaService, w, h);

        _aiImageService = new AiImageService(_layoutManager, _cm, w, h);
        _aiChatService = new AiChatService();
        Console.WriteLine("[INIT] AI services initialized (image + chat)");

        _musicSearchService = new MusicSearchService();
        _radioBrowserService = new RadioBrowserService();

        _localCameraService = new LocalCameraService(_cm, w, h);
        // Let per-canvas rotation drive USB-camera playback ("camera" content steps).
        _canvasRotationService.LocalCamera = _localCameraService;

        _voiceCommandService = new VoiceCommandService(
            _cm, _aiImageService, _aiChatService,
            _mediaService, _contentManager, _extensionDiscovery,
            _musicSearchService, _radioBrowserService, _alertService,
            _localCameraService, w, h);

        _networkShareService = new NetworkShareService();
        Console.WriteLine("[INIT] Network share service initialized");

        _favoritesService = new FavoritesService();
        Console.WriteLine("[INIT] Favorites service initialized");

        // Home Assistant: connect (if enabled) and mirror entity states into HomeAssistantBridge.
        var haConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        var haOptions = haConfig.GetSection(HomeAssistantOptions.SectionName).Get<HomeAssistantOptions>()
                        ?? new HomeAssistantOptions();
        _homeAssistantService = new HomeAssistantService(haOptions);
        _homeAssistantService.Start();
        _haToastService = new HaToastService(_cm, w, h, _homeAssistantService);
        var httpPort = haConfig.GetSection(WebServerOptions.SectionName).GetValue("HttpPort", 5000);
        _haWallDevice = new HaWallDevice(
            _homeAssistantService, _cm, _nightModeManager, _layoutStorageManager, _layoutLoader,
            () =>
            {
                var ip = LocalIPAddress();
                return ip == null ? null : $"http://{ip}:{httpPort}";
            });
        _homeAssistantService.WallDevice = _haWallDevice;

        BdfFontRegistry.LoadFontsFromCommonLocations();

        // Apply a configured default text font, if specified (e.g. for scrolling text / BDF labels).
        if (!string.IsNullOrWhiteSpace(_appOptions.DefaultFont))
        {
            BdfFontRegistry.DefaultFontName = _appOptions.DefaultFont;
            Console.WriteLine($"[INIT] Default BDF text font set from config: {_appOptions.DefaultFont}");
        }
    }

    /// <summary>
    ///     Restarts the render loop via the render service.
    /// </summary>
    public static void RestartRenderLoop()
    {
        _renderService.Restart();
    }

    /// <summary>
    ///     Gets the local IP address (delegates to StartupService)
    /// </summary>
    private static IPAddress? LocalIPAddress()
    {
        return StartupService.GetLocalIPAddress();
    }

    /// <summary>
    ///     Shows the intro/splash screen using the StartupService
    /// </summary>
    private static async Task ShowIntro()
    {
        await _startupService.ShowIntroAsync();
    }

    private static async Task StartLocalMode()
    {
        // Try to load default layout first
        var defaultLayout = _layoutStorageManager.GetDefaultLayout();

        if (defaultLayout != null)
        {
            var result = await _layoutLoader.LoadLayoutAsync(defaultLayout, "STARTUP");

            if (result.Success)
            {
                //_primeCanvas = _layoutLoader.PrimaryCanvas
                //               ?? throw new InvalidOperationException("No canvas available");
            }
            else
            {
                Console.WriteLine("[STARTUP] Falling back to default state...");
            }
        }
        else
        {
            Console.WriteLine("[STARTUP] No default layout set. Using initial FullScreen layout.");
        }

        // Start the scene playlist if it was enabled (rotates saved layouts; takes over from the default).
        _layoutPlaylistService.StartIfEnabled();

        // Resume any per-canvas content rotations that were enabled.
        _canvasRotationService.StartIfEnabled();

        // Initialize layout scheduler
        Console.WriteLine("[STARTUP] Initializing layout scheduler...");

        _scheduleManager.ScheduleTriggered += async (sender, args) =>
        {
            Console.WriteLine($"[SCHEDULER] Triggered: Loading '{args.LayoutName}'");

            var scheduledLayout = _layoutStorageManager.LoadLayout(args.LayoutName);
            if (scheduledLayout == null) Console.WriteLine($"[SCHEDULER] Layout '{args.LayoutName}' not found");

            //var result = await _layoutLoader.LoadLayoutAsync(scheduledLayout, "SCHEDULER");

            //if (result.Success)
            //{
            //    _primeCanvas = _layoutLoader.PrimaryCanvas!;
            //}
        };

        _scheduleManager.Start();
        Console.WriteLine("[STARTUP] Layout scheduler started");
    }

    private static (WebApplication App, CancellationTokenSource Shutdown) CreateWebApplication()
    {
        // ============================================================================
        // WEB API FOR REMOTE CONTROL
        // ============================================================================
        var builder = WebApplication.CreateBuilder();

        // Fast shutdown — don't wait 30s for hosted services
        builder.Host.ConfigureHostOptions(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(2));

        // Configuration
        builder.Services.AddAppConfiguration(builder.Configuration);

        // DI Registration
        builder.Services.AddDisplayServicesFromInstances(
            _cm,
            _layoutManager,
            _contentManager,
            _layoutStorageManager,
            _nightModeManager,
            _scheduleManager,
            _extensionDiscovery,
            _filterDiscovery,
            _layoutLoader,
            LocalIPAddress,
            () => _renderService.CurrentFps,
            $"{_matrixRenderer.Width}x{_matrixRenderer.Height}",
            _canvasRotationService
        );

        builder.Services.AddApplicationServices(
            _renderService,
            _frameStreamService,
            _audioOutputService,
            _bluetoothAudioService,
            _audioVisualizerService,
            _mediaService,
            _favoritesService,
            _networkShareService,
            _alertService,
            _aiImageService,
            _aiChatService,
            _voiceCommandService,
            _musicSearchService,
            _radioBrowserService,
            _localCameraService,
            _logService
        );

        // Health checks
        builder.Services.AddDisplayHealthChecks();

        // Audio event service (SSE for real-time volume updates)
        builder.Services.AddSingleton<PulseAudioEventService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PulseAudioEventService>());

        // Logging configuration
        var appConfig = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>();
        var useVerboseLogging = _verboseLogging || (appConfig?.VerboseLogging ?? false);
        ConfigureLogging(builder, useVerboseLogging);

        // Web server configuration (ports, HTTPS toggle, certificate) from the "WebServer" section
        var webServerOptions = builder.Configuration.GetSection(WebServerOptions.SectionName).Get<WebServerOptions>()
                               ?? new WebServerOptions();

        var certPath = webServerOptions.CertificatePath;
        var certPassword = webServerOptions.CertificatePassword;

        // Resolve relative cert path to app base directory (where server.pfx traditionally lives)
        if (!string.IsNullOrEmpty(certPath) && !Path.IsPathRooted(certPath))
            certPath = Path.Combine(AppContext.BaseDirectory, certPath);

        // Always register the certificate service so the certificate management API works,
        // but only materialize a certificate (and enable the HTTPS listener) when HTTPS is on.
        var certService = new CertificateService(certPath, certPassword);
        builder.Services.AddSingleton(certService);

        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null;
        if (webServerOptions.EnableHttps)
            certificate = certService.GetOrCreateCertificate();

        // Kestrel configuration (ports + HTTPS toggle wired from WebServer config)
        builder.WebHost.ConfigureKestrelEndpoints(
            certificate,
            webServerOptions.HttpPort,
            webServerOptions.HttpsPort,
            webServerOptions.EnableHttps);

        var app = builder.Build();

        // Middleware pipeline
        app.UseDisplayMiddleware(
            certificate,
            useVerboseLogging,
            webServerOptions.HttpsPort,
            webServerOptions.EnableHttps);
        app.UseDisplayStaticFiles();
        app.MapUtilityEndpoints();

        // Start health monitoring
        var healthMonitor = new HealthMonitoringService();
        healthMonitor.Start();

        // Map API endpoints
        app.MapDisplayEndpointsWithContext();
        app.MapLayoutEndpoints();
        app.MapBrightnessEndpoints();
        app.MapNightModeEndpoints();
        app.MapScheduleEndpoints();
        app.MapMediaPlayerEndpoints();
        app.MapNetworkShareEndpoints();
        app.MapYouTubeEndpoints();
        app.MapFavoritesEndpoints();
        app.MapAudioEndpoints();
        app.MapVisualizerEndpoints();
        app.MapSettingsEndpoints(_cm, _outputRuntime, _homeAssistantService);
        app.MapOutputSettingsEndpoints(_outputRuntime, _cm, _homeAssistantService);
        app.MapImageCorrectionEndpoints(_imageCorrection, _outputRuntime);
        app.MapNetworkConfigEndpoints(_outputRuntime);
        app.MapSeamEndpoints(_outputRuntime);
        app.MapLogEndpoints();
        app.MapAlertEndpoints();
        app.MapAiEndpoints();
        app.MapVoiceEndpoints();
        app.MapMusicSearchEndpoints();
        app.MapPreviewEndpoints();
        app.MapNowPlayingEndpoints();
        app.MapHomeAssistantEndpoints(_homeAssistantService);
        app.MapGeoEndpoints();
        app.MapPlaylistEndpoints(_layoutPlaylistService);
        app.MapCanvasRotationEndpoints(_canvasRotationService);

        // Discovery debug logging
        LogDiscoveryInfo();

        // Health check endpoint
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    totalDuration = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        duration = e.Value.Duration.TotalMilliseconds,
                        data = e.Value.Data
                    })
                });
            }
        });

        // Root endpoint
        app.MapGet("/", () => Results.Content(WebUIProvider.GetIndexHtml(), "text/html; charset=utf-8"));

        // Startup message
        Console.WriteLine("\n╔════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  ╦  ╦╔═╗╦═╗╔═╗╦═╗ ╦╔═╗╦  ╔╦╗                           ║");
        Console.WriteLine("║  ╚╗╔╝║╣ ╠╦╝╠═╝║╔╩╦╝║╣ ║   ║║  LED Matrix Control       ║");
        Console.WriteLine("║   ╚╝ ╚═╝╩╚═╩  ╩╩ ╚═╚═╝╩═╝═╩╝                           ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════╣");
        Console.WriteLine($"║  HTTP:  http://{LocalIPAddress()}:{webServerOptions.HttpPort}");
        if (webServerOptions.EnableHttps)
            Console.WriteLine($"║  HTTPS: https://{LocalIPAddress()}:{webServerOptions.HttpsPort}");
        Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");

        // Shutdown handling — stop all services in order before cancelling the host
        var pulseEventService = app.Services.GetRequiredService<PulseAudioEventService>();
        var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutdown requested...");

            // Stop hosted services first to prevent restarts during teardown
            pulseEventService.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            // Stop services that create background processes or timers
            healthMonitor.Stop();
            _voiceCommandService.Stop();
            _homeAssistantService?.Stop();
            _haWallDevice?.Dispose();
            _haToastService?.Dispose();
            _scheduleManager.Stop();
            _nightModeManager.Dispose();
            _alertService.Dispose();
            _localCameraService.Dispose();
            _renderService.Stop();
            if (_renderService is IDisposable renderDisposable)
                renderDisposable.Dispose();

            Console.WriteLine("[SHUTDOWN] All services stopped");
            shutdownCts.Cancel();
        };

        return (app, shutdownCts);
    }

    private static void ConfigureLogging(WebApplicationBuilder builder, bool verbose)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Information : LogLevel.Error);

        if (verbose)
            Console.WriteLine("[CONFIG] Verbose logging enabled");
    }

    private static void LogDiscoveryInfo()
    {
        Console.WriteLine("\n=== Discovery Info ===");
        try
        {
            var filterInfo = _filterDiscovery.GetAvailableInfo();
            Console.WriteLine($"Filters: {filterInfo.Count()}");
            foreach (var info in filterInfo)
                Console.WriteLine($"  - {info.DisplayName} [{info.Category}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
        }

        Console.WriteLine("======================\n");
    }
}
