using System.Text.Json;
using PixPlane;
using verpixeld.Hardware;

namespace verpixeld.WebApi;

/// <summary>
///     Per-column seam correction (driver-cascade boundary columns, e.g. 63/127/191/255).
///     Live when the network output is active; otherwise reads/writes seam_correction.json so the
///     values are ready the next time that output is selected.
/// </summary>
public static class SeamEndpoints
{
    private static readonly string DefaultSeamFile = Path.Combine(AppContext.BaseDirectory, "seam_correction.json");

    public static void MapSeamEndpoints(this WebApplication app, IMatrixRenderer renderer)
    {
        NetworkMatrixRenderer? Current() =>
            (renderer as OutputRuntime)?.As<NetworkMatrixRenderer>() ?? renderer as NetworkMatrixRenderer;

        app.MapGet("/api/settings/seam", () =>
        {
            try
            {
                var net = Current();
                var src = net != null ? net.GetSeamColumns() : LoadSeamFromFile();
                var cols = new List<object>();
                foreach (var c in src)
                    cols.Add(new { x = c.X, gainR = c.GainR, gainG = c.GainG, gainB = c.GainB, liftR = c.LiftR, liftG = c.LiftG, liftB = c.LiftB });
                return ApiResponse.Ok(new { supported = true, columns = cols, live = net != null });
            }
            catch (Exception ex) { return ApiResponse.Error(ex); }
        });

        app.MapPost("/api/settings/seam", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var root = doc.RootElement;
                var save = !root.TryGetProperty("save", out var s) || s.ValueKind != JsonValueKind.False;

                var list = new List<SeamColumn>();
                if (root.TryGetProperty("columns", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var x = (int)GetD(e, "x", -1);
                        if (x < 0 || x >= 256) continue;
                        list.Add(new SeamColumn
                        {
                            X = x,
                            GainR = Clamp(GetD(e, "gainR", 1.0)), GainG = Clamp(GetD(e, "gainG", 1.0)), GainB = Clamp(GetD(e, "gainB", 1.0)),
                            LiftR = ClampLift(GetD(e, "liftR", 0.0)), LiftG = ClampLift(GetD(e, "liftG", 0.0)), LiftB = ClampLift(GetD(e, "liftB", 0.0))
                        });
                    }
                }

                var net = Current();
                if (net != null) net.SetSeam(list, save);
                else if (save) WriteSeamFile(list);

                Console.WriteLine($"[API] Seam correction: {list.Count} columns, save={save}, live={net != null}");

                var outCols = new List<object>();
                foreach (var c in list)
                    outCols.Add(new { x = c.X, gainR = c.GainR, gainG = c.GainG, gainB = c.GainB, liftR = c.LiftR, liftG = c.LiftG, liftB = c.LiftB });
                return ApiResponse.Ok(new { supported = true, columns = outCols, saved = save, live = net != null });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[API] Error setting seam correction: {ex.Message}");
                return ApiResponse.Error(ex);
            }
        });
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 4 ? 4 : v;
    private static double ClampLift(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static double GetD(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v) ? v : fallback;

    private sealed class SeamCol
    {
        public int X { get; set; } = -1;
        public double GainR { get; set; } = 1;
        public double GainG { get; set; } = 1;
        public double GainB { get; set; } = 1;
        public double LiftR { get; set; }
        public double LiftG { get; set; }
        public double LiftB { get; set; }
    }

    private sealed class SeamConfig { public List<SeamCol> Columns { get; set; } = []; }

    private static List<SeamColumn> LoadSeamFromFile()
    {
        try
        {
            if (!File.Exists(DefaultSeamFile)) return DefaultColumns();
            var cfg = JsonSerializer.Deserialize<SeamConfig>(File.ReadAllText(DefaultSeamFile),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var list = new List<SeamColumn>();
            foreach (var c in cfg?.Columns ?? [])
                list.Add(new SeamColumn
                {
                    X = c.X, GainR = c.GainR, GainG = c.GainG, GainB = c.GainB,
                    LiftR = c.LiftR, LiftG = c.LiftG, LiftB = c.LiftB
                });
            return list.Count > 0 ? list : DefaultColumns();
        }
        catch { return DefaultColumns(); }
    }

    private static void WriteSeamFile(IReadOnlyList<SeamColumn> cols)
    {
        var cfg = new SeamConfig();
        foreach (var c in cols)
            cfg.Columns.Add(new SeamCol
            {
                X = c.X, GainR = c.GainR, GainG = c.GainG, GainB = c.GainB,
                LiftR = c.LiftR, LiftG = c.LiftG, LiftB = c.LiftB
            });
        File.WriteAllText(DefaultSeamFile, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static List<SeamColumn> DefaultColumns()
    {
        SeamColumn Col(int x) => new()
        {
            X = x, GainR = 0.85, GainG = 0.85, GainB = 0.85, LiftR = 0.004, LiftG = 0.004, LiftB = 0.004
        };
        return [Col(63), Col(127), Col(191), Col(255)];
    }
}
