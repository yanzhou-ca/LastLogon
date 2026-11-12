using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class ConnectionTestTests
{
    [Theory]
    [InlineData(5, "not an administrator")]
    [InlineData(53, "could not be found")]
    [InlineData(64, "could not be found")]
    [InlineData(67, "does not exist")]
    [InlineData(1326, "credentials were not accepted")]
    public void Hint_ExplainsTheWindowsError(int error, string expected)
        => Assert.Contains(expected, ConnectionTestResult.Hint(error));

    [Fact]
    public void Hint_SaysNothingForAnUnknownError()
        => Assert.Equal(string.Empty, ConnectionTestResult.Hint(9999));

    [Fact]
    public void Hint_SaysNothingWhenThereIsNoError()
        => Assert.Equal(string.Empty, ConnectionTestResult.Hint(null));

    [Fact]
    public void Summary_NamesTheAddressThatWorked()
    {
        var result = new ConnectionTestResult(
            "pc01",
            "10.0.0.5",
            true,
            "pc01.corp.example",
            new[] { new ConnectionAttempt("pc01.corp.example", true, "3 profile folder(s) readable.", null) });

        Assert.Contains("pc01.corp.example", result.Summary);
    }
}
