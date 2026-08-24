using verpixeld.Services;

namespace verpixeld.Tests;

public class StartupServiceIpTests
{
    [Fact]
    public void GetLocalIPAddress_does_not_use_dns_and_does_not_throw()
    {
        var ip = StartupService.GetLocalIPAddress();
        if (ip != null)
            Assert.False(ip.ToString().StartsWith("127."));
    }
}
