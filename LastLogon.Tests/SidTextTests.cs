using LastLogon.Core;

namespace LastLogon.Tests;

public class SidTextTests
{
    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001")]
    [InlineData(" s-1-5-21-1-2-3-1001\r\n", "S-1-5-21-1-2-3-1001")]
    [InlineData("S-1-12-1-1-2-3-4", "S-1-12-1-1-2-3-4")]
    public void TryNormalize_AcceptsAWellFormedSid(string text, string expected)
    {
        Assert.True(SidText.TryNormalize(text, out var sid));
        Assert.Equal(expected, sid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("S-1")]
    [InlineData("S-1-5")]
    [InlineData("S-1-5-21-1-2-3-x")]
    [InlineData("S-1-5-21-1' or 'a'='a")]
    [InlineData("S-1-5-21-1-2-3-1001'] | //*")]
    [InlineData("S-1-5-21-12345678901-1")]
    public void TryNormalize_RefusesAnythingElse(string? text) => Assert.False(SidText.TryNormalize(text, out _));

    [Theory]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-12-1-1-2-3-4", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-32-544", false)]
    [InlineData("S-1-15-3-65536-1-2-3-4-5-6-7", false)]
    public void IsAccountSid_KeepsUserAndGroupAccountsOnly(string sid, bool expected) => Assert.Equal(expected, SidText.IsAccountSid(sid));

    [Fact]
    public void Same_IgnoresCaseAndRefusesNull()
    {
        Assert.True(SidText.Same("s-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1001"));
        Assert.False(SidText.Same(null, "S-1-5-21-1-2-3-1001"));
        Assert.False(SidText.Same("S-1-5-21-1-2-3-1001", null));
        Assert.False(SidText.Same("S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002"));
    }
}
