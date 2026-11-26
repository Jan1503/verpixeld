using verpixeld.Layout;

namespace verpixeld.Tests;

public class CanvasNamesTests
{
    [Theory]
    [InlineData("Ticker")]
    [InlineData("Bob's \"TV\" & <i>")] // quotes and markup are fine: the web page escapes them
    [InlineData("Küche – links")]
    public void Ordinary_names_are_accepted(string name) => Assert.Null(CanvasNames.Problem(name));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("a/b")] // an encoded slash is not decoded in the /api/canvas/{name} route
    [InlineData("a\\b")]
    [InlineData("tab\there")]
    public void Unaddressable_or_empty_names_are_refused(string? name) => Assert.NotNull(CanvasNames.Problem(name));

    [Fact]
    public void Long_names_are_refused() =>
        Assert.NotNull(CanvasNames.Problem(new string('x', CanvasNames.MaxLength + 1)));
}
