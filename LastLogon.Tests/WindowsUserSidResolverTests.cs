using LastLogon.Core;

namespace LastLogon.Tests;

public class WindowsUserSidResolverTests
{
    [Fact]
    public void AccountNameFor_KeepsADomainAccountName()
        => Assert.Equal(@"CORP\jdoe", WindowsUserSidResolver.AccountNameFor(@"CORP\jdoe", "corp.local"));

    [Fact]
    public void AccountNameFor_KeepsAUserPrincipalName()
        => Assert.Equal("jdoe@corp.local", WindowsUserSidResolver.AccountNameFor("jdoe@corp.local", null));

    [Fact]
    public void AccountNameFor_QualifiesAPlainNameWithTheDnsDomain()
        => Assert.Equal("jdoe@corp.local", WindowsUserSidResolver.AccountNameFor("jdoe", "corp.local"));

    // A plain name must not be looked up on this computer: it could be a local account of the technician's PC.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corp/share")]
    public void AccountNameFor_IsNullForAPlainNameWithoutAUsableDomain(string? domain)
        => Assert.Null(WindowsUserSidResolver.AccountNameFor("jdoe", domain));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"a\b\c")]
    [InlineData(@"\jdoe")]
    [InlineData(@"CORP\")]
    [InlineData("jdo\u0001e")]
    [InlineData("CORP\\jd\roe")]
    public void AccountNameFor_IsNullForTextThatIsNotAnAccountName(string text)
        => Assert.Null(WindowsUserSidResolver.AccountNameFor(text, "corp.local"));

    [Fact]
    public void AccountNameFor_IsNullForTextOfMoreThan256Characters()
    {
        var name = new string('a', 250) + "@x.test";

        Assert.Equal(257, name.Length);
        Assert.Null(WindowsUserSidResolver.AccountNameFor(name, "corp.local"));
    }

    [Fact]
    public void AccountNameFor_AcceptsTextOf256Characters()
    {
        var name = new string('a', 249) + "@x.test";

        Assert.Equal(256, name.Length);
        Assert.Equal(name, WindowsUserSidResolver.AccountNameFor(name, "corp.local"));
    }

    // The two tests below translate accounts of this computer, which needs no network.
    [Fact]
    public async Task ResolveAsync_TranslatesAWellKnownAccountToItsSid()
        => Assert.Equal("S-1-5-32-544", await WindowsUserSidResolver.ResolveAsync(@"BUILTIN\Administrators", CancellationToken.None));

    [Fact]
    public async Task ResolveAsync_IsNullForAnAccountThatDoesNotExist()
        => Assert.Null(await WindowsUserSidResolver.ResolveAsync(@"BUILTIN\NoSuchGroupForTheLastLogonTests", CancellationToken.None));

    [Fact]
    public async Task ResolveAsync_ThrowsWhenTheScanIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WindowsUserSidResolver.ResolveAsync(@"BUILTIN\Administrators", cts.Token));
    }
}
