using System.Text.Json;
using System.Text.Json.Nodes;
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
    public static void MapNetworkConfigEndpoints(this WebApplication app, IMatrixRenderer renderer)
    {
        var runtime = renderer as OutputRuntime;
        NetworkMatrixRenderer? Current() =>
            runtime?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/network", () =>
        {
            try
            {
                var net = Current();
                var opts = runtime?.Network;
                return ApiResponse.Ok(new
                {
                    supported = true,
                    live = net != null,
                    host = net?.Host ?? opts?.Host ?? "",
                    port = net?.Port ?? opts?.Port ?? 7777,
                    targetMbps = net?.TargetMbps ?? opts?.TargetMbps ?? 19,
                    colorBits = net?.ColorBits ?? opts?.ColorBits ?? 14,
                    swapRedBlue = net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
                    wallWidth = opts?.WallWidth ?? 0,
                    wallHeight = opts?.WallHeight ?? 0,
                    panelId = opts?.PanelId ?? ""
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
                var save = !root.TryGetProperty("save", out var s) || s.ValueKind != JsonValueKind.False;

                bool? swap = null;
                if (root.TryGetProperty("swapRedBlue", out var sw) &&
                    (sw.ValueKind == JsonValueKind.True || sw.ValueKind == JsonValueKind.False))
                    swap = sw.GetBoolean();

                if (opts != null)
                {
                    opts.Host = host;
                    opts.Port = port;
                    opts.TargetMbps = mbps;
                    opts.ColorBits = bits;
                    opts.PanelId = panelId;
                    if (swap.HasValue) opts.SwapRedBlue = swap.Value;
                }

                net?.Reconfigure(host, port, mbps, bits);
                if (swap.HasValue) net?.SetSwapRedBlue(swap.Value);
                if (save) Persist(net, opts);

                Console.WriteLine($"[API] Network display -> {host}:{port} {bits}-bit {mbps:0.#} Mbit/s save={save}");

                return ApiResponse.Ok(new
                {
                    supported = true,
                    live = net != null,
                    host,
                    port,
                    targetMbps = mbps,
                    colorBits = bits,
                    swapRedBlue = swap ?? net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
                    panelId,
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

    private static void Persist(NetworkMatrixRenderer? net, NetworkOptions? opts)
    {
        var host = net?.Host ?? opts?.Host;
        if (string.IsNullOrWhiteSpace(host)) return;
        OutputSettingsEndpoints.PersistSection("Network", new Dictionary<string, JsonNode?>
        {
            ["Host"] = host,
            ["Port"] = net?.Port ?? opts?.Port ?? 7777,
            ["TargetMbps"] = net?.TargetMbps ?? opts?.TargetMbps ?? 19,
            ["ColorBits"] = net?.ColorBits ?? opts?.ColorBits ?? 14,
            ["SwapRedBlue"] = net?.SwapRedBlue ?? opts?.SwapRedBlue ?? false,
            ["PanelId"] = opts?.PanelId ?? ""
        });
    }
}
