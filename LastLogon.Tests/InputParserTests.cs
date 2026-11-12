using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class InputParserTests
{
    [Theory]
    [InlineData("pc01")]
    [InlineData("PC-01")]
    [InlineData("a")]
    [InlineData("pc01.corp.example")]
    [InlineData("pc01.corp.example.com")]
    public void IsValidComputerName_AcceptsHostNames(string name)
        => Assert.True(InputParser.IsValidComputerName(name));

    [Theory]
    [InlineData("")]
    [InlineData(@"\\attacker\share")]
    [InlineData("attacker:445")]
    [InlineData("-pc01")]
    [InlineData("pc01-")]
    [InlineData("pc_01")]
    [InlineData("pc01.")]
    [InlineData(".pc01")]
    [InlineData("pc..01")]
    [InlineData("pc01..corp")]
    [InlineData("pc 01")]
    public void IsValidComputerName_RejectsAnythingThatCouldAddressAnotherHost(string name)
        => Assert.False(InputParser.IsValidComputerName(name));

    [Theory]
    [InlineData("alice")]
    [InlineData("jane.doe")]
    [InlineData("user_name")]
    [InlineData("O'Brien")]
    [InlineData("=cmd('calc'!A0)")]
    [InlineData("user#1")]
    public void IsSafeProfileFolderName_AcceptsNamesWindowsAllows(string name)
        => Assert.True(InputParser.IsSafeProfileFolderName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("..\\..\\Windows")]
    [InlineData("../../Windows")]
    [InlineData("a\"b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a:b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\r\nb")]
    public void IsSafeProfileFolderName_RejectsNamesThatCouldEscapeAPathOrCommandLine(string? name)
        => Assert.False(InputParser.IsSafeProfileFolderName(name));
}

public class ComputerTargetTests
{
    [Fact]
    public void Qualify_UsesTheGivenDomain()
        => Assert.Equal("pc01.corp.example", ComputerTarget.Qualify("pc01", "corp.example"));

    [Fact]
    public void Qualify_KeepsTheBareNameWhenTheDomainIsUnusable()
    {
        Assert.Equal("pc01", ComputerTarget.Qualify("pc01", "corp/share"));
        Assert.Equal("pc01", ComputerTarget.Qualify("pc01", ""));
    }

    [Fact]
    public void Qualify_LeavesAFullyQualifiedNameAlone()
        => Assert.Equal("pc01.other.example", ComputerTarget.Qualify("pc01.other.example", "corp.example"));

    [Fact]
    public void Qualify_UsesTheShortNameWhenTheOperatorAsksForIt()
    {
        var previous = ComputerTarget.AddressingMode;
        try
        {
            ComputerTarget.AddressingMode = ComputerTarget.Addressing.ShortName;
            Assert.Equal("pc01", ComputerTarget.Qualify("pc01", "corp.example"));
        }
        finally
        {
            ComputerTarget.AddressingMode = previous;
        }
    }

    [Fact]
    public void Qualify_CanIgnoreTheOperatorsChoiceForACallerThatTriesBothForms()
    {
        var previous = ComputerTarget.AddressingMode;
        try
        {
            ComputerTarget.AddressingMode = ComputerTarget.Addressing.ShortName;
            Assert.Equal(
                "pc01.corp.example",
                ComputerTarget.Qualify(
                    "pc01",
                    "corp.example",
                    ComputerTarget.Addressing.DomainQualified));
        }
        finally
        {
            ComputerTarget.AddressingMode = previous;
        }
    }

    [Fact]
    public void UncPath_BuildsAPathInTheAdministrativeShare()
        => Assert.Equal(
            @"\\pc01.corp.example\C$\Users\alice",
            ComputerTarget.UncPathInDomain("pc01", "corp.example", "Users", "alice"));

    [Theory]
    [InlineData("corp.example", true)]
    [InlineData("corp-share", true)]        // a single label is a legal domain name
    [InlineData("corp/share", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidDnsDomain_RejectsAnythingThatIsNotADomainName(string? domain, bool expected)
        => Assert.Equal(expected, ComputerTarget.IsValidDnsDomain(domain));
}
