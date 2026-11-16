using System.Globalization;
using LastLogon.Core;

namespace LastLogon.Tests;

public class LoginInfoTests
{
    private static readonly DateTimeOffset Now = TestHistory.T("2026-11-13T20:00:00Z");

    private static LoginInfo Info(
        LoggedInState state,
        DateTimeOffset? since = null,
        DateTimeOffset? login = null,
        DateTimeOffset? logout = null,
        DateTime? file = null,
        DateTimeOffset? begin = null,
        bool used = true)
        => new(state, since, login, logout, file, begin, used, string.Empty);

    // The expected texts are the local time of the machine the test runs on, so no test depends on the time zone.
    private static string Local(DateTimeOffset time, string format)
        => time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    // ---- Last login

    [Fact]
    public void LastLoginText_IsTheNewestLoginInLocalTime()
    {
        var login = TestHistory.T("2026-10-15T16:25:45Z");

        Assert.Equal(Local(login, "yyyy-MM-dd HH:mm"), Info(LoggedInState.No, login: login).LastLoginText);
    }

    [Fact]
    public void LastLoginText_SaysBeforeTheDateTheRecordsBegin()
    {
        var begin = TestHistory.T("2026-05-12T12:00:00Z");

        Assert.Equal("Before " + Local(begin, "yyyy-MM-dd"), Info(LoggedInState.No, begin: begin).LastLoginText);
    }

    [Fact]
    public void LastLoginText_IsBlankWhenTheRecordsWereNotUsed()
    {
        Assert.Equal(string.Empty, Info(LoggedInState.No, begin: TestHistory.T("2026-05-12T12:00:00Z"), used: false).LastLoginText);
        Assert.Equal(string.Empty, Info(LoggedInState.No, used: true).LastLoginText);
    }

    // ---- Logged in now

    [Theory]
    [InlineData(29 * 86400 + 3 * 3600, "Yes, 29 days")]
    [InlineData(1 * 86400 + 2 * 3600, "Yes, 1 day")]
    [InlineData(5 * 3600 + 30 * 60, "Yes, 5 hours")]
    [InlineData(1 * 3600, "Yes, 1 hour")]
    [InlineData(12 * 60, "Yes, 12 minutes")]
    [InlineData(30, "Yes, 0 minutes")]
    public void LoggedInNowText_CountsInTheLargestFittingUnit(int secondsAgo, string expected)
    {
        var since = Now.AddSeconds(-secondsAgo);

        Assert.Equal(expected, Info(LoggedInState.Yes, since: since).LoggedInNowText(Now));
    }

    [Fact]
    public void LoggedInNowText_SaysOverWhenTheLoginIsOlderThanTheRecords()
    {
        var begin = Now.AddDays(-185).AddHours(-2);

        Assert.Equal("Yes, over 185 days", Info(LoggedInState.Yes, begin: begin).LoggedInNowText(Now));
    }

    [Fact]
    public void LoggedInNowText_IsJustYesWhenTheTimeIsUnknown()
    {
        Assert.Equal("Yes", Info(LoggedInState.Yes, used: false).LoggedInNowText(Now));
        Assert.Equal("Yes", Info(LoggedInState.Yes, begin: null, used: true).LoggedInNowText(Now));
        Assert.Equal("Yes", Info(LoggedInState.Yes, begin: Now.AddDays(-185), used: false).LoggedInNowText(Now));
    }

    [Fact]
    public void LoggedInNowText_IsNoForNoAndBlankForUnknown()
    {
        Assert.Equal("No", Info(LoggedInState.No).LoggedInNowText(Now));
        Assert.Equal(string.Empty, Info(LoggedInState.Unknown).LoggedInNowText(Now));
    }

    // Review focus 2: a login in the future, and a daylight-saving change
    [Fact]
    public void LoggedInNowText_NeverShowsANegativeDuration()
        => Assert.Equal("Yes, 0 minutes", Info(LoggedInState.Yes, since: Now.AddDays(3)).LoggedInNowText(Now));

    [Fact]
    public void LoggedInNowText_CountsInUtcAcrossADaylightSavingChange()
    {
        // 01:30 EST to 03:30 EDT on the night the clocks go forward is one hour, not two.
        var since = DateTimeOffset.Parse("2026-03-08T01:30:00-05:00", CultureInfo.InvariantCulture);
        var now = DateTimeOffset.Parse("2026-03-08T03:30:00-04:00", CultureInfo.InvariantCulture);

        Assert.Equal("Yes, 1 hour", Info(LoggedInState.Yes, since: since).LoggedInNowText(now));
    }

    // ---- Days ago

    [Fact]
    public void DaysAgo_IsNeverNegative()
        => Assert.Equal((0, false), Info(LoggedInState.No, logout: Now.AddDays(2)).DaysAgo(Now));

    [Fact]
    public void DaysAgo_IsZeroForAUserWhoIsLoggedIn()
        => Assert.Equal((0, false), Info(LoggedInState.Yes, since: Now.AddDays(-29), logout: Now.AddDays(-30)).DaysAgo(Now));

    [Fact]
    public void DaysAgo_CountsFromTheNewestTimeTheRowShows()
        => Assert.Equal((4, false), Info(LoggedInState.No, login: Now.AddDays(-10), logout: Now.AddDays(-4)).DaysAgo(Now));

    [Fact]
    public void DaysAgo_UsesTheFileTimeShownAsTheLogout()
        => Assert.Equal((621, false), Info(LoggedInState.No, file: Now.AddDays(-621).AddHours(-1).LocalDateTime).DaysAgo(Now));

    [Fact]
    public void DaysAgo_IsALowerBoundWhenOnlyTheDateTheRecordsBeginIsKnown()
        => Assert.Equal((185, true), Info(LoggedInState.No, begin: Now.AddDays(-185).AddHours(-1)).DaysAgo(Now));

    [Fact]
    public void DaysAgo_IsUnknownWhenNothingIsKnown()
    {
        Assert.Null(Info(LoggedInState.Unknown, used: false).DaysAgo(Now));
        Assert.Null(Info(LoggedInState.No, begin: Now.AddDays(-185), used: false).DaysAgo(Now));
    }

    // ---- Last logout

    [Fact]
    public void LastLogoutText_PrefersTheRecordOverTheFile()
    {
        var record = TestHistory.T("2026-10-14T20:46:08Z");
        var file = new DateTime(2025, 3, 2, 9, 40, 0);

        Assert.Equal(Local(record, "yyyy-MM-dd HH:mm"), Info(LoggedInState.No, logout: record, file: file).LastLogoutText);
        Assert.Equal("2025-03-02 09:40", Info(LoggedInState.No, file: file).LastLogoutText);
        Assert.Equal(string.Empty, Info(LoggedInState.No).LastLogoutText);
    }

    [Fact]
    public void LastSeen_IsTheNewestExactTime()
    {
        var withSince = Info(LoggedInState.Yes, since: Now.AddDays(-3), login: Now.AddDays(-3), logout: Now.AddDays(-5), file: Now.AddDays(-9).LocalDateTime);
        var withFile = Info(LoggedInState.No, logout: Now.AddDays(-4), file: Now.AddDays(-1).LocalDateTime);
        var withLogout = Info(LoggedInState.No, login: Now.AddDays(-6), logout: Now.AddDays(-2));

        Assert.Equal(Now.AddDays(-3).LocalDateTime, withSince.LastSeen);
        Assert.Equal(Now.AddDays(-1).LocalDateTime, withFile.LastSeen);
        Assert.Equal(Now.AddDays(-2).LocalDateTime, withLogout.LastSeen);
        Assert.Null(Info(LoggedInState.Unknown, begin: Now.AddDays(-185)).LastSeen);
    }

    [Fact]
    public void IsApproximate_IsTrueOnlyWhenTheLogoutIsTheFileTime()
    {
        Assert.True(Info(LoggedInState.No, file: Now.AddDays(-1).LocalDateTime).IsApproximate);
        Assert.False(Info(LoggedInState.No, logout: Now.AddDays(-1)).IsApproximate);
        Assert.False(Info(LoggedInState.No).IsApproximate);
    }
}
