using System.Reflection;
using System.Runtime.ExceptionServices;
using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

// Changes the static addressing mode and DNS domain of ComputerTarget: see ComputerTargetTests.
[Collection("ComputerTarget static state")]
public class SmbProfileFileSystemTests
{
    [Fact]
    public void Root_QualifiesShortComputerWithCurrentDnsDomain()
    {
        WithDnsDomain("corp.example", () =>
            Assert.Equal(@"\\pc01.corp.example\C$\Users", InvokeRoot("pc01")));
    }

    [Theory]
    [InlineData(@"\\attacker\share")]
    [InlineData("attacker:445")]
    [InlineData("pc01/../pc02")]
    public void Root_RejectsComputerPathAndDomainSyntax(string computer)
    {
        WithDnsDomain("corp.example", () =>
            Assert.Throws<FormatException>(() => InvokeRoot(computer)));
    }

    [Fact]
    public void Root_UsesAFullyQualifiedNameAsItIs()
    {
        WithDnsDomain("corp.example", () =>
            Assert.Equal(@"\\pc01.other.example\C$\Users", InvokeRoot("pc01.other.example")));
    }

    [Fact]
    public void Root_UsesTheShortNameWhenTheOperatorAsksForIt()
    {
        WithDnsDomain("corp.example", () =>
        {
            ComputerTarget.AddressingMode = ComputerTarget.Addressing.ShortName;
            try
            {
                Assert.Equal(@"\\pc01\C$\Users", InvokeRoot("pc01"));
            }
            finally
            {
                ComputerTarget.AddressingMode = ComputerTarget.Addressing.DomainQualified;
            }
        });
    }

    [Fact]
    public void Root_RejectsInvalidCurrentDnsDomain()
    {
        WithDnsDomain("corp/share", () =>
            Assert.Throws<InvalidOperationException>(() => InvokeRoot("pc01")));
    }

    [Fact]
    public void Root_AcceptsAShortNameWithoutADomainInShortNameMode()
    {
        WithDnsDomain("corp/share", () =>
        {
            ComputerTarget.AddressingMode = ComputerTarget.Addressing.ShortName;
            try
            {
                Assert.Equal(@"\\pc01\C$\Users", InvokeRoot("pc01"));
            }
            finally
            {
                ComputerTarget.AddressingMode = ComputerTarget.Addressing.DomainQualified;
            }
        });
    }

    [Fact]
    public void Root_AcceptsAFullyQualifiedNameWithoutADomain()
    {
        WithDnsDomain("corp/share", () =>
            Assert.Equal(@"\\pc01.other.example\C$\Users", InvokeRoot("pc01.other.example")));
    }

    [Fact]
    public void Root_RejectsProfileNameThatCouldEscapeTheShare()
    {
        WithDnsDomain("corp.example", () =>
        {
            var method = typeof(SmbProfileFileSystem).GetMethod("GetFolderTime")!;
            var exception = Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(new SmbProfileFileSystem(), new object[] { "pc01", "..\\..\\Windows" }));
            Assert.IsType<ArgumentException>(exception.InnerException);
        });
    }

    // Review focus 3: a folder name from a remote listing reaches the two new file calls
    [Theory]
    [InlineData("..\\..\\Windows")]
    [InlineData("a/b")]
    [InlineData("")]
    public void NewFileCalls_RefuseAFolderNameThatCouldEscapeTheShare(string folder)
    {
        WithDnsDomain("corp.example", () =>
        {
            var fs = new SmbProfileFileSystem();
            Assert.Throws<ArgumentException>(() => fs.GetFolderAccessSids("pc01", folder));
            Assert.Throws<ArgumentException>(() => fs.GetProfileUse("pc01", folder));
        });
    }

    private static string InvokeRoot(string computer)
    {
        var method = typeof(SmbProfileFileSystem).GetMethod("Root", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return (string)method.Invoke(null, new object[] { computer })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static void WithDnsDomain(string domain, Action action)
    {
        var previous = ComputerTarget.DnsDomain;
        try
        {
            ComputerTarget.DnsDomain = domain;
            action();
        }
        finally
        {
            ComputerTarget.DnsDomain = previous;
        }
    }
}
