using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class PluralTests
{
    [Theory]
    [InlineData(0, "0 computers")]
    [InlineData(1, "1 computer")]
    [InlineData(2, "2 computers")]
    [InlineData(12, "12 computers")]
    public void Count_UsesTheSingularOnlyForOne(int count, string expected)
        => Assert.Equal(expected, Plural.Count(count, "computer"));

    [Fact]
    public void Count_TakesAnIrregularPlural()
    {
        Assert.Equal("1 match", Plural.Count(1, "match", "matches"));
        Assert.Equal("2 matches", Plural.Count(2, "match", "matches"));
    }
}
