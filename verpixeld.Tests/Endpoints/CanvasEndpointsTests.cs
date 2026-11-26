namespace verpixeld.Tests.Endpoints;

/// <summary>Creating and renaming canvases: names the API could not address again are refused.</summary>
[Collection(HostCollection.Name)]
public class CanvasEndpointsTests(HostFixture host)
{
    private static object Canvas(string name) => new { name, x = 0, y = 0, width = 64, height = 32, zOrder = 50 };

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_refuses_names_that_cannot_be_addressed(string name)
    {
        var result = await host.PostJson("/api/canvas/create", Canvas(name));

        Assert.False(result.Success());
        Assert.False(string.IsNullOrEmpty(result.Str("error")));
    }

    [Fact]
    public async Task Create_refuses_names_over_64_characters()
    {
        var result = await host.PostJson("/api/canvas/create", Canvas(new string('x', 65)));

        Assert.False(result.Success());
    }

    [Fact]
    public async Task Create_rename_and_remove_with_a_quoted_name()
    {
        await host.PostJson("/api/layout/apply/fullscreen");
        var name = "Test " + Guid.NewGuid().ToString("N")[..6];
        var renamed = $"Jan's \"{name}\"";
        try
        {
            Assert.True((await host.PostJson("/api/canvas/create", Canvas(name))).Success());
            Assert.False((await host.PostJson("/api/canvas/create", Canvas(name))).Success()); // exists already

            var bad = await host.PutJson($"/api/canvas/{Uri.EscapeDataString(name)}/rename", new { newName = "x/y" });
            Assert.False(bad.Success());

            var ok = await host.PutJson($"/api/canvas/{Uri.EscapeDataString(name)}/rename", new { newName = renamed });
            Assert.True(ok.Success(), ok.ToString());

            var stack = await host.GetJson("/api/canvas/stack");
            var names = stack.GetProperty("data").EnumerateArray().Select(c => c.Str("name")).ToList();
            Assert.Contains(renamed, names);
            Assert.DoesNotContain(name, names);

            Assert.True((await host.PostJson($"/api/canvas/{Uri.EscapeDataString(renamed)}/remove")).Success());
        }
        finally
        {
            await host.Client.PostAsync($"/api/canvas/{Uri.EscapeDataString(name)}/remove", null);
            await host.Client.PostAsync($"/api/canvas/{Uri.EscapeDataString(renamed)}/remove", null);
        }
    }

    [Fact]
    public async Task Base_canvases_cannot_be_renamed()
    {
        await host.PostJson("/api/layout/apply/fullscreen");

        var result = await host.PutJson("/api/canvas/Main/rename", new { newName = "Other" });

        Assert.False(result.Success());
    }
}
