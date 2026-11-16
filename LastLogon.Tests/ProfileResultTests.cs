using System.Globalization;
using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class ProfileResultTests
{
    private static ProfileResult Row(string username, ScanStatus status)
        => new("pc1", username, null, null, status, null);

    [Fact]
    public void CanOpenFolder_IsTrueForAProfileThatWasFound()
        => Assert.True(Row("alice", ScanStatus.Ok).CanOpenFolder);

    // A profile that exists but could not be read is still worth opening: the operator may have
    // more luck in Explorer than the scan had.
    [Fact]
    public void CanOpenFolder_IsTrueForAProfileThatCouldNotBeRead()
        => Assert.True(Row("alice", ScanStatus.AccessDenied).CanOpenFolder);

    // The folder of a user who has no profile on the computer does not exist, so Explorer would
    // only report an error.
    [Fact]
    public void CanOpenFolder_IsFalseForAComputerWithoutThatProfile()
        => Assert.False(Row("ghost", ScanStatus.NoProfile).CanOpenFolder);

    [Fact]
    public void CanOpenFolder_IsFalseForARowThatNamesNoProfile()
        => Assert.False(Row("", ScanStatus.Offline).CanOpenFolder);

    // The grid shows these texts to an operator, so they must not be the names of enum members.
    [Theory]
    [InlineData(ActivitySource.NtUserDat, "NTUSER.DAT")]
    [InlineData(ActivitySource.Folder, "Folder time")]
    [InlineData(ActivitySource.LoginRecords, "Login records")]
    public void SourceText_NamesTheSourceTheWayAnOperatorReadsIt(ActivitySource source, string expected)
        => Assert.Equal(expected, new ProfileResult("pc1", "alice", null, source, ScanStatus.Ok, null).SourceText);

    [Fact]
    public void SourceText_IsEmptyWhenTheRowHasNoSource()
        => Assert.Equal("", Row("alice", ScanStatus.Offline).SourceText);

    [Theory]
    [InlineData(ScanStatus.NoProfile, "No profile")]
    [InlineData(ScanStatus.AccessDenied, "Access denied")]
    [InlineData(ScanStatus.Offline, "Offline")]
    [InlineData(ScanStatus.TimedOut, "Timed out")]
    [InlineData(ScanStatus.Error, "Error")]
    public void ResultText_SaysWhatHappenedInWords(ScanStatus status, string expected)
        => Assert.Equal(expected, Row("alice", status).ResultText);

    [Fact]
    public void ResultText_IsOkWhenTheTimeCameFromNtUserDat()
        => Assert.Equal(
            "OK",
            new ProfileResult("pc1", "alice", new DateTime(2025, 1, 1), ActivitySource.NtUserDat, ScanStatus.Ok, null).ResultText);

    // A folder time is only an approximation of the activity, so the row must not read as a plain OK.
    [Fact]
    public void ResultText_IsApproximateWhenOnlyTheFolderTimeIsKnown()
        => Assert.Equal(
            "Approximate",
            new ProfileResult("pc1", "alice", new DateTime(2025, 1, 1), ActivitySource.Folder, ScanStatus.Ok, "NTUSER.DAT not found; using folder time").ResultText);

    [Fact]
    public void DetailText_IsTheMessageOfTheRow()
        => Assert.Equal(
            "Offline or C$ unreachable",
            new ProfileResult("pc1", "alice", null, null, ScanStatus.Offline, "Offline or C$ unreachable").DetailText);

    [Fact]
    public void DetailText_IsEmptyWhenThereIsNoMessage()
        => Assert.Equal("", Row("alice", ScanStatus.Ok).DetailText);

    // ---- The login facts of a row

    private static LoginInfo Info(
        LoggedInState state,
        DateTimeOffset? since = null,
        DateTimeOffset? login = null,
        DateTimeOffset? logout = null,
        DateTime? file = null,
        DateTimeOffset? begin = null,
        bool used = true)
        => new(state, since, login, logout, file, begin, used, "");

    private static ProfileResult WithLogin(
        LoginInfo? info, DateTime? lastActivity = null, ActivitySource? source = ActivitySource.NtUserDat, string user = "alice")
        => new("pc1", user, lastActivity, source, ScanStatus.Ok, null, info);

    private static string Local(DateTimeOffset time, string format)
        => time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    [Fact]
    public void DaysAgo_IsZeroForAUserWhoIsLoggedIn_WhateverTheFileTime()
    {
        var row = WithLogin(Info(LoggedInState.Yes), lastActivity: DateTime.Now.AddDays(-400));

        Assert.Equal(0, row.DaysAgo);
        Assert.Equal("0", row.DaysAgoText);
    }

    [Fact]
    public void DaysAgoText_CarriesAPlusWhenOnlyALowerBoundIsKnown()
    {
        var row = WithLogin(Info(LoggedInState.No, begin: DateTimeOffset.UtcNow.AddDays(-185).AddHours(-1)));

        Assert.Equal(185, row.DaysAgo);
        Assert.Equal("185+", row.DaysAgoText);
    }

    [Fact]
    public void DaysAgo_IsBlankWhenTheLoginInfoKnowsNoTime()
    {
        var row = WithLogin(Info(LoggedInState.Unknown, used: false));

        Assert.Null(row.DaysAgo);
        Assert.Equal("", row.DaysAgoText);
    }

    [Fact]
    public void DaysAgo_WithoutLoginInfo_IsStillCountedFromTheFileTime()
    {
        var row = WithLogin(null, lastActivity: DateTime.Now.AddDays(-10).AddHours(-1));

        Assert.Equal(10, row.DaysAgo);
        Assert.Equal("10", row.DaysAgoText);
    }

    [Fact]
    public void ResultText_IsApproximateWhenTheLogoutTimeCameFromTheProfileFile()
        => Assert.Equal("Approximate", WithLogin(Info(LoggedInState.No, file: new DateTime(2025, 3, 2, 9, 40, 0), used: false)).ResultText);

    [Fact]
    public void ResultText_IsOkWhenEveryTimeCameFromTheRecords()
        => Assert.Equal(
            "OK",
            WithLogin(Info(LoggedInState.No, login: DateTimeOffset.UtcNow.AddDays(-3)), source: ActivitySource.LoginRecords).ResultText);

    [Fact]
    public void ResultText_StaysAFailureForARowThatDidNotWork()
    {
        var row = new ProfileResult("pc1", "alice", null, null, ScanStatus.Offline, "Offline", Info(LoggedInState.No, file: new DateTime(2025, 3, 2)));

        Assert.Equal("Offline", row.ResultText);
    }

    [Fact]
    public void LastLogoutText_IsTheFileTimeOnlyForARowWithoutLoginInfo()
    {
        var file = new DateTime(2025, 3, 4, 5, 6, 0);

        Assert.Equal("2025-03-04 05:06", WithLogin(null, lastActivity: file).LastLogoutText);
        Assert.Equal("", WithLogin(Info(LoggedInState.No), lastActivity: file).LastLogoutText);
        Assert.Equal("2025-03-04 05:06", WithLogin(Info(LoggedInState.No, file: file), lastActivity: file).LastLogoutText);
    }

    [Fact]
    public void LoginTexts_AreBlankForARowWithoutLoginInfo()
    {
        var row = WithLogin(null, lastActivity: new DateTime(2025, 3, 4, 5, 6, 0));

        Assert.Equal("", row.LastLoginText);
        Assert.Equal("", row.LoggedInNowText);
        Assert.False(row.IsLoggedIn);
    }

    [Fact]
    public void LoginTexts_ComeFromTheLoginInfo()
    {
        var login = DateTimeOffset.UtcNow.AddDays(-29).AddHours(-3);
        var row = WithLogin(Info(LoggedInState.Yes, since: login, login: login));

        Assert.Equal(Local(login, "yyyy-MM-dd HH:mm"), row.LastLoginText);
        Assert.Equal("Yes, 29 days", row.LoggedInNowText);
        Assert.True(row.IsLoggedIn);
        Assert.False(WithLogin(Info(LoggedInState.No)).IsLoggedIn);
        Assert.False(WithLogin(Info(LoggedInState.Unknown)).IsLoggedIn);
    }

    [Fact]
    public void SortKeys_FollowTheColumns()
    {
        var login = TestHistory.T("2026-10-15T16:25:45Z");
        var logout = TestHistory.T("2026-10-14T20:46:08Z");
        var file = new DateTime(2025, 3, 2, 9, 40, 0);

        var both = WithLogin(Info(LoggedInState.Yes, since: login, login: login, logout: logout));
        var fileOnly = WithLogin(Info(LoggedInState.No, file: file, used: false));

        Assert.Equal(login.LocalDateTime, both.LastLoginSort);
        Assert.Equal(logout.LocalDateTime, both.LastLogoutSort);
        Assert.Equal(2, both.LoggedInSort);
        Assert.Null(fileOnly.LastLoginSort);
        Assert.Equal(file, fileOnly.LastLogoutSort);
        Assert.Equal(1, fileOnly.LoggedInSort);
        Assert.Equal(0, WithLogin(Info(LoggedInState.Unknown)).LoggedInSort);
        Assert.Equal(0, WithLogin(null).LoggedInSort);
        Assert.Equal(new DateTime(2025, 3, 4, 5, 6, 0), WithLogin(null, lastActivity: new DateTime(2025, 3, 4, 5, 6, 0)).LastLogoutSort);
    }

    [Fact]
    public void LastSeen_IsTheNewestTimeOfTheLoginInfoOrTheFileTimeOfARowWithout()
    {
        var logout = TestHistory.T("2026-10-14T20:46:08Z");

        Assert.Equal(logout.LocalDateTime, WithLogin(Info(LoggedInState.No, logout: logout), lastActivity: new DateTime(2020, 1, 1)).LastSeen);
        Assert.Equal(new DateTime(2020, 1, 1), WithLogin(null, lastActivity: new DateTime(2020, 1, 1)).LastSeen);
        Assert.Null(WithLogin(null).LastSeen);
    }

    [Fact]
    public void CompareForDisplay_PutsLoggedInUsersFirstThenTheNewestThenRowsWithoutATime()
    {
        var old = WithLogin(Info(LoggedInState.No, logout: TestHistory.T("2026-01-01T10:00:00Z")), user: "old");
        var loggedIn = WithLogin(Info(LoggedInState.Yes, since: TestHistory.T("2026-09-01T10:00:00Z"), logout: TestHistory.T("2026-08-30T10:00:00Z")), user: "loggedIn");
        var newest = WithLogin(Info(LoggedInState.No, logout: TestHistory.T("2026-11-12T10:00:00Z")), user: "newest");
        var noTime = WithLogin(Info(LoggedInState.Unknown, used: false), user: "noTime");
        var sameAsNewest = WithLogin(Info(LoggedInState.No, logout: TestHistory.T("2026-11-12T10:00:00Z")), user: "sameAsNewest");

        var sorted = new[] { old, noTime, sameAsNewest, newest, loggedIn }
            .OrderBy(row => row, Comparer<ProfileResult>.Create(ProfileResult.CompareForDisplay))
            .Select(row => row.Username)
            .ToArray();

        // equal rows keep the order they came in
        Assert.Equal(new[] { "loggedIn", "sameAsNewest", "newest", "old", "noTime" }, sorted);
        Assert.Equal(0, ProfileResult.CompareForDisplay(newest, sameAsNewest));
        Assert.Equal(0, ProfileResult.CompareForDisplay(noTime, noTime));
    }

    [Fact]
    public void CompareForDisplay_OrdersRowsWithoutLoginInfoByTheirFileTime()
    {
        var older = WithLogin(null, lastActivity: new DateTime(2024, 1, 1), user: "older");
        var newer = WithLogin(null, lastActivity: new DateTime(2025, 1, 1), user: "newer");
        var none = WithLogin(null, user: "none");

        var sorted = new[] { none, older, newer }
            .OrderBy(row => row, Comparer<ProfileResult>.Create(ProfileResult.CompareForDisplay))
            .Select(row => row.Username);

        Assert.Equal(new[] { "newer", "older", "none" }, sorted);
    }
}
