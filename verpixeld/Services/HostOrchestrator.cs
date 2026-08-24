using CanvasManagement.BdfFontManager;
using CanvasManagement.Interfaces;
using verpixeld.Configuration;
using verpixeld.Hardware;
using verpixeld.Interfaces;
using verpixeld.Layout;

namespace verpixeld.Services;

/// <summary>
///     Post-DI startup: hardware already exists in the container; this starts loops and
///     loads the default scene / scheduler. Testable without Kestrel.
/// </summary>
public sealed class HostOrchestrator
{
    private readonly LayoutStorageManager _storage;
    private readonly ILayoutLoaderService _loader;
    private readonly LayoutPlaylistService _playlist;
    private readonly CanvasRotationService _rotation;
    private readonly ILayoutScheduleManager _schedule;

    public HostOrchestrator(
        LayoutStorageManager storage,
        ILayoutLoaderService loader,
        LayoutPlaylistService playlist,
        CanvasRotationService rotation,
        ILayoutScheduleManager schedule)
    {
        _storage = storage;
        _loader = loader;
        _playlist = playlist;
        _rotation = rotation;
        _schedule = schedule;
    }

    public async Task StartLocalModeAsync()
    {
        var defaultLayout = _storage.GetDefaultLayout();

        if (defaultLayout != null)
        {
            var result = await _loader.LoadLayoutAsync(defaultLayout, "STARTUP");
            if (!result.Success)
                Console.WriteLine("[STARTUP] Falling back to default state...");
        }
        else
        {
            Console.WriteLine("[STARTUP] No default layout set. Using initial FullScreen layout.");
        }

        _playlist.StartIfEnabled();
        _rotation.StartIfEnabled();

        Console.WriteLine("[STARTUP] Initializing layout scheduler...");
        _schedule.ScheduleTriggered += async (_, args) =>
            await HandleScheduleTriggeredAsync(args.LayoutName, _storage, _loader);
        _schedule.Start();
        Console.WriteLine("[STARTUP] Layout scheduler started");
    }

    /// <summary>Load a saved scene when the clock schedule fires. Null layout is a no-op.</summary>
    public static async Task HandleScheduleTriggeredAsync(
        string layoutName,
        LayoutStorageManager storage,
        ILayoutLoaderService loader)
    {
        Console.WriteLine($"[SCHEDULER] Triggered: Loading '{layoutName}'");
        var scheduledLayout = storage.LoadLayout(layoutName);
        if (scheduledLayout == null)
        {
            Console.WriteLine($"[SCHEDULER] Layout '{layoutName}' not found");
            return;
        }

        await loader.LoadLayoutAsync(scheduledLayout, "SCHEDULER");
    }
}

/// <summary>
///     Resolves and starts process-lifetime services that must not run inside factories
///     (render loop, HA, fonts).
/// </summary>
public static class HostRuntime
{
    public static void Start(IServiceProvider sp)
    {
        var runtime = sp.GetRequiredService<OutputRuntime>();
        Console.WriteLine($"[INIT] Output mode: {runtime.Mode}");
        Console.WriteLine(
            $"[INIT] Matrix configured: {runtime.Width}x{runtime.Height} " +
            $"(panel={runtime.Matrix.PanelType}, rowAddr={runtime.Matrix.RowAddressType}, " +
            $"mux={runtime.Matrix.Multiplexing}, mapping={runtime.Matrix.HardwareMapping})");

        var cm = sp.GetRequiredService<CanvasManagement.CanvasManager>();
        Console.WriteLine($"[INIT] Render target: {cm.TargetFps} fps");

        var render = sp.GetRequiredService<IRenderService>();
        render.Start();
        Console.WriteLine("[INIT] Render service started");

        _ = sp.GetRequiredService<IExtensionDiscovery>();
        _ = sp.GetRequiredService<IFilterDiscovery>();
        Console.WriteLine("[INIT] Discovery services initialized");

        _ = sp.GetRequiredService<IDisplayLayoutManager>();
        Console.WriteLine("[INIT] Layout system initialized");

        var ha = sp.GetRequiredService<HomeAssistantService>();
        _ = sp.GetRequiredService<HaWallDevice>();
        _ = sp.GetRequiredService<HaToastService>();
        ha.Start();

        BdfFontRegistry.LoadFontsFromDirectory(AppPaths.FontsDir);
        if (!AppPaths.RunningInContainer())
            BdfFontRegistry.LoadFontsFromCommonLocations();
        if (!string.IsNullOrWhiteSpace(runtime.App.DefaultFont))
        {
            BdfFontRegistry.DefaultFontName = runtime.App.DefaultFont;
            Console.WriteLine($"[INIT] Default BDF text font set from config: {runtime.App.DefaultFont}");
        }

        sp.GetRequiredService<HealthMonitoringService>().Start();
    }

    public static void LogDiscovery(IServiceProvider sp)
    {
        Console.WriteLine("\n=== Discovery Info ===");
        try
        {
            var filterInfo = sp.GetRequiredService<IFilterDiscovery>().GetAvailableInfo();
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
