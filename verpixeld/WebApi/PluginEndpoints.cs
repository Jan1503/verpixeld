using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.Layout;

namespace verpixeld.WebApi;

/// <summary>
///     Live reload of extension and filter plugin DLLs (collectible ALC, files are not locked).
/// </summary>
public static class PluginEndpoints
{
    /// <summary>Set when the endpoints are mapped; handlers in helper methods log through it too.</summary>
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public static void MapPluginEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PluginEndpoints));
        var canvasManager = app.Services.GetRequiredService<CanvasManager>();
        var contentManager = app.Services.GetRequiredService<ICanvasContentManager>();
        var extensionDiscovery = app.Services.GetRequiredService<IExtensionDiscovery>();
        var filterDiscovery = app.Services.GetRequiredService<IFilterDiscovery>();
        app.MapPost("/api/plugins/reload", () =>
        {
            try { return ApiResponse.Ok(Reload(canvasManager, contentManager, extensionDiscovery, filterDiscovery), "Plugins reloaded"); }
            catch (Exception ex)
            {
                Log.LogError(ex, "Reload failed");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static object Reload(CanvasManager cm, ICanvasContentManager content, IExtensionDiscovery ext,
        IFilterDiscovery filt)
    {
        var running = content.GetAllContents()
            .Where(c => c.ContentType == ContentType.DynamicExtension &&
                        !string.IsNullOrWhiteSpace(c.ExtensionDisplayName))
            .Select(c => new RunningExt(
                c.CanvasName,
                c.ExtensionDisplayName!,
                c.Configuration.Count > 0 ? new Dictionary<string, object>(c.Configuration) : null))
            .ToList();

        var filterSnap = new List<RunningFilter>();
        var liveFilters = cm.GetFilters().ToList();
        foreach (var filter in liveFilters)
        {
            filterSnap.Add(new RunningFilter(
                filter.GetType().Name,
                filter.Name,
                EndpointHelpers.ExtractFilterParameters(filter)));
        }

        cm.ClearFilters();
        foreach (var filter in liveFilters)
        {
            if (filter is not IDisposable d) continue;
            try { d.Dispose(); }
            catch (Exception ex) { Log.LogWarning("dispose filter: {Message}", ex.Message); }
        }

        content.StopAllContent();

        Log.LogInformation("reload: {Count} extension(s), {Count2} filter(s) to restore", running.Count, filterSnap.Count);
        ext.ReloadAssemblies(AppPaths.ExtensionsDir);
        filt.ReloadAssemblies(AppPaths.FiltersDir);

        var restoredExt = new List<string>();
        var failedExt = new List<string>();
        foreach (var item in running)
        {
            try
            {
                content.AssignExtension(item.Canvas, item.DisplayName, item.Config);
                restoredExt.Add($"{item.Canvas}: {item.DisplayName}");
            }
            catch (Exception ex)
            {
                failedExt.Add($"{item.Canvas}: {item.DisplayName} ({ex.Message})");
                Log.LogWarning("restore extension '{DisplayName}' on '{Canvas}': {Message}", item.DisplayName, item.Canvas, ex.Message);
            }
        }

        var restoredFilt = new List<string>();
        var failedFilt = new List<string>();
        foreach (var item in filterSnap)
        {
            try
            {
                var instance = filt.Create(item.TypeName) ?? filt.CreateByDisplayName(item.Name);
                if (instance == null)
                {
                    failedFilt.Add($"{item.Name} (not found after reload)");
                    continue;
                }

                if (item.Parameters.Count > 0)
                    EndpointHelpers.ApplyFilterParameters(instance, item.Parameters);
                cm.AddFilter(instance);
                restoredFilt.Add(instance.Name);
            }
            catch (Exception ex)
            {
                failedFilt.Add($"{item.Name} ({ex.Message})");
                Log.LogWarning("restore filter '{Name}': {Message}", item.Name, ex.Message);
            }
        }

        var extInfo = ext.GetAvailableInfo().Count();
        var filtInfo = filt.GetAvailableInfo().Count();
        Log.LogInformation("reload done: {ExtInfo} extension type(s), {FiltInfo} filter type(s)", extInfo, filtInfo);

        return new
        {
            extensions = new { available = extInfo, restored = restoredExt, failed = failedExt },
            filters = new { available = filtInfo, restored = restoredFilt, failed = failedFilt }
        };
    }

    private sealed record RunningExt(string Canvas, string DisplayName, Dictionary<string, object>? Config);
    private sealed record RunningFilter(string TypeName, string Name, Dictionary<string, object> Parameters);
}
