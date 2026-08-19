using System.Net;
using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Shared context for API endpoints containing all required dependencies
/// </summary>
public class EndpointContext
{
    public required CanvasManager CanvasManager { get; init; }
    public required Func<IPAddress?> GetLocalIp { get; init; }
    public required Func<double> GetCurrentFps { get; init; }

    // Layout and content management
    public IDisplayLayoutManager? LayoutManager { get; init; }
    public ICanvasContentManager? ContentManager { get; init; }
    public LayoutStorageManager? LayoutStorageManager { get; init; }
    public ILayoutLoaderService? LayoutLoader { get; init; }

    // Discovery services
    public IExtensionDiscovery? ExtensionDiscovery { get; init; }
    public IFilterDiscovery? FilterDiscovery { get; init; }

    // Night mode and scheduling
    public INightModeManager? NightModeManager { get; init; }
    public ILayoutScheduleManager? ScheduleManager { get; init; }

    // Per-canvas content rotation
    public CanvasRotationService? RotationService { get; init; }

    // Current layout state (mutable - updated when layout changes)
    public LayoutProfile CurrentLayout { get; set; } = LayoutProfile.FullScreen;

    // Prime canvas reference (updated when layout changes)
    public Canvas? PrimeCanvas { get; set; }

    // Display resolution (set during initialization)
    public string? DisplayResolution { get; init; }
}
