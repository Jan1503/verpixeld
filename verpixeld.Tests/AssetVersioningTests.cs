using verpixeld.WebApi;

namespace verpixeld.Tests;

public class AssetVersioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "assetver-" + Guid.NewGuid().ToString("N"));

    public AssetVersioningTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "js"));
        Directory.CreateDirectory(Path.Combine(_root, "css"));
        File.WriteAllText(Path.Combine(_root, "js", "app.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(_root, "css", "site.css"), "body{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp folder */ }
    }

    [Fact]
    public void Local_scripts_and_styles_get_a_content_version()
    {
        const string html = """
                            <link rel="stylesheet" href="/css/site.css?v=20260101a">
                            <script src="/js/app.js"></script>
                            """;
        var result = AssetVersioning.Apply(html, _root);

        Assert.Matches("""href="/css/site\.css\?v=[0-9a-f]{10}" """.TrimEnd(), result);
        Assert.Matches("""src="/js/app\.js\?v=[0-9a-f]{10}" """.TrimEnd(), result);
        Assert.DoesNotContain("20260101a", result);
    }

    [Fact]
    public void A_changed_file_gets_a_new_version()
    {
        const string html = """<script src="/js/app.js"></script>""";
        var before = AssetVersioning.Apply(html, _root);

        var file = Path.Combine(_root, "js", "app.js");
        File.WriteAllText(file, "console.log(2);");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        Assert.NotEqual(before, AssetVersioning.Apply(html, _root));
        Assert.Equal(AssetVersioning.Apply(html, _root), AssetVersioning.Apply(html, _root));
    }

    [Fact]
    public void Missing_and_external_references_are_left_alone()
    {
        const string html = """
                            <script src="/js/missing.js?v=1"></script>
                            <script src="https://cdn.example.org/lib.js"></script>
                            <img src="/favicon.svg">
                            """;
        Assert.Equal(html, AssetVersioning.Apply(html, _root));
    }
}
