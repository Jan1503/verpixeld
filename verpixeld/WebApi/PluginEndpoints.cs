using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Layout;

namespace verpixeld.WebApi;

/// <summary>
///     Live reload of extension and filter plugin DLLs (collectible ALC, files are not locked).
/// </summary>
public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this WebApplication app)
    {
        var ctx = app.Services.GetRequiredService<EndpointContext>();
        app.MapPost("/api/plugins/reload", () =>
        {
            try { return ApiResponse.Ok(Reload(ctx), "Plugins reloaded"); }
            catch (Exception ex)
            {
                Console.WriteLine($"[PLUGIN] reload failed: {ex}");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static object Reload(EndpointContext ctx)
    {
        var cm = ctx.CanvasManager;
        var content = ctx.ContentManager;
        var ext = ctx.ExtensionDiscovery;
        var filt = ctx.FilterDiscovery;

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
            catch (Exception ex) { Console.WriteLine($"[PLUGIN] dispose filter: {ex.Message}"); }
        }

        content.StopAllContent();

        Console.WriteLine($"[PLUGIN] reload: {running.Count} extension(s), {filterSnap.Count} filter(s) to restore");
        ext.ReloadAssemblies();
        filt.ReloadAssemblies();

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
                Console.WriteLine($"[PLUGIN] restore extension '{item.DisplayName}' on '{item.Canvas}': {ex.Message}");
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
                Console.WriteLine($"[PLUGIN] restore filter '{item.Name}': {ex.Message}");
            }
        }

        var extInfo = ext.GetAvailableInfo().Count();
        var filtInfo = filt.GetAvailableInfo().Count();
        Console.WriteLine($"[PLUGIN] reload done: {extInfo} extension type(s), {filtInfo} filter type(s)");

        return new
        {
            extensions = new { available = extInfo, restored = restoredExt, failed = failedExt },
            filters = new { available = filtInfo, restored = restoredFilt, failed = failedFilt }
        };
    }

    private sealed record RunningExt(string Canvas, string DisplayName, Dictionary<string, object>? Config);
    private sealed record RunningFilter(string TypeName, string Name, Dictionary<string, object> Parameters);
}
