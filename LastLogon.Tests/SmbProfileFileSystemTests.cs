using System.Reflection;
using System.Runtime.ExceptionServices;
using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

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
    [InlineData("pc01.corp.example")]
    [InlineData("attacker:445")]
    public void Root_RejectsComputerPathAndDomainSyntax(string computer)
    {
        WithDnsDomain("corp.example", () =>
            Assert.Throws<FormatException>(() => InvokeRoot(computer)));
    }

    [Fact]
    public void Root_RejectsInvalidCurrentDnsDomain()
    {
        WithDnsDomain("corp/share", () =>
            Assert.Throws<InvalidOperationException>(() => InvokeRoot("pc01")));
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
        var previous = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        try
        {
            Environment.SetEnvironmentVariable("USERDNSDOMAIN", domain);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERDNSDOMAIN", previous);
        }
    }
}
