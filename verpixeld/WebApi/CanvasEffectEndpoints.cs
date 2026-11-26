using System.Text.Json;
using CanvasManagement;
using CanvasManagement.Interfaces;
using verpixeld.Interfaces;
using verpixeld.Layout;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Effects of one canvas (its own filter chain, applied before it is composited):
///     list, add, change settings, reorder, remove. The wall-wide chain stays under /api/filters.
/// </summary>
[LogTag("API")]
public static class CanvasEffectEndpoints
{
    private static ILogger Log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public sealed class AddEffectRequest
    {
        public string? FilterType { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public sealed class UpdateEffectRequest
    {
        public Dictionary<string, object>? Parameters { get; set; }
    }

    public static void MapCanvasEffectEndpoints(this WebApplication app)
    {
        Log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CanvasEffectEndpoints));
        var canvasManager = app.Services.GetRequiredService<CanvasManager>();
        var layoutManager = app.Services.GetRequiredService<IDisplayLayoutManager>();
        var discovery = app.Services.GetRequiredService<IFilterDiscovery>();
        var factory = app.Services.GetRequiredService<FilterFactory>();

        Canvas? Find(string name) => layoutManager.GetCanvas(name) ?? canvasManager.GetCanvasByName(name);

        IResult NotFound(string name) => Results.Json(new ApiResponse<object>(false, Error: $"Canvas '{name}' not found"));

        object View(ICanvasFilter filter, int index)
        {
            var config = FilterFactory.Describe(filter);
            return new
            {
                index,
                filterType = config.FilterType,
                displayName = discovery.GetByTypeName(config.FilterType)?.DisplayName ?? filter.Name,
                enabled = filter.Enabled,
                intensity = filter.Intensity,
                parameters = config.Parameters
            };
        }

        app.MapGet("/api/canvas/{canvasName}/effects", (string canvasName) =>
        {
            var canvas = Find(canvasName);
            if (canvas == null) return NotFound(canvasName);
            var effects = canvas.Filters.Select(View).ToArray();
            return Results.Json(new ApiResponse<object[]>(true, effects));
        });

        app.MapPost("/api/canvas/{canvasName}/effects", async (string canvasName, HttpContext context) =>
        {
            var canvas = Find(canvasName);
            if (canvas == null) return NotFound(canvasName);
            AddEffectRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<AddEffectRequest>(); }
            catch (JsonException) { request = null; }
            if (string.IsNullOrWhiteSpace(request?.FilterType))
                return Results.Json(new ApiResponse<object>(false, Error: "Effect type is required"));

            var filter = factory.Create(request.FilterType);
            if (filter == null)
                return Results.Json(new ApiResponse<object>(false, Error: $"Unknown effect '{request.FilterType}'"));
            FilterFactory.Apply(filter, request.Parameters);

            var chain = canvas.Filters.ToList();
            chain.Add(filter);
            canvas.SetFilters(chain);
            Log.LogInformation("Effect {Effect} added to '{Canvas}'", filter.GetType().Name, canvasName);
            return Results.Json(new ApiResponse<object>(true, View(filter, chain.Count - 1)));
        });

        app.MapPut("/api/canvas/{canvasName}/effects/{index:int}", async (string canvasName, int index, HttpContext context) =>
        {
            var canvas = Find(canvasName);
            if (canvas == null) return NotFound(canvasName);
            var chain = canvas.Filters;
            if (index < 0 || index >= chain.Count)
                return Results.Json(new ApiResponse<object>(false, Error: "Invalid effect index"));
            UpdateEffectRequest? request;
            try { request = await context.Request.ReadFromJsonAsync<UpdateEffectRequest>(); }
            catch (JsonException) { request = null; }
            if (request?.Parameters == null)
                return Results.Json(new ApiResponse<object>(false, Error: "Parameters required"));

            // Settings change on the live instance (like the wall-wide filters): no restart of its animation.
            FilterFactory.Apply(chain[index], request.Parameters);
            return Results.Json(new ApiResponse<object>(true, View(chain[index], index)));
        });

        app.MapDelete("/api/canvas/{canvasName}/effects/{index:int}", (string canvasName, int index) =>
        {
            var canvas = Find(canvasName);
            if (canvas == null) return NotFound(canvasName);
            var chain = canvas.Filters.ToList();
            if (index < 0 || index >= chain.Count)
                return Results.Json(new ApiResponse<object>(false, Error: "Invalid effect index"));
            var removed = chain[index];
            chain.RemoveAt(index);
            canvas.SetFilters(chain);
            (removed as IDisposable)?.Dispose();
            return Results.Json(new ApiResponse<object>(true, $"Effect removed from '{canvasName}'"));
        });

        app.MapPost("/api/canvas/{canvasName}/effects/{index:int}/move", (string canvasName, int index, int delta) =>
        {
            var canvas = Find(canvasName);
            if (canvas == null) return NotFound(canvasName);
            var chain = canvas.Filters.ToList();
            var target = index + Math.Sign(delta);
            if (index < 0 || index >= chain.Count || target < 0 || target >= chain.Count)
                return Results.Json(new ApiResponse<object>(false, Error: "Cannot move the effect there"));
            (chain[index], chain[target]) = (chain[target], chain[index]);
            canvas.SetFilters(chain);
            return Results.Json(new ApiResponse<object>(true, $"Effect moved"));
        });
    }
}
