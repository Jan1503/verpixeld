using System.Net;
using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Hardware;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.MediaPlayer;
using verpixeld.MediaPlayer.Audio;
using verpixeld.Services;
using verpixeld.WebApi;

namespace verpixeld.Configuration;

/// <summary>
///     Extension methods for registering application services with DI container
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers configuration options from IConfiguration
    /// </summary>
    public static IServiceCollection AddAppConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AppOptions>(configuration.GetSection(AppOptions.SectionName));
        services.Configure<WebServerOptions>(configuration.GetSection(WebServerOptions.SectionName));
        services.Configure<MatrixOptions>(configuration.GetSection(MatrixOptions.SectionName));

        return services;
    }

    /// <summary>
    ///     Registers hardware services (matrix renderer)
    /// </summary>
    public static IServiceCollection AddHardwareServices(this IServiceCollection services)
    {
        services.AddSingleton<IMatrixRenderer, RgbMatrixRenderer>();
        return services;
    }

    /// <summary>
    ///     Registers render services (render loop, startup)
    /// </summary>
    public static IServiceCollection AddRenderServices(this IServiceCollection services)
    {
        services.AddSingleton<IRenderService, RenderLoopService>();
        services.AddSingleton<StartupService>();
        return services;
    }

    /// <summary>
    ///     Registers all display-related services with the DI container
    /// </summary>
    public static IServiceCollection AddDisplayServices(
        this IServiceCollection services,
        CanvasManager canvasManager,
        Func<IPAddress?> getLocalIp,
        Func<double> getCurrentFps)
    {
        // Register CanvasManager as singleton (already created, hardware-dependent)
        services.AddSingleton(canvasManager);

        // Register discovery services
        services.AddSingleton<IExtensionDiscovery>(sp =>
        {
            var discovery = new ExtensionDiscoveryService();
            discovery.LoadAssemblies();
            return discovery;
        });

        services.AddSingleton<IFilterDiscovery>(sp =>
        {
            var discovery = new FilterDiscoveryService();
            discovery.LoadAssemblies();
            return discovery;
        });

        // Register layout services
        services.AddSingleton<IDisplayLayoutManager>(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new DisplayLayoutManager(cm);
        });

        services.AddSingleton<ICanvasContentManager>(sp =>
        {
            var layoutManager = sp.GetRequiredService<IDisplayLayoutManager>();
            var extensionDiscovery = sp.GetRequiredService<IExtensionDiscovery>();
            return new CanvasContentManager(layoutManager, extensionDiscovery);
        });

        services.AddSingleton<LayoutStorageManager>();

        services.AddSingleton<INightModeManager>(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            return new NightModeManager(cm);
        });

        services.AddSingleton<ILayoutScheduleManager>(sp =>
        {
            var storage = sp.GetRequiredService<LayoutStorageManager>();
            return new LayoutScheduleManager(storage);
        });

        // Register layout loader service
        services.AddSingleton<ILayoutLoaderService>(sp =>
        {
            var cm = sp.GetRequiredService<CanvasManager>();
            var layoutManager = sp.GetRequiredService<IDisplayLayoutManager>();
            var contentManager = sp.GetRequiredService<ICanvasContentManager>();
            var nightModeManager = sp.GetRequiredService<INightModeManager>();
            var filterDiscovery = sp.GetRequiredService<IFilterDiscovery>();
            return new LayoutLoaderService(cm, layoutManager, contentManager, nightModeManager, filterDiscovery);
        });

        // Register the endpoint context
        services.AddSingleton(sp => new EndpointContext
        {
            CanvasManager = sp.GetRequiredService<CanvasManager>(),
            GetLocalIp = getLocalIp,
            GetCurrentFps = getCurrentFps,
            LayoutManager = sp.GetRequiredService<IDisplayLayoutManager>(),
            ContentManager = sp.GetRequiredService<ICanvasContentManager>(),
            LayoutStorageManager = sp.GetRequiredService<LayoutStorageManager>(),
            LayoutLoader = sp.GetRequiredService<ILayoutLoaderService>(),
            ExtensionDiscovery = sp.GetRequiredService<IExtensionDiscovery>(),
            FilterDiscovery = sp.GetRequiredService<IFilterDiscovery>(),
            NightModeManager = sp.GetRequiredService<INightModeManager>(),
            ScheduleManager = sp.GetRequiredService<ILayoutScheduleManager>(),
            DisplayResolution = "384x192" // Fixed display resolution
        });

        return services;
    }

    /// <summary>
    ///     Registers pre-created services (for backward compatibility during transition)
    /// </summary>
    public static IServiceCollection AddDisplayServicesFromInstances(
        this IServiceCollection services,
        CanvasManager canvasManager,
        IDisplayLayoutManager layoutManager,
        ICanvasContentManager contentManager,
        LayoutStorageManager layoutStorageManager,
        INightModeManager nightModeManager,
        ILayoutScheduleManager scheduleManager,
        IExtensionDiscovery extensionDiscovery,
        IFilterDiscovery filterDiscovery,
        ILayoutLoaderService layoutLoader,
        Func<IPAddress?> getLocalIp,
        Func<double> getCurrentFps,
        string? displayResolution = null,
        CanvasRotationService? rotationService = null)
    {
        // Register all pre-created instances as singletons
        services.AddSingleton(canvasManager);
        services.AddSingleton(layoutManager);
        services.AddSingleton(contentManager);
        services.AddSingleton(layoutStorageManager);
        services.AddSingleton(nightModeManager);
        services.AddSingleton(scheduleManager);
        services.AddSingleton(extensionDiscovery);
        services.AddSingleton(filterDiscovery);
        services.AddSingleton(layoutLoader);

        // Register the endpoint context with all dependencies
        services.AddSingleton(new EndpointContext
        {
            CanvasManager = canvasManager,
            GetLocalIp = getLocalIp,
            GetCurrentFps = getCurrentFps,
            LayoutManager = layoutManager,
            ContentManager = contentManager,
            LayoutStorageManager = layoutStorageManager,
            LayoutLoader = layoutLoader,
            ExtensionDiscovery = extensionDiscovery,
            FilterDiscovery = filterDiscovery,
            NightModeManager = nightModeManager,
            ScheduleManager = scheduleManager,
            RotationService = rotationService,
            DisplayResolution = displayResolution ?? "384x192" // Actual configured resolution
        });

        return services;
    }

    /// <summary>
    ///     Registers all application-level services as singletons from pre-created instances.
    /// </summary>
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services,
        IRenderService renderService,
        FrameStreamService frameStreamService,
        AudioOutputService audioOutputService,
        BluetoothAudioService bluetoothAudioService,
        IAudioVisualizerService audioVisualizerService,
        MediaPlayerService mediaService,
        FavoritesService favoritesService,
        NetworkShareService networkShareService,
        AlertService alertService,
        AiImageService aiImageService,
        AiChatService aiChatService,
        VoiceCommandService voiceCommandService,
        MusicSearchService musicSearchService,
        RadioBrowserService radioBrowserService,
        LocalCameraService localCameraService,
        LogService logService)
    {
        services.AddSingleton(renderService);
        services.AddSingleton(frameStreamService);
        services.AddSingleton(audioOutputService);
        services.AddSingleton<IAudioOutputService>(audioOutputService);
        services.AddSingleton(bluetoothAudioService);
        services.AddSingleton(audioVisualizerService);
        services.AddSingleton(mediaService);
        services.AddSingleton(favoritesService);
        services.AddSingleton(networkShareService);
        services.AddSingleton(alertService);
        services.AddSingleton(aiImageService);
        services.AddSingleton(aiChatService);
        services.AddSingleton(voiceCommandService);
        services.AddSingleton(musicSearchService);
        services.AddSingleton(radioBrowserService);
        services.AddSingleton(localCameraService);
        services.AddSingleton(logService);
        return services;
    }
}
