using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class AdComputerSearchTests
{
    [Fact]
    public void BuildFilter_EscapesLdapSpecialChars()
        => Assert.Equal("(&(objectCategory=computer)(name=pc\\2a\\28x\\29*))", AdComputerSearch.BuildFilter("pc*(x)"));

    [Fact]
    public void BuildFilter_EmptyPrefix_MatchesAllComputers()
        => Assert.Equal("(&(objectCategory=computer)(name=*))", AdComputerSearch.BuildFilter(""));
}

public class AdDistinguishedNameTests
{
    private const string LocalContext = "DC=corp,DC=example";

    [Theory]
    [InlineData("OU=Sales")]
    [InlineData("OU=Sales,OU=West")]
    [InlineData("OU=Sales,DC=corp,DC=example")]
    public void AcceptsNamesInsideTheLocalDomain(string dn)
        => Assert.True(AdComputerSearch.IsValidDistinguishedName(dn, LocalContext));

    [Theory]
    [InlineData("evil.example.com/OU=Sales")]     // server component
    [InlineData("LDAP://OU=Sales,DC=corp,DC=example")]
    [InlineData("OU=Sales,DC=evil,DC=com")]        // another domain
    [InlineData("OU=Sales/DC=corp")]              // server component in the middle
    [InlineData("Sales")]                          // not a distinguished name
    [InlineData("OU=")]                           // empty value
    [InlineData("=Sales")]                        // empty type
    [InlineData("OU=Sales\r\nX-Injected: 1")]     // control characters
    public void RejectsNamesThatCouldRedirectTheBind(string dn)
        => Assert.False(AdComputerSearch.IsValidDistinguishedName(dn, LocalContext));

    [Fact]
    public void RejectsEveryNameWhenTheLocalContextIsUnknown()
        => Assert.False(AdComputerSearch.IsValidDistinguishedName("OU=Sales,DC=corp,DC=example", ""));

    // The dialog checks the form of the name as soon as it is entered, without asking the
    // directory. Whether the name is inside this computer's own domain is only known to the
    // directory, so that stays a matter for the search.
    [Theory]
    [InlineData("OU=Sales")]
    [InlineData("OU=Sales,OU=West")]
    [InlineData("CN=Computers,DC=corp,DC=example")]
    [InlineData("OU=Sales,DC=evil,DC=com")]       // another domain: well formed, refused by the search
    [InlineData("  OU=Sales  ")]
    [InlineData("")]                               // the field is optional
    [InlineData("   ")]
    [InlineData(null)]
    public void AWellFormedOrEmptyNameHasNoProblemOfForm(string? dn)
        => Assert.Null(AdComputerSearch.DescribeDistinguishedNameProblem(dn));

    [Theory]
    [InlineData("evil.example.com/OU=Sales")]
    [InlineData("LDAP://OU=Sales,DC=corp,DC=example")]
    [InlineData("OU=Sales/DC=corp")]
    [InlineData("Sales")]
    [InlineData("OU=")]
    [InlineData("=Sales")]
    [InlineData("OU=Sales\r\nX-Injected: 1")]
    public void AMalformedNameIsDescribedWithAnExample(string dn)
    {
        var problem = AdComputerSearch.DescribeDistinguishedNameProblem(dn);

        Assert.NotNull(problem);
        Assert.Contains("OU=Sales", problem);
    }

    [Theory]
    [InlineData("OU=Sales")]
    [InlineData("Sales")]
    [InlineData("OU=Sales/DC=corp")]
    public void TheDescriptionAndTheValidityCheckAgreeOnTheFormOfAName(string dn)
        => Assert.Equal(
            AdComputerSearch.DescribeDistinguishedNameProblem(dn) is null,
            AdComputerSearch.IsValidDistinguishedName(dn, LocalContext));
}
