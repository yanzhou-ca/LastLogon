using LastLogon.Core;

namespace LastLogon.Tests;

public class WindowsLogonEventSourceTests
{
    [Fact]
    public void WindowsSource_BuildsXPathForSupportedIdsAndUtcRange()
    {
        var fromUtc = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var toUtc = new DateTimeOffset(2025, 3, 5, 8, 9, 10, TimeSpan.Zero);

        var xpath = WindowsLogonEventSource.BuildXPath(fromUtc, toUtc);

        Assert.Contains("EventID=4624", xpath);
        Assert.Contains("EventID=4625", xpath);
        Assert.Contains("EventID=4634", xpath);
        Assert.Contains("EventID=4647", xpath);
        Assert.Contains("EventID=4800", xpath);
        Assert.Contains("EventID=4801", xpath);
        Assert.Contains("EventID=4672", xpath);
        Assert.Contains("2025-03-04T05:06:07.0000000Z", xpath);
        Assert.Contains("2025-03-05T08:09:10.0000000Z", xpath);
    }
}
