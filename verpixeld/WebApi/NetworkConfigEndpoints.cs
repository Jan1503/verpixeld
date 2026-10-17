using System.Text.Json;
using System.Text.Json.Nodes;
using CanvasManagement;
using PixPlane;
using verpixeld.Configuration;
using verpixeld.Hardware;

namespace verpixeld.WebApi;

/// <summary>
///     Live configuration of the network LED panel target (IP / port / pacing / colour depth). Applies to
///     the running <see cref="NetworkMatrixRenderer"/> WITHOUT a restart when that output is active, and
///     always persists to the "Network" section of appsettings.json when requested.
/// </summary>
public static class NetworkConfigEndpoints
{
    public static void MapNetworkConfigEndpoints(this WebApplication app)
    {
        var renderer = app.Services.GetRequiredService<IMatrixRenderer>();
        var runtime = renderer as OutputRuntime;
        var canvas = app.Services.GetRequiredService<CanvasManager>();
        NetworkMatrixRenderer? Current() =>
            runtime?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/network", () =>
        {
            try
            {
                var net = Current();
                var opts = runtime?.Network;
                var size = NetworkWall.Measure(opts?.StreamTargets() ?? []);
                return ApiResponse.Ok(new
                {
                    supported = true,
                    live = net != null,
                    host = net?.Host ?? opts?.Host ?? "",
                    port = net?.Port ?? opts?.Port ?? 7777,
                    targetMbps = net?.TargetMbps ?? opts?.TargetMbps ?? 19,
                    colorBits = net?.ColorBits ?? opts?.ColorBits ?? 14,
                    swapRedBlue = net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
                    wallWidth = size.Width,
                    wallHeight = size.Height,
                    panelId = opts?.PanelId ?? "",
                    panels = PanelDtos(net != null ? runtime?.Network.StreamTargets() : opts?.StreamTargets())
                });
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        app.MapPost("/api/settings/network", async (HttpContext context) =>
        {
            try
            {
                var net = Current();
                var opts = runtime?.Network;
                var fallbackHost = net?.Host ?? opts?.Host ?? "192.168.1.50";
                var fallbackPort = net?.Port ?? opts?.Port ?? 7777;
                var fallbackMbps = net?.TargetMbps ?? opts?.TargetMbps ?? 19;
                var fallbackBits = net?.ColorBits ?? opts?.ColorBits ?? 14;

                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = doc.RootElement;

                var host = GetString(root, "host", fallbackHost);
                var port = (int)GetDouble(root, "port", fallbackPort);
                var mbps = GetDouble(root, "targetMbps", fallbackMbps);
                var bits = (int)GetDouble(root, "colorBits", fallbackBits);
                var panelId = GetString(root, "panelId", opts?.PanelId ?? "");
                var panels = ReadPanels(root, host, port, panelId);
                NetworkWall.Normalize(panels);
                var save = !root.TryGetProperty("save", out var s) || s.ValueKind != JsonValueKind.False;

                bool? swap = null;
                if (root.TryGetProperty("swapRedBlue", out var sw) &&
                    (sw.ValueKind == JsonValueKind.True || sw.ValueKind == JsonValueKind.False))
                    swap = sw.GetBoolean();

                if (opts != null && net == null)
                {
                    opts.Panels = panels;
                    opts.SyncPrimary();
                    opts.TargetMbps = mbps;
                    opts.ColorBits = bits;
                    if (swap.HasValue) opts.SwapRedBlue = swap.Value;
                }

                net?.Reconfigure(panels, mbps, bits);
                if (swap.HasValue) net?.SetSwapRedBlue(swap.Value);

                var wall = NetworkWall.Measure(opts?.StreamTargets() ?? panels);
                if (canvas.Width != wall.Width || canvas.Height != wall.Height)
                    canvas.Resize(wall.Width, wall.Height);
                if (opts != null)
                {
                    opts.WallWidth = wall.Width;
                    opts.WallHeight = wall.Height;
                }
                if (runtime != null)
                {
                    runtime.App.DisplayWidth = wall.Width;
                    runtime.App.DisplayHeight = wall.Height;
                }
                if (save) Persist(net, opts, wall.Width, wall.Height);

                var targets = net != null ? (opts?.StreamTargets() ?? panels) : (opts?.StreamTargets() ?? panels);
                var primary = targets.FirstOrDefault();
                Console.WriteLine($"[API] Network display -> {string.Join(", ", targets.Select(p => p.Host + ":" + p.Port))} " +
                                  $"{bits}-bit {mbps:0.#} Mbit/s save={save}");

                return ApiResponse.Ok(new
                {
                    supported = true,
                    live = net != null,
                    host = primary?.Host ?? host,
                    port = primary?.Port ?? port,
                    targetMbps = mbps,
                    colorBits = bits,
                    swapRedBlue = swap ?? net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
                    panelId = primary?.PanelId ?? panelId,
                    panels = PanelDtos(targets),
                    wallWidth = wall.Width,
                    wallHeight = wall.Height,
                    saved = save
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting network config: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        app.MapGet("/api/settings/network/discover", async (HttpContext context) =>
        {
            try
            {
                var timeoutMs = 2000;
                if (context.Request.Query.TryGetValue("timeout", out var tq) &&
                    int.TryParse(tq, out var parsed))
                    timeoutMs = Math.Clamp(parsed, 500, 4000);
                var panels = await PanelDiscovery.ScanAsync(TimeSpan.FromMilliseconds(timeoutMs));
                return ApiResponse.Ok(panels, panels.Count == 0
                    ? "No panels answered. Check LAN / firmware 1.1+."
                    : $"Found {panels.Count} panel(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Panel discover: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });

        app.MapPost("/api/settings/network/identify", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var host = GetString(doc.RootElement, "host", "");
                var webPort = (int)GetDouble(doc.RootElement, "webPort", PanelDiscovery.DefaultWebPort);
                if (string.IsNullOrWhiteSpace(host)) return ApiResponse.Fail("host required");
                await PanelDiscovery.IdentifyAsync(host, webPort);
                return ApiResponse.Ok(new { host, webPort }, "Identify flash sent.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Panel identify: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static double GetDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v)
            ? v : fallback;

    private static string GetString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? fallback : fallback;

    private static List<NetworkPanelTarget> ReadPanels(JsonElement root, string host, int port, string panelId)
    {
        if (root.TryGetProperty("panels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<NetworkPanelTarget>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var h = GetString(el, "host", "");
                if (string.IsNullOrWhiteSpace(h)) continue;
                var enabled = !el.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                list.Add(new NetworkPanelTarget
                {
                    Host = h.Trim(),
                    Port = (int)GetDouble(el, "port", port),
                    PanelId = GetString(el, "panelId", ""),
                    Enabled = enabled,
                    X = (int)GetDouble(el, "x", 0),
                    Y = (int)GetDouble(el, "y", 0)
                });
            }
            if (list.Count > 0) return list;
        }

        return
        [
            new NetworkPanelTarget { Host = host, Port = port, PanelId = panelId, Enabled = true }
        ];
    }

    internal static object[] PanelDtos(IEnumerable<NetworkPanelTarget>? panels) =>
        (panels ?? []).Select(p => new
        {
            host = p.Host,
            port = p.Port,
            panelId = p.PanelId,
            enabled = p.Enabled,
            x = p.X,
            y = p.Y
        }).ToArray();

    internal static JsonArray PanelsNode(IEnumerable<NetworkPanelTarget> panels)
    {
        var arr = new JsonArray();
        foreach (var p in panels)
        {
            arr.Add(new JsonObject
            {
                ["Host"] = p.Host,
                ["Port"] = p.Port,
                ["PanelId"] = p.PanelId ?? "",
                ["Enabled"] = p.Enabled,
                ["X"] = p.X,
                ["Y"] = p.Y
            });
        }
        return arr;
    }

    private static void Persist(NetworkMatrixRenderer? net, NetworkOptions? opts, int wallWidth, int wallHeight)
    {
        var targets = opts?.StreamTargets() ?? [];
        var host = targets.FirstOrDefault()?.Host ?? net?.Host ?? opts?.Host;
        if (string.IsNullOrWhiteSpace(host)) return;
        OutputSettingsEndpoints.PersistSection("Network", new Dictionary<string, JsonNode?>
        {
            ["Host"] = host,
            ["Port"] = targets.FirstOrDefault()?.Port ?? net?.Port ?? opts?.Port ?? 7777,
            ["TargetMbps"] = net?.TargetMbps ?? opts?.TargetMbps ?? 19,
            ["ColorBits"] = net?.ColorBits ?? opts?.ColorBits ?? 14,
            ["SwapRedBlue"] = net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
            ["PanelId"] = targets.FirstOrDefault()?.PanelId ?? opts?.PanelId ?? "",
            ["WallWidth"] = wallWidth,
            ["WallHeight"] = wallHeight,
            ["Panels"] = PanelsNode(targets)
        });
        OutputSettingsEndpoints.PersistSection("App", new Dictionary<string, JsonNode?>
        {
            ["DisplayWidth"] = wallWidth,
            ["DisplayHeight"] = wallHeight
        });
    }
}
