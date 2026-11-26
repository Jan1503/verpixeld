namespace verpixeld.Tests.Endpoints;

/// <summary>Saved scenes through the API: save, load, the scene on display, blank profiles, delete.</summary>
[Collection(HostCollection.Name)]
public class LayoutEndpointsTests(HostFixture host)
{
    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private static string Path(string name) => Uri.EscapeDataString(name);

    private async Task<string?> CurrentScene() => (await host.GetJson("/api/layout/current")).GetProperty("data").Str("scene");

    private async Task<List<string>> SavedNames() =>
        (await host.GetJson("/api/layout/saved")).GetProperty("data").EnumerateArray()
        .Select(l => l.Str("name") ?? "").ToList();

    [Fact]
    public async Task Scene_on_display_follows_save_profile_load_and_delete()
    {
        var name = Unique("Bob's \"Scene\"");
        try
        {
            Assert.True((await host.PostJson("/api/layout/apply/fullscreen")).Success());
            Assert.Null(await CurrentScene());

            var saved = await host.PostJson("/api/layout/save", new { name, overrideGlobalBrightness = false });
            Assert.True(saved.Success(), saved.ToString());
            Assert.Equal(name, await CurrentScene());
            Assert.Contains(name, await SavedNames());

            // A blank profile replaces the display: no saved scene is shown any more.
            Assert.True((await host.PostJson("/api/layout/apply/splitview")).Success());
            Assert.Null(await CurrentScene());
            var canvases = (await host.GetJson("/api/layout/current")).GetProperty("data").GetProperty("canvasCount").GetInt32();
            Assert.Equal(2, canvases);

            var loaded = await host.PostJson($"/api/layout/load/{Path(name)}");
            Assert.True(loaded.Success(), loaded.ToString());
            Assert.Equal(name, await CurrentScene());
            Assert.Equal("fullscreen", (await host.GetJson("/api/layout/current")).GetProperty("data").Str("profile"));

            // Deleting the scene on display clears it.
            Assert.True((await host.Delete($"/api/layout/saved/{Path(name)}")).Success());
            Assert.Null(await CurrentScene());
            Assert.DoesNotContain(name, await SavedNames());
        }
        finally
        {
            await host.Client.DeleteAsync($"/api/layout/saved/{Path(name)}");
        }
    }

    [Fact]
    public async Task Loading_an_unknown_scene_fails_and_keeps_the_display()
    {
        await host.PostJson("/api/layout/apply/fullscreen");
        var before = await CurrentScene();

        var result = await host.PostJson($"/api/layout/load/{Path(Unique("missing"))}");

        Assert.False(result.Success());
        Assert.Contains("not found", result.Str("error"));
        Assert.Equal(before, await CurrentScene());
    }

    [Fact]
    public async Task Saving_requires_a_name()
    {
        var result = await host.PostJson("/api/layout/save", new { name = "" });

        Assert.False(result.Success());
    }
}
