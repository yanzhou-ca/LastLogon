using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class ConnectionTestTests
{
    [Theory]
    [InlineData(5, "not an administrator")]
    [InlineData(53, "could not be found")]
    [InlineData(64, "could not be found")]
    [InlineData(67, "C$ share is turned off")]
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
        Assert.Contains("3 profile folder(s) readable", result.Summary);
    }

    [Fact]
    public void Summary_ReportsTheWindowsErrorForASingleAttempt()
    {
        var result = new ConnectionTestResult(
            "pc01",
            null,
            false,
            "pc01.corp.example",
            new[]
            {
                new ConnectionAttempt(
                    "pc01.corp.example",
                    false,
                    @"Access to the path '\\pc01.corp.example\C$\Users' is denied.",
                    5),
            });

        var summary = result.Summary;

        Assert.Contains("Windows error 5", summary);
        Assert.Contains("not an administrator", summary);
    }

    [Fact]
    public void Summary_DoesNotThrowWhenNothingWasAttempted()
    {
        var result = new ConnectionTestResult(
            "pc01",
            null,
            false,
            "pc01",
            Array.Empty<ConnectionAttempt>());

        Assert.Contains("Not reachable", result.Summary);
    }

    [Fact]
    public void Candidates_TriesTheQualifiedNameFirstByDefault()
        => Assert.Equal(
            new[] { "pc01.corp.example", "pc01" },
            ConnectionTest.Candidates(
                "pc01",
                ComputerTarget.Addressing.DomainQualified,
                "corp.example"));

    [Fact]
    public void Candidates_TriesTheShortNameFirstWhenTheOperatorAskedForIt()
        => Assert.Equal(
            new[] { "pc01", "pc01.corp.example" },
            ConnectionTest.Candidates(
                "pc01",
                ComputerTarget.Addressing.ShortName,
                "corp.example"));

    [Fact]
    public void Candidates_UsesAFullyQualifiedNameAsItIs()
        => Assert.Equal(
            new[] { "pc01.other.example" },
            ConnectionTest.Candidates(
                "pc01.other.example",
                ComputerTarget.Addressing.DomainQualified,
                "corp.example"));

    [Fact]
    public void Run_RejectsAPathInsteadOfAHostName()
        => Assert.Throws<FormatException>(() =>
            ConnectionTest.Run(
                @"\\pc01\C$",
                ComputerTarget.Addressing.DomainQualified));

    [Fact]
    public void Readable_RemovesThePathThatDirectoryErrorsAppend()
        => Assert.Equal(
            "The specified network name is no longer available.",
            ConnectionTest.Readable(
                @"The specified network name is no longer available. : '\\pc01\C$\Users'"));

    [Fact]
    public void Readable_KeepsAMessageWithoutAPath()
        => Assert.Equal("Access is denied.", ConnectionTest.Readable("Access is denied."));

    [Theory]
    [InlineData(0x80070005u, 5)]
    [InlineData(0x80070040u, 64)]
    public void ErrorCode_ReadsAWin32Error(uint hresult, int expected)
        => Assert.Equal(
            expected,
            ConnectionTest.ErrorCode(new FakeException(unchecked((int)hresult))));

    [Theory]
    [InlineData(0x80131500u)]
    [InlineData(0x80070000u)]
    public void ErrorCode_IgnoresAnErrorThatIsNotAWin32Code(uint hresult)
        => Assert.Null(ConnectionTest.ErrorCode(new FakeException(unchecked((int)hresult))));

    private sealed class FakeException : Exception
    {
        public FakeException(int hresult) => HResult = hresult;
    }
}
