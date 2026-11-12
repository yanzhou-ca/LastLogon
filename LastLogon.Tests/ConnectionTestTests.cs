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

    private static ConnectionTestResult Result(params ConnectionAttempt[] attempts)
        => new("pc01", attempts[0].ResolvedAddress, attempts.Any(a => a.Succeeded), attempts[0].AddressedAs, attempts);

    [Fact]
    public void DescribeAttempts_ShowsTheAddressOfEveryNameAndWhetherItWorked()
    {
        var result = Result(
            new ConnectionAttempt("pc01.corp.example", false, "denied", 5, "10.0.0.5"),
            new ConnectionAttempt("pc01", true, "3 profile folder(s) readable.", null, "10.0.0.9"));

        Assert.Equal(
            "Tried: pc01.corp.example (10.0.0.5) failed, Windows error 5; pc01 (10.0.0.9) reachable. "
            + "The names resolve to different addresses, so they reach different computers.",
            result.DescribeAttempts());
    }

    [Fact]
    public void DescribeAttempts_SaysWhenBothNamesResolveToTheSameAddress()
    {
        var result = Result(
            new ConnectionAttempt("pc01.corp.example", false, "denied", 5, "10.0.0.5"),
            new ConnectionAttempt("pc01", false, "denied", 5, "10.0.0.5"));

        var text = result.DescribeAttempts();

        Assert.Contains("Both names resolve to the same address.", text);
        Assert.DoesNotContain("different", text);
    }

    [Fact]
    public void DescribeAttempts_ShowsANameThatDoesNotResolve()
    {
        var result = Result(
            new ConnectionAttempt("pc01.corp.example", false, "not found", 53, null),
            new ConnectionAttempt("pc01", true, null, null, "10.0.0.9"));

        var text = result.DescribeAttempts();

        Assert.Contains("pc01.corp.example (no address) failed, Windows error 53", text);
        // One address cannot be compared with none, so no claim is made about the computers.
        Assert.DoesNotContain("different", text);
        Assert.DoesNotContain("same address", text);
    }

    [Fact]
    public void DescribeAttempts_ForOneNameStatesTheAddressWithoutComparing()
    {
        var result = Result(new ConnectionAttempt("pc01.other.example", false, "denied", 5, "10.0.0.5"));

        Assert.Equal(
            "Tried: pc01.other.example (10.0.0.5) failed, Windows error 5.",
            result.DescribeAttempts());
    }

    [Fact]
    public void DescribeAttempts_LeavesOutTheErrorNumberWhenThereIsNone()
    {
        var result = Result(new ConnectionAttempt("pc01", false, "no answer", null, "10.0.0.5"));

        Assert.Equal("Tried: pc01 (10.0.0.5) failed.", result.DescribeAttempts());
    }

    [Fact]
    public void DescribeAttempts_IsEmptyWhenNothingWasAttempted()
        => Assert.Equal(
            string.Empty,
            new ConnectionTestResult("pc01", null, false, "pc01", Array.Empty<ConnectionAttempt>()).DescribeAttempts());

    private sealed class FakeException : Exception
    {
        public FakeException(int hresult) => HResult = hresult;
    }
}
