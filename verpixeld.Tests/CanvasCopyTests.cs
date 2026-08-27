using verpixeld.Layout;

namespace verpixeld.Tests;

public class CanvasCopyTests
{
    [Fact]
    public void UniqueName_appends_copy_then_numbers()
    {
        Assert.Equal("Main copy", CanvasCopy.UniqueName("Main", ["Main"]));
        Assert.Equal("Main copy 2", CanvasCopy.UniqueName("Main", ["Main", "Main copy"]));
        Assert.Equal("Main copy 3", CanvasCopy.UniqueName("Main", ["Main", "Main copy", "Main copy 2"]));
    }

    [Fact]
    public void UniqueName_is_case_insensitive()
    {
        Assert.Equal("clock copy 2", CanvasCopy.UniqueName("clock", ["clock", "Clock copy"]));
    }

    [Fact]
    public void UniqueName_falls_back_when_source_is_blank()
    {
        Assert.Equal("Overlay copy", CanvasCopy.UniqueName("  ", []));
    }
}
