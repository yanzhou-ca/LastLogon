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
