using System.Globalization;
using LastLogon.Core;

namespace LastLogon.Tests;

public class LoginAssessorTests
{
    private const string Jdoe = "S-1-5-21-1-2-3-1001";
    private const string Asmith = "S-1-5-21-1-2-3-1002";
    private static readonly FolderMatch Match = FolderMatch.Of(Jdoe);

    private static DateTimeOffset T(string utc) => TestHistory.T(utc);

    private static ProfileFolder Folder(DateTime? file = null)
        => new("jdoe", file, ActivitySource.NtUserDat, new[] { Jdoe });

    private static LoginRecord Login(string utc, int? session = 2) => TestHistory.Login(Jdoe, utc, session);

    private static LoginRecord Logout(string utc, int? session = 2) => TestHistory.Logout(Jdoe, utc, session);

    private static LoginRecord OfSomeoneElse() => TestHistory.Login(Asmith, "2026-11-12T08:02:00Z");

    private static ProfileUse NeverAsked() => throw new InvalidOperationException("The in-use check was made.");

    private static string LocalDate(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static LoginHistory Scenario(string scenario) => scenario switch
    {
        "open login" => TestHistory.Of(new[] { Login("2026-10-15T16:25:45Z") }),
        "logged out" => TestHistory.Of(new[] { Login("2026-10-15T16:25:45Z"), Logout("2026-10-15T20:00:00Z") }),
        "no record" => TestHistory.Of(new[] { OfSomeoneElse() }),
        "unusable" => TestHistory.Of(Array.Empty<LoginRecord>(), read: false),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    // ---- Logged in now: the table of "What a row says"

    [Theory]
    [InlineData("open login", ProfileUse.InUse, LoggedInState.Yes)]
    [InlineData("open login", ProfileUse.NotInUse, LoggedInState.No)]
    [InlineData("open login", ProfileUse.Unknown, LoggedInState.Yes)]
    [InlineData("logged out", ProfileUse.InUse, LoggedInState.No)]
    [InlineData("no record", ProfileUse.InUse, LoggedInState.Yes)]
    [InlineData("no record", ProfileUse.NotInUse, LoggedInState.No)]
    [InlineData("no record", ProfileUse.Unknown, LoggedInState.Unknown)]
    [InlineData("unusable", ProfileUse.InUse, LoggedInState.Yes)]
    [InlineData("unusable", ProfileUse.NotInUse, LoggedInState.No)]
    [InlineData("unusable", ProfileUse.Unknown, LoggedInState.Unknown)]
    public void Assess_FollowsTheTable(string scenario, ProfileUse use, LoggedInState expected)
        => Assert.Equal(expected, LoginAssessor.Assess(Match, Folder(), Scenario(scenario), () => use).LoggedIn);

    [Fact]
    public void Assess_OpenLogin_SaysSinceTheNewestOpenLogin()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("open login"), () => ProfileUse.InUse);

        Assert.Equal(T("2026-10-15T16:25:45Z"), info.LoggedInSince);
        Assert.Equal(T("2026-10-15T16:25:45Z"), info.LastLogin);
        Assert.True(info.RecordsUsed);
        Assert.Equal(T("2026-05-12T12:00:00Z"), info.RecordsBegin);
        Assert.False(info.IsApproximate);
    }

    [Fact]
    public void Assess_OpenLoginWithoutAProfileInUse_SaysTheSessionEnded()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("open login"), () => ProfileUse.NotInUse);

        Assert.Equal(LoggedInState.No, info.LoggedIn);
        Assert.Null(info.LoggedInSince);
        Assert.Contains(LoginAssessor.SessionEnded, info.Details);
    }

    [Fact]
    public void Assess_NoRecordButInUse_IsLoggedInSinceBeforeTheRecordsBegin()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("no record"), () => ProfileUse.InUse);

        Assert.Equal(LoggedInState.Yes, info.LoggedIn);
        Assert.Null(info.LoggedInSince);
        Assert.True(info.RecordsUsed);
        Assert.Equal(T("2026-05-12T12:00:00Z"), info.RecordsBegin);
        Assert.Null(info.LastLogin);
    }

    [Fact]
    public void Assess_LastLoginAndLastLogoutAreTheNewestRecords()
    {
        var history = TestHistory.Of(new[]
        {
            Login("2026-10-10T08:00:00Z"), Logout("2026-10-10T17:00:00Z"),
            Login("2026-10-14T08:00:00Z"), Logout("2026-10-14T20:46:08Z")
        });

        var info = LoginAssessor.Assess(Match, Folder(), history, NeverAsked);

        Assert.Equal(T("2026-10-14T08:00:00Z"), info.LastLogin);
        Assert.Equal(T("2026-10-14T20:46:08Z"), info.LastLogoutRecord);
        Assert.Null(info.LastLogoutFile);
    }

    [Fact]
    public void Assess_DoesNotAskWhetherTheProfileIsInUseForAUserWhoLoggedOut()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("logged out"), NeverAsked);

        Assert.Equal(LoggedInState.No, info.LoggedIn);
    }

    // ---- Last logout: when the profile file's time is shown

    [Fact]
    public void Assess_NoRecord_FileOlderThanTheRecordsBegin_ShowsTheFileAsTheLogout()
    {
        var file = new DateTime(2025, 3, 2, 9, 40, 0);

        var info = LoginAssessor.Assess(Match, Folder(file), Scenario("no record"), () => ProfileUse.NotInUse);

        Assert.Equal(file, info.LastLogoutFile);
        Assert.True(info.IsApproximate);
        Assert.Contains(LoginAssessor.NoLoginSince, info.Details);
    }

    [Fact]
    public void Assess_NoRecord_FileNewerThanTheRecordsBegin_ShowsNoLogoutAndSaysSomethingElseWroteIt()
    {
        var info = LoginAssessor.Assess(Match, Folder(new DateTime(2026, 10, 20, 9, 0, 0)), Scenario("no record"), () => ProfileUse.NotInUse);

        Assert.Null(info.LastLogoutFile);
        Assert.False(info.IsApproximate);
        Assert.Contains(LoginAssessor.FileNotALogin, info.Details);
    }

    [Fact]
    public void Assess_RecordsWithoutALogout_DoNotShowTheFileTime()
    {
        var info = LoginAssessor.Assess(Match, Folder(new DateTime(2025, 3, 2, 9, 40, 0)), Scenario("open login"), () => ProfileUse.NotInUse);

        Assert.Null(info.LastLogoutFile);
        Assert.Null(info.LastLogoutRecord);
    }

    // ---- When the records cannot be used, the row rests on the profile file

    public static TheoryData<string, string> UnusableCases => new()
    {
        { "neither log could be read", "could not be read" },
        { "no record of anyone", "hold no login or logout records" },
        { "no record and no date", "cannot be dated" },
        { "no match", "access list" }
    };

    [Theory]
    [MemberData(nameof(UnusableCases))]
    public void Assess_WhenTheRecordsCannotBeUsed_RestsOnTheProfileFile(string reason, string expectedInDetails)
    {
        var file = new DateTime(2025, 3, 2, 9, 40, 0);
        var match = Match;
        var history = reason switch
        {
            "neither log could be read" => TestHistory.Of(Array.Empty<LoginRecord>(), read: false),
            "no record of anyone" => TestHistory.Of(Array.Empty<LoginRecord>(), anyone: false),
            "no record and no date" => TestHistory.Of(new[] { OfSomeoneElse() }, begins: null),
            _ => TestHistory.Of(new[] { Login("2026-10-15T16:25:45Z") })
        };
        if (reason == "no match")
        {
            match = FolderMatch.None(ProfileFolderMatcher.AccessListUnreadable);
        }

        var info = LoginAssessor.Assess(match, Folder(file), history, () => ProfileUse.NotInUse);

        Assert.False(info.RecordsUsed);
        Assert.Null(info.LastLogin);
        Assert.Null(info.LastLogoutRecord);
        Assert.Null(info.RecordsBegin);
        Assert.Equal(file, info.LastLogoutFile);
        Assert.True(info.IsApproximate);
        Assert.Contains(expectedInDetails, info.Details);
        Assert.Contains(LoginAssessor.FromTheProfileFile, info.Details);
    }

    [Fact]
    public void Assess_WhenTheLogsCouldNotBeRead_GivesTheFailureOfTheFirstLog()
    {
        var history = LoginHistory.Unavailable("The event log of the computer could not be reached.");

        var info = LoginAssessor.Assess(Match, Folder(new DateTime(2025, 3, 2, 9, 40, 0)), history, () => ProfileUse.Unknown);

        Assert.StartsWith(LoginAssessor.UnreadableLogs + " The event log of the computer could not be reached.", info.Details);
        Assert.Equal(1, info.Details.Split("could not be reached").Length - 1);
    }

    [Fact]
    public void Assess_WithoutAFileTime_HasNoLogoutToShow()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("unusable"), () => ProfileUse.Unknown);

        Assert.Null(info.LastLogoutFile);
        Assert.False(info.IsApproximate);
    }

    // ---- What the Details say when the records were used

    // One log was read and holds nothing, the other could not be read: that is not "the logs hold no records".
    [Fact]
    public void Assess_WhenOneLogCouldNotBeRead_AndTheOtherHoldsNoRecords_DoesNotSayThatTheLogsHoldNone()
    {
        var history = new LoginHistory(
            Array.Empty<LoginRecord>(),
            new[]
            {
                new LoginLogResult(LoginLog.System, LoginLogOutcome.Complete, T("2026-05-12T12:00:00Z"), null),
                new LoginLogResult(LoginLog.UserProfileService, LoginLogOutcome.Failed, null, "Reading the event log timed out.")
            },
            false);

        var info = LoginAssessor.Assess(Match, Folder(new DateTime(2025, 3, 2, 9, 40, 0)), history, () => ProfileUse.NotInUse);

        Assert.False(info.RecordsUsed);
        Assert.DoesNotContain(LoginAssessor.NoRecordsAtAll, info.Details);
        Assert.Contains(LoginAssessor.NoRecordsInTheLogThatWasRead, info.Details);
        Assert.Contains("The User Profile Service log could not be used. Reading the event log timed out.", info.Details);
        Assert.EndsWith(LoginAssessor.FromTheProfileFile, info.Details);
    }

    [Fact]
    public void Assess_WhenALogCouldNotBeRead_TheDetailsNameItEvenWhenTheRecordsCannotBeDated()
    {
        // The other log holds a record of someone else, and no log could tell when it begins.
        var history = new LoginHistory(
            new[] { OfSomeoneElse() },
            new[]
            {
                new LoginLogResult(LoginLog.System, LoginLogOutcome.Complete, null, null),
                new LoginLogResult(LoginLog.UserProfileService, LoginLogOutcome.Failed, null, "Reading the event log timed out.")
            },
            true);

        var info = LoginAssessor.Assess(Match, Folder(new DateTime(2025, 3, 2, 9, 40, 0)), history, () => ProfileUse.NotInUse);

        Assert.False(info.RecordsUsed);
        Assert.Contains("cannot be dated", info.Details);
        Assert.Contains("The User Profile Service log could not be used. Reading the event log timed out.", info.Details);
    }

    [Fact]
    public void Assess_Details_SayWhereTheTimesComeFromAndHowFarTheyGoBack()
    {
        var info = LoginAssessor.Assess(Match, Folder(), Scenario("logged out"), NeverAsked);

        Assert.StartsWith(LoginAssessor.FromRecords + ", which go back to " + LocalDate(T("2026-05-12T12:00:00Z")) + ".", info.Details);
    }

    [Fact]
    public void Assess_NamesTheLogThatCouldNotBeRead_WhenTheOtherOneWasUsed()
    {
        var history = new LoginHistory(
            new[] { Login("2026-10-15T16:25:45Z"), Logout("2026-10-15T20:00:00Z") },
            new[]
            {
                new LoginLogResult(LoginLog.System, LoginLogOutcome.Failed, null, "Access denied. The account has no permission."),
                new LoginLogResult(LoginLog.UserProfileService, LoginLogOutcome.Complete, T("2026-05-12T12:00:00Z"), null)
            },
            true);

        var info = LoginAssessor.Assess(Match, Folder(), history, NeverAsked);

        Assert.True(info.RecordsUsed);
        Assert.Contains("System", info.Details);
        Assert.Contains("Access denied. The account has no permission.", info.Details);
        Assert.DoesNotContain("User Profile Service", info.Details);
    }

    [Theory]
    [InlineData(LoginLogOutcome.CutShort)]
    [InlineData(LoginLogOutcome.TimedOut)]
    public void Assess_SaysWhenTheReadWasCutShort(LoginLogOutcome outcome)
    {
        var history = new LoginHistory(
            new[] { Login("2026-10-15T16:25:45Z"), Logout("2026-10-15T20:00:00Z") },
            new[]
            {
                new LoginLogResult(LoginLog.System, outcome, T("2026-10-01T00:00:00Z"), null),
                new LoginLogResult(LoginLog.UserProfileService, LoginLogOutcome.Complete, T("2026-05-12T12:00:00Z"), null)
            },
            true);

        var info = LoginAssessor.Assess(Match, Folder(), history, NeverAsked);

        Assert.Contains(LoginAssessor.CutShort, info.Details);
    }

    [Fact]
    public void Assess_DoesNotSayCutShortForACompleteRead()
        => Assert.DoesNotContain(LoginAssessor.CutShort, LoginAssessor.Assess(Match, Folder(), Scenario("logged out"), NeverAsked).Details);

    // ---- AssessComputer

    [Fact]
    public void AssessComputer_AsksAboutEachFolderAtMostOnce()
    {
        var folders = new[]
        {
            new ProfileFolder("jdoe", null, ActivitySource.NtUserDat, new[] { Jdoe }),
            new ProfileFolder("jdoe.old", null, ActivitySource.NtUserDat, new[] { Jdoe })
        };
        var asked = new List<string>();

        // A rebuilt profile with an open login: the matcher asks about both folders, and the assessment asks again.
        var infos = LoginAssessor.AssessComputer(folders, Scenario("open login"), name =>
        {
            asked.Add(name);
            return name == "jdoe.old" ? ProfileUse.InUse : ProfileUse.NotInUse;
        });

        Assert.Equal(new[] { "jdoe", "jdoe.old" }, asked.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(LoggedInState.Yes, infos["jdoe.old"].LoggedIn);
        Assert.NotEqual(LoggedInState.Yes, infos["jdoe"].LoggedIn);
    }

    [Fact]
    public void AssessComputer_GivesEachLoggedInUserTheirOwnAnswer()
    {
        var folders = new[]
        {
            new ProfileFolder("jdoe", null, ActivitySource.NtUserDat, new[] { Jdoe }),
            new ProfileFolder("asmith", null, ActivitySource.NtUserDat, new[] { Asmith })
        };
        var history = TestHistory.Of(new[] { Login("2026-10-15T16:25:45Z"), OfSomeoneElse() });

        var infos = LoginAssessor.AssessComputer(folders, history, _ => ProfileUse.InUse);

        Assert.Equal(LoggedInState.Yes, infos["jdoe"].LoggedIn);
        Assert.Equal(T("2026-10-15T16:25:45Z"), infos["jdoe"].LoggedInSince);
        Assert.Equal(LoggedInState.Yes, infos["asmith"].LoggedIn);
        Assert.Equal(T("2026-11-12T08:02:00Z"), infos["asmith"].LoggedInSince);
        Assert.Equal(LoggedInState.Yes, infos["ASMITH"].LoggedIn);
    }
}
