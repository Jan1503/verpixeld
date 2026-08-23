using System.Net;
using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Shared context for older layout/filter/system mappers. Built from DI; layout state lives on
///     <see cref="IDisplayLayoutManager"/> / <see cref="ILayoutLoaderService"/>.
/// </summary>
public class EndpointContext
{
    public required CanvasManager CanvasManager { get; init; }
    public required Func<IPAddress?> GetLocalIp { get; init; }
    public required Func<double> GetCurrentFps { get; init; }

    public required IDisplayLayoutManager LayoutManager { get; init; }
    public required ICanvasContentManager ContentManager { get; init; }
    public required LayoutStorageManager LayoutStorageManager { get; init; }
    public required ILayoutLoaderService LayoutLoader { get; init; }

    public required IExtensionDiscovery ExtensionDiscovery { get; init; }
    public required IFilterDiscovery FilterDiscovery { get; init; }

    public required INightModeManager NightModeManager { get; init; }
    public required ILayoutScheduleManager ScheduleManager { get; init; }

    public required CanvasRotationService RotationService { get; init; }

    public required string DisplayResolution { get; init; }
}
