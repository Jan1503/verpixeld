using System.Net;
using System.Text.Json;
using CanvasManagement;
using CanvasManagement.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using verpixeld.Configuration;
using verpixeld.Interfaces;
using verpixeld.MediaPlayer;
using verpixeld.Services;

namespace verpixeld.WebApi;

/// <summary>
///     Main coordinator for all display-related API endpoints.
///     Delegates to specialized endpoint files for better organization.
/// </summary>
public static class DisplayApiEndpoints
{
    /// <summary>
    ///     Maps layout/filter/system endpoints. Each mapper resolves <see cref="EndpointContext"/> from DI.
    /// </summary>
    public static void MapDisplayEndpointsWithContext(this WebApplication app)
    {
        app.MapSystemEndpoints();
        app.MapFilterEndpoints();
        app.MapExtensionEndpoints();
        app.MapPluginEndpoints();
        app.MapCanvasEndpoints();
        app.MapDrawingEndpoints();
    }

    /// <summary>
    ///     Maps display endpoints from the DI-built <see cref="EndpointContext"/>.
    /// </summary>
    public static void MapDisplayEndpoints(this WebApplication app) => app.MapDisplayEndpointsWithContext();
}

// ============================================================================
// API MODELS
// ============================================================================

/// <summary>
///     Standard API response wrapper
/// </summary>
public record ApiResponse<T>(bool Success, T? Data = default, string? Error = null);

/// <summary>
///     System status information
/// </summary>
public record SystemStatus(
    string IpAddress,
    bool IsStreaming,
    int ActiveFiltersCount,
    string DisplayResolution,
    long UptimeSeconds,
    string UptimeFormatted,
    string Fps,
    bool InContainer
);

/// <summary>
///     Filter information
/// </summary>
public record FilterInfo(
    string Id,
    string Type,
    bool IsActive,
    Dictionary<string, object> Parameters
);

/// <summary>
///     Request to add a filter
/// </summary>
public class AddFilterRequest
{
    public string? FilterType { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

/// <summary>
///     Request to update filter parameters
/// </summary>
public class UpdateFilterRequest
{
    public Dictionary<string, object>? Parameters { get; set; }
}

// ============================================================================
// DATA STORAGE CLASSES
// ============================================================================

/// <summary>
///     Saved drawing data model
/// </summary>
public class SavedDrawing
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string ImageData { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>
///     In-memory storage for shared drawings (persisted to file)
/// </summary>
public static class SharedDrawingsStorage
{
    private const int MaxDrawings = 100;
    private static readonly List<SavedDrawing> _drawings = new();
    private static readonly object _lock = new();
    private static readonly string _storageFile = AppPaths.Drawings;

    static SharedDrawingsStorage()
    {
        Load();
    }

    public static List<SavedDrawing> GetAll()
    {
        lock (_lock)
        {
            return _drawings.ToList();
        }
    }

    public static void Add(SavedDrawing drawing)
    {
        lock (_lock)
        {
            _drawings.Insert(0, drawing);
            while (_drawings.Count > MaxDrawings) _drawings.RemoveAt(_drawings.Count - 1);
            Save();
        }
    }

    public static bool Delete(string id)
    {
        lock (_lock)
        {
            var drawing = _drawings.FirstOrDefault(d => d.Id == id);
            if (drawing != null)
            {
                _drawings.Remove(drawing);
                Save();
                return true;
            }

            return false;
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _drawings.Clear();
            Save();
        }
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_drawings, new JsonSerializerOptions { WriteIndented = true });
            FileHelper.AtomicWriteAllText(_storageFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Could not save drawings: {ex.Message}");
        }
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(_storageFile))
            {
                var json = File.ReadAllText(_storageFile);
                var drawings = JsonSerializer.Deserialize<List<SavedDrawing>>(json);
                if (drawings != null) _drawings.AddRange(drawings);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Could not load drawings: {ex.Message}");
        }
    }
}

/// <summary>
///     Manages Server-Sent Events connections for collaborative live drawing
/// </summary>
public static class LiveDrawingBroadcast
{
    private static readonly Dictionary<string, HttpResponse> _clients = new();
    private static readonly object _lock = new();

    public static void AddClient(string clientId, HttpResponse response)
    {
        lock (_lock)
        {
            _clients[clientId] = response;
            Console.WriteLine($"[LiveDraw] Client connected: {clientId} (total: {_clients.Count})");
        }
    }

    public static void RemoveClient(string clientId)
    {
        lock (_lock)
        {
            _clients.Remove(clientId);
            Console.WriteLine($"[LiveDraw] Client disconnected: {clientId} (total: {_clients.Count})");
        }
    }

    public static async Task BroadcastStrokes(string canvasName, List<object> strokes, string? excludeClientId)
    {
        var data = JsonSerializer.Serialize(new { type = "strokes", canvas = canvasName, strokes });
        await Broadcast($"event: draw\ndata: {data}\n\n", excludeClientId);
    }

    public static async Task BroadcastShape(string canvasName, string tool, int x1, int y1, int x2, int y2,
        string color, float alpha, int size, bool filled, string? excludeClientId)
    {
        var data = JsonSerializer.Serialize(new
        {
            type = "shape", canvas = canvasName, tool, x1, y1, x2, y2, color, alpha, size, filled
        });
        await Broadcast($"event: draw\ndata: {data}\n\n", excludeClientId);
    }

    public static async Task BroadcastClear(string canvasName, string? excludeClientId)
    {
        var data = JsonSerializer.Serialize(new { type = "clear", canvas = canvasName });
        await Broadcast($"event: draw\ndata: {data}\n\n", excludeClientId);
    }

    private static async Task Broadcast(string message, string? excludeClientId)
    {
        List<string> deadClients = new();
        Dictionary<string, HttpResponse> clientsCopy;

        lock (_lock)
        {
            clientsCopy = new Dictionary<string, HttpResponse>(_clients);
        }

        foreach (var (clientId, response) in clientsCopy)
        {
            if (clientId == excludeClientId) continue;

            try
            {
                await response.WriteAsync(message);
                await response.Body.FlushAsync();
            }
            catch
            {
                deadClients.Add(clientId);
            }
        }

        if (deadClients.Count > 0)
            lock (_lock)
            {
                foreach (var id in deadClients) _clients.Remove(id);
            }
    }

    public static int GetClientCount()
    {
        lock (_lock)
        {
            return _clients.Count;
        }
    }
}
