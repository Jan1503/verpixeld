using System.Net;
using CanvasManagement;
using CanvasManagement.Interfaces;
using PixPlane;
using verpixeld.Hardware;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.MediaPlayer;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;
using verpixeld.WebApi;
using LayoutProfile = verpixeld.Interfaces.LayoutProfile;

namespace verpixeld.Configuration;

/// <summary>
///     Composition root: the container builds the process-lifetime graph.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAppConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.Configure<WebServerOptions>(configuration.GetSection(WebServerOptions.SectionName));
        services.Configure<MatrixOptions>(configuration.GetSection(MatrixOptions.SectionName));
        return services;
    }

    /// <summary>
    ///     Registers the host graph. <see cref="OutputRuntime.Start"/> runs on first resolve so
    ///     <see cref="CanvasManager"/> is sized from the live renderer.
    /// </summary>
    public static IServiceCollection AddVerpixeldHost(
        this IServiceCollection services,
        IConfiguration configuration,
        LogService logService)
    {
        services.AddSingleton(logService);

        services.AddSingleton(sp =>
        {
            var o = configuration.GetSection(ImageCorrectionOptions.SectionName).Get<ImageCorrectionOptions>()
                    ?? new ImageCorrectionOptions();
            return new ImageCorrectionService(o);
        });

        services.AddSingleton(sp =>
        {
            var app = configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
            var matrix = configuration.GetSection(MatrixOptions.SectionName).Get<MatrixOptions>() ?? new MatrixOptions();
            var hdmi = configuration.GetSection(HdmiOptions.SectionName).Get<HdmiOptions>() ?? new HdmiOptions();
            var spi = configuration.GetSection(SpiOptions.SectionName).Get<SpiOptions>() ?? new SpiOptions();
            var network = configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new NetworkOptions();
            ResolveBoundPanel(network);
            var ic = sp.GetRequiredService<ImageCorrectionService>();
            var runtime = new OutputRuntime(app, matrix, hdmi, spi, network, ic);
            Console.WriteLine("[INIT] Creating matrix renderer...");
            runtime.Start();
            return runtime;
        });
        services.AddSingleton<IMatrixRenderer>(sp => sp.GetRequiredService<OutputRuntime>());

        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<OutputRuntime>();
            Console.WriteLine("[INIT] Creating CanvasManager...");
            var cm = new CanvasManager(runtime.Width, runtime.Height);
            cm.TargetFps = runtime.App.TargetFps;
            return cm;
        });

        services.AddSingleton<IRenderService>(sp =>
            new RenderLoopService(
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<IMatrixRenderer>(),
                sp.GetRequiredService<ImageCorrectionService>()));

        services.AddSingleton(sp => new FrameStreamService(sp.GetRequiredService<IRenderService>()));

        services.AddSingleton<IExtensionDiscovery>(_ =>
        {
            ExtensionDiscoveryService.Default.LoadAssemblies();
            return ExtensionDiscoveryService.Default;
        });
        services.AddSingleton<IFilterDiscovery>(_ =>
        {
            FilterDiscoveryService.Default.LoadAssemblies();
            return FilterDiscoveryService.Default;
        });

        services.AddSingleton<AudioOutputService>();
        services.AddSingleton<IAudioOutputService>(sp => sp.GetRequiredService<AudioOutputService>());
        services.AddSingleton(sp => new BluetoothAudioService(sp.GetRequiredService<AudioOutputService>()));
        services.AddSingleton<IAudioVisualizerService>(_ => new AudioVisualizerService());

        services.AddSingleton(sp =>
        {
            Console.WriteLine("[INIT] Initializing layout manager...");
            var mgr = new DisplayLayoutManager(
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<IAudioVisualizerService>());
            mgr.ApplyLayout(LayoutProfile.FullScreen);
            return mgr;
        });
        services.AddSingleton<IDisplayLayoutManager>(sp => sp.GetRequiredService<DisplayLayoutManager>());

        services.AddSingleton<ICanvasContentManager>(sp =>
            new CanvasContentManager(
                sp.GetRequiredService<IDisplayLayoutManager>(),
                sp.GetRequiredService<IExtensionDiscovery>()));

        services.AddSingleton<LayoutStorageManager>();
        services.AddSingleton(sp => new NightModeManager(sp.GetRequiredService<CanvasManager>()));
        services.AddSingleton<INightModeManager>(sp => sp.GetRequiredService<NightModeManager>());
        services.AddSingleton(sp => new LayoutScheduleManager(sp.GetRequiredService<LayoutStorageManager>()));
        services.AddSingleton<ILayoutScheduleManager>(sp => sp.GetRequiredService<LayoutScheduleManager>());

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            var startup = new StartupService(cm)
            {
                DisplayWidth = cm.Width,
                DisplayHeight = cm.Height,
                PreferredFont = sp.GetRequiredService<OutputRuntime>().App.StartupFont
            };
            return startup;
        });

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new LocalCameraService(cm, cm.Width, cm.Height);
        });

        services.AddSingleton(sp =>
        {
            var rot = new CanvasRotationService(
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<ICanvasContentManager>());
            rot.LocalCamera = sp.GetRequiredService<LocalCameraService>();
            return rot;
        });

        services.AddSingleton<ILayoutLoaderService>(sp =>
            new LayoutLoaderService(
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<IDisplayLayoutManager>(),
                sp.GetRequiredService<ICanvasContentManager>(),
                sp.GetRequiredService<INightModeManager>(),
                sp.GetRequiredService<IFilterDiscovery>(),
                sp.GetRequiredService<CanvasRotationService>()));

        services.AddSingleton(sp =>
            new LayoutPlaylistService(
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<LayoutStorageManager>(),
                sp.GetRequiredService<ILayoutLoaderService>()));

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new MediaPlayerService(cm, sp.GetRequiredService<IAudioOutputService>(), cm.Width, cm.Height);
        });

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new AlertService(cm, sp.GetRequiredService<MediaPlayerService>(), cm.Width, cm.Height);
        });

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            Console.WriteLine("[INIT] AI services initialized (image + chat)");
            return new AiImageService(
                sp.GetRequiredService<DisplayLayoutManager>(), cm, cm.Width, cm.Height);
        });
        services.AddSingleton<AiChatService>();
        services.AddSingleton<MusicSearchService>();
        services.AddSingleton<RadioBrowserService>();
        services.AddSingleton<NetworkShareService>();
        services.AddSingleton<FavoritesService>();

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new VoiceCommandService(
                cm,
                sp.GetRequiredService<AiImageService>(),
                sp.GetRequiredService<AiChatService>(),
                sp.GetRequiredService<MediaPlayerService>(),
                sp.GetRequiredService<ICanvasContentManager>(),
                sp.GetRequiredService<IExtensionDiscovery>(),
                sp.GetRequiredService<MusicSearchService>(),
                sp.GetRequiredService<RadioBrowserService>(),
                sp.GetRequiredService<AlertService>(),
                sp.GetRequiredService<LocalCameraService>(),
                cm.Width,
                cm.Height);
        });

        services.AddSingleton(sp =>
        {
            var ha = configuration.GetSection(HomeAssistantOptions.SectionName).Get<HomeAssistantOptions>()
                     ?? new HomeAssistantOptions();
            return new HomeAssistantService(ha);
        });

        services.AddSingleton(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new HaToastService(cm, cm.Width, cm.Height, sp.GetRequiredService<HomeAssistantService>());
        });

        services.AddSingleton(sp =>
        {
            var ha = sp.GetRequiredService<HomeAssistantService>();
            var httpPort = configuration.GetSection(WebServerOptions.SectionName).GetValue("HttpPort", 5000);
            var device = new HaWallDevice(
                ha,
                sp.GetRequiredService<CanvasManager>(),
                sp.GetRequiredService<INightModeManager>(),
                sp.GetRequiredService<LayoutStorageManager>(),
                sp.GetRequiredService<ILayoutLoaderService>(),
                () =>
                {
                    var ip = StartupService.GetLocalIPAddress();
                    return ip == null ? null : $"http://{ip}:{httpPort}";
                });
            ha.WallDevice = device;
            return device;
        });

        services.AddSingleton<HostOrchestrator>();
        services.AddSingleton<HealthMonitoringService>();
        services.AddSingleton<PulseAudioEventService>();
        services.AddHostedService(sp => sp.GetRequiredService<PulseAudioEventService>());

        services.AddSingleton(sp =>
        {
            var runtime = sp.GetRequiredService<OutputRuntime>();
            var render = sp.GetRequiredService<IRenderService>();
            return new EndpointContext
            {
                CanvasManager = sp.GetRequiredService<CanvasManager>(),
                GetLocalIp = StartupService.GetLocalIPAddress,
                GetCurrentFps = () => render.CurrentFps,
                LayoutManager = sp.GetRequiredService<IDisplayLayoutManager>(),
                ContentManager = sp.GetRequiredService<ICanvasContentManager>(),
                LayoutStorageManager = sp.GetRequiredService<LayoutStorageManager>(),
                LayoutLoader = sp.GetRequiredService<ILayoutLoaderService>(),
                ExtensionDiscovery = sp.GetRequiredService<IExtensionDiscovery>(),
                FilterDiscovery = sp.GetRequiredService<IFilterDiscovery>(),
                NightModeManager = sp.GetRequiredService<INightModeManager>(),
                ScheduleManager = sp.GetRequiredService<ILayoutScheduleManager>(),
                RotationService = sp.GetRequiredService<CanvasRotationService>(),
                DisplayResolution = $"{runtime.Width}x{runtime.Height}"
            };
        });

        services.AddDisplayHealthChecks();
        return services;
    }

    internal static void ResolveBoundPanel(NetworkOptions network)
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

            if (!moved) return;
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
        catch (Exception ex)
        {
            Console.WriteLine($"[NET] panel resolve failed: {ex.Message}");
        }
    }
}
