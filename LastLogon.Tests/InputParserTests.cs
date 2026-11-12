using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class InputParserTests
{
    [Fact]
    public void ParseComputers_TrimsDedupesIgnoresBlank()
    {
        var r = InputParser.ParseComputers(" pc01\r\n\r\nPC01\npc02 ,pc03");
        Assert.Equal(new[] { "pc01", "pc02", "pc03" }, r);
    }

    [Theory]
    [InlineData(@"\\attacker\share")]
    [InlineData("attacker/share")]
    [InlineData("attacker:445")]
    [InlineData("bad..hostname")]
    [InlineData("pc01.corp.local")]
    public void ParseComputers_RejectsPathsAndInvalidHostnames(string input)
        => Assert.Throws<FormatException>(() => InputParser.ParseComputers(input));

    [Theory]
    [InlineData("CORP\\jdoe")]
    [InlineData("jdoe@corp.local")]
    [InlineData(" jdoe ")]
    public void NormalizeUsername_StripsDomainAndUpn(string input)
        => Assert.Equal("jdoe", InputParser.NormalizeUsername(input));

    [Fact]
    public void NormalizeUsername_Empty_ReturnsEmpty()
        => Assert.Equal("", InputParser.NormalizeUsername("  "));
}
