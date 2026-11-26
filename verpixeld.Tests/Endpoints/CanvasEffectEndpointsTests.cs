using System.Text.Json;

namespace verpixeld.Tests.Endpoints;

/// <summary>Effects on one canvas: API, and that they survive scenes, resizing and duplicating.</summary>
[Collection(HostCollection.Name)]
public class CanvasEffectEndpointsTests(HostFixture host)
{
    private async Task<List<JsonElement>> Effects(string canvas) =>
        (await host.GetJson($"/api/canvas/{Uri.EscapeDataString(canvas)}/effects")).GetProperty("data").EnumerateArray().ToList();

    private async Task<string> NewCanvas()
    {
        await host.PostJson("/api/layout/apply/fullscreen");
        var name = "Fx " + Guid.NewGuid().ToString("N")[..6];
        var created = await host.PostJson("/api/canvas/create", new { name, x = 0, y = 0, width = 64, height = 32, zOrder = 40 });
        Assert.True(created.Success(), created.ToString());
        return name;
    }

    [Fact]
    public async Task Add_change_reorder_and_remove_effects()
    {
        var canvas = await NewCanvas();
        var path = $"/api/canvas/{Uri.EscapeDataString(canvas)}/effects";

        var pixelate = await host.PostJson(path, new { filterType = "PixelateFilter", parameters = new { Intensity = 0.5 } });
        Assert.True(pixelate.Success(), pixelate.ToString());
        Assert.True((await host.PostJson(path, new { filterType = "Scanline" })).Success()); // by display name

        var list = await Effects(canvas);
        Assert.Equal(["PixelateFilter", "ScanlineFilter"], list.Select(e => e.Str("filterType")));
        Assert.Equal(0.5, list[0].GetProperty("intensity").GetDouble(), 3);

        var changed = await host.PutJson($"{path}/0", new { parameters = new { Intensity = 0.25, Enabled = false } });
        Assert.True(changed.Success(), changed.ToString());
        list = await Effects(canvas);
        Assert.Equal(0.25, list[0].GetProperty("intensity").GetDouble(), 3);
        Assert.False(list[0].GetProperty("enabled").GetBoolean());

        Assert.True((await host.PostJson($"{path}/0/move?delta=1")).Success());
        Assert.Equal(["ScanlineFilter", "PixelateFilter"], (await Effects(canvas)).Select(e => e.Str("filterType")));
        Assert.False((await host.PostJson($"{path}/1/move?delta=1")).Success()); // already last

        Assert.True((await host.Delete($"{path}/0")).Success());
        Assert.Equal(["PixelateFilter"], (await Effects(canvas)).Select(e => e.Str("filterType")));
        Assert.False((await host.Delete($"{path}/5")).Success());

        await host.PostJson($"/api/canvas/{Uri.EscapeDataString(canvas)}/remove");
    }

    [Fact]
    public async Task Unknown_effects_and_canvases_are_refused()
    {
        var canvas = await NewCanvas();

        var unknown = await host.PostJson($"/api/canvas/{Uri.EscapeDataString(canvas)}/effects", new { filterType = "NoSuchEffect" });
        Assert.False(unknown.Success());
        Assert.False((await host.PostJson($"/api/canvas/{Uri.EscapeDataString(canvas)}/effects", new { })).Success());
        Assert.False((await host.GetJson("/api/canvas/NoSuchCanvas/effects")).Success());

        await host.PostJson($"/api/canvas/{Uri.EscapeDataString(canvas)}/remove");
    }

    [Fact]
    public async Task Effects_survive_resize_duplicate_and_a_saved_scene()
    {
        await host.PostJson("/api/layout/apply/fullscreen");
        var scene = "Fx scene " + Guid.NewGuid().ToString("N")[..6];
        string? copy = null;
        try
        {
            Assert.True((await host.PostJson("/api/canvas/Main/effects", new { filterType = "PixelateFilter", parameters = new { Intensity = 0.4 } })).Success());

            // Resizing recreates the canvas: the effect stays.
            var resized = await host.PutJson("/api/canvas/Main/bounds", new { x = 0, y = 0, width = 192, height = 96 });
            Assert.True(resized.Success(), resized.ToString());
            Assert.Equal(["PixelateFilter"], (await Effects("Main")).Select(e => e.Str("filterType")));

            // A duplicate gets its own copy of the chain.
            var dup = await host.PostJson("/api/canvas/Main/duplicate");
            Assert.True(dup.Success(), dup.ToString());
            copy = dup.GetProperty("data").Str("name")!;
            Assert.Equal(["PixelateFilter"], (await Effects(copy)).Select(e => e.Str("filterType")));
            await host.PostJson($"/api/canvas/{Uri.EscapeDataString(copy)}/remove");
            copy = null;

            // The scene needs content on Main to keep its canvas: a clock is enough.
            await host.PostJson("/api/layout/assign", new { canvasName = "Main", extensionName = "Digital Clock" });
            Assert.True((await host.PostJson("/api/layout/save", new { name = scene, overrideGlobalBrightness = false })).Success());

            await host.PostJson("/api/layout/apply/fullscreen");
            Assert.Empty(await Effects("Main"));

            var loaded = await host.PostJson($"/api/layout/load/{Uri.EscapeDataString(scene)}");
            Assert.True(loaded.Success(), loaded.ToString());
            var effects = await Effects("Main");
            Assert.Single(effects);
            Assert.Equal("PixelateFilter", effects[0].Str("filterType"));
            Assert.Equal(0.4, effects[0].GetProperty("intensity").GetDouble(), 3);
        }
        finally
        {
            if (copy != null) await host.Client.PostAsync($"/api/canvas/{Uri.EscapeDataString(copy)}/remove", null);
            await host.Client.DeleteAsync($"/api/layout/saved/{Uri.EscapeDataString(scene)}");
            await host.PostJson("/api/layout/apply/fullscreen");
        }
    }

    [Fact]
    public async Task Include_filters_saves_the_wall_wide_chain_with_the_scene()
    {
        await host.PostJson("/api/layout/apply/fullscreen");
        await host.PostJson("/api/filters/clear");
        var scene = "Fx wall " + Guid.NewGuid().ToString("N")[..6];
        try
        {
            Assert.True((await host.PostJson("/api/filters/add", new { filterType = "VignetteFilter" })).Success());
            await host.PostJson("/api/layout/assign", new { canvasName = "Main", extensionName = "Digital Clock" });
            Assert.True((await host.PostJson("/api/layout/save", new { name = scene, includeFilters = true })).Success());

            var saved = await host.GetJson($"/api/layout/saved/{Uri.EscapeDataString(scene)}");
            var filters = saved.GetProperty("data").GetProperty("filters").EnumerateArray().Select(f => f.Str("filterType")).ToList();
            Assert.Equal(["VignetteFilter"], filters);
        }
        finally
        {
            await host.PostJson("/api/filters/clear");
            await host.Client.DeleteAsync($"/api/layout/saved/{Uri.EscapeDataString(scene)}");
        }
    }
}
