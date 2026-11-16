using LastLogon.Core;

namespace LastLogon.Tests;

/// <summary>The records of one user, times on 2026-10-15 written as "hh:mm:ss".</summary>
public class LoginRecordsTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private static DateTimeOffset At(string time) => TestHistory.T("2026-10-15T" + time + "Z");

    private static LoginRecord Login(string time, int? session = 2, LoginLog log = LoginLog.System)
        => TestHistory.Login(Sid, "2026-10-15T" + time + "Z", session, log);

    private static LoginRecord Logout(string time, int? session = 2, LoginLog log = LoginLog.System)
        => TestHistory.Logout(Sid, "2026-10-15T" + time + "Z", session, log);

    [Fact]
    public void NewestOpenLogin_IsNullWhenEverySessionWasClosed()
        => Assert.Null(LoginRecords.NewestOpenLogin(new[] { Logout("11:00:00"), Login("10:00:00") }));

    [Fact]
    public void NewestOpenLogin_IsTheLoginOfASessionWithoutALogout()
        => Assert.Equal(At("10:00:00"), LoginRecords.NewestOpenLogin(new[] { Login("10:00:00") }));

    [Fact]
    public void NewestOpenLogin_UsesTheLatestRecordOfAReusedSessionNumber()
        => Assert.Equal(At("12:00:00"), LoginRecords.NewestOpenLogin(new[] { Login("12:00:00"), Logout("11:00:00"), Login("10:00:00") }));

    [Fact]
    public void NewestOpenLogin_PrefersTheNewestOfTwoOpenSessions()
        => Assert.Equal(At("11:00:00"), LoginRecords.NewestOpenLogin(new[] { Login("11:00:00", 3), Login("10:00:00", 1) }));

    [Fact]
    public void NewestOpenLogin_KeepsAnOlderSessionOpenWhenAnotherClosed()
        => Assert.Equal(At("10:00:00"), LoginRecords.NewestOpenLogin(
            new[] { Logout("12:00:00", 3), Login("11:00:00", 3), Login("10:00:00", 1) }));

    [Fact]
    public void NewestOpenLogin_PairsAMissingSessionWithAMissingSession()
    {
        Assert.Null(LoginRecords.NewestOpenLogin(new[] { Logout("11:00:00", null), Login("10:00:00", null) }));
        Assert.Equal(At("10:00:00"), LoginRecords.NewestOpenLogin(new[] { Logout("11:00:00", 2), Login("10:00:00", null) }));
    }

    [Fact]
    public void NewestOpenLogin_TreatsTheSameLoginInBothLogsAsOne()
    {
        var records = new[]
        {
            Logout("11:00:01", 2, LoginLog.UserProfileService), Logout("11:00:00", 2, LoginLog.System),
            Login("10:00:01", 2, LoginLog.UserProfileService), Login("10:00:00", 2, LoginLog.System),
        };

        Assert.Null(LoginRecords.NewestOpenLogin(records));
    }

    [Fact]
    public void NewestOpenLogin_LetsALogoutAtTheSameTimeCloseTheSession()
    {
        Assert.Null(LoginRecords.NewestOpenLogin(new[] { Login("10:00:00"), Logout("10:00:00") }));
        Assert.Null(LoginRecords.NewestOpenLogin(new[] { Logout("10:00:00"), Login("10:00:00") }));
    }

    [Fact]
    public void NewestOpenLogin_DoesNotDependOnTheOrderOfTheRecords()
    {
        var records = new[] { Login("12:00:00"), Logout("11:00:00"), Login("10:00:00") };

        Assert.Equal(At("12:00:00"), LoginRecords.NewestOpenLogin(records));
        Assert.Equal(At("12:00:00"), LoginRecords.NewestOpenLogin(records.Reverse()));
        Assert.Equal(At("12:00:00"), LoginRecords.NewestOpenLogin(new[] { records[1], records[2], records[0] }));
    }

    [Fact]
    public void NewestLogin_And_NewestLogout_ReturnTheLatestOfTheirKind()
    {
        var records = new[] { Login("10:00:00"), Logout("11:00:00"), Login("12:00:00", 3), Logout("09:00:00", 1) };

        Assert.Equal(At("12:00:00"), LoginRecords.NewestLogin(records));
        Assert.Equal(At("11:00:00"), LoginRecords.NewestLogout(records));
        Assert.Null(LoginRecords.NewestLogin(new[] { Logout("11:00:00") }));
        Assert.Null(LoginRecords.NewestLogout(new[] { Login("10:00:00") }));
        Assert.Null(LoginRecords.NewestLogin(Array.Empty<LoginRecord>()));
    }
}
