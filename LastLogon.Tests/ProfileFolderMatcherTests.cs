using LastLogon.Core;

namespace LastLogon.Tests;

public class ProfileFolderMatcherTests
{
    private const string Jdoe = "S-1-5-21-1-2-3-1001";
    private const string Asmith = "S-1-5-21-1-2-3-1002";
    private const string Tech = "S-1-5-21-1-2-3-1003";
    private const string Group = "S-1-5-21-1-2-3-513";

    // SYSTEM, Administrators and an app container: on the access list of every profile folder
    private static readonly string[] Builtin = { "S-1-5-18", "S-1-5-32-544", "S-1-15-3-65536-1-2-3-4-5-6-7" };

    private static ProfileFolder Folder(string name, DateTime? file = null, params string[] accounts)
        => new(name, file, ActivitySource.NtUserDat, Builtin.Concat(accounts).ToList());

    private static ProfileUse NeverAsked(string folder)
        => throw new InvalidOperationException($"The in-use check was made for {folder}.");

    private static LoginHistory History(params LoginRecord[] records) => TestHistory.Of(records);

    private static IReadOnlyDictionary<string, FolderMatch> Assign(
        ProfileFolder[] folders, LoginHistory? history = null, Func<string, ProfileUse>? use = null)
        => ProfileFolderMatcher.AssignForComputer(folders, history ?? History(), use ?? NeverAsked);

    // ---- AssignForComputer: one account per folder

    [Fact]
    public void AssignForComputer_MatchesAFolderToTheOnlyAccountOnItsList()
    {
        var map = Assign(new[] { Folder("jdoe", null, Jdoe) });

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
        Assert.Equal(FolderMatch.Of(Jdoe), map["JDOE"]);
    }

    [Fact]
    public void AssignForComputer_StrikesAnAccountThatOwnsAFolderOfItsOwn()
    {
        var map = Assign(new[] { Folder("jdoe", null, Jdoe, Tech), Folder("tech", null, Tech) });

        Assert.Equal(Jdoe, map["jdoe"].Sid);
        Assert.Equal(Tech, map["tech"].Sid);
    }

    [Fact]
    public void AssignForComputer_KeepsStrikingUntilNothingChanges()
    {
        var map = Assign(new[]
        {
            Folder("f1", null, Jdoe),
            Folder("f2", null, Jdoe, Asmith),
            Folder("f3", null, Asmith, Tech)
        });

        Assert.Equal(Jdoe, map["f1"].Sid);
        Assert.Equal(Asmith, map["f2"].Sid);
        Assert.Equal(Tech, map["f3"].Sid);
    }

    [Fact]
    public void AssignForComputer_ChoosesTheAccountThatHasRecords()
    {
        var history = History(
            TestHistory.Login(Jdoe, "2026-10-15T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-15T17:00:00Z"),
            TestHistory.Login(Asmith, "2026-10-15T09:00:00Z"), TestHistory.Logout(Asmith, "2026-10-15T18:00:00Z"));

        var map = Assign(new[] { Folder("jdoe", null, Jdoe, Group), Folder("asmith", null, Group, Asmith) }, history);

        Assert.Equal(Jdoe, map["jdoe"].Sid);
        Assert.Equal(Asmith, map["asmith"].Sid);
    }

    [Fact]
    public void AssignForComputer_NeedsNoChoiceWhenNoCandidateHasRecords()
    {
        var map = Assign(new[] { Folder("jdoe", null, Jdoe, Group) });

        Assert.True(map["jdoe"].IsMatch);
    }

    [Fact]
    public void AssignForComputer_DoesNotMatchWhenSeveralCandidatesHaveRecords()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-15T08:00:00Z"), TestHistory.Login(Asmith, "2026-10-15T09:00:00Z"));

        var map = Assign(new[] { Folder("shared", null, Jdoe, Asmith) }, history);

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.Ambiguous), map["shared"]);
    }

    [Fact]
    public void AssignForComputer_DoesNotMatchAnUnreadableListOrOneWithoutAccounts()
    {
        var map = Assign(new[]
        {
            new ProfileFolder("locked", null, ActivitySource.NtUserDat, null),
            Folder("empty")
        });

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AccessListUnreadable), map["locked"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.NoAccount), map["empty"]);
    }

    [Fact]
    public void AssignForComputer_ReportsAFolderWhoseCandidatesAreAllOwnersElsewhere()
    {
        var map = Assign(new[]
        {
            Folder("jdoe", null, Jdoe),
            Folder("asmith", null, Asmith),
            Folder("both", null, Jdoe, Asmith)
        });

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.OnlyOwnersOfOtherFolders), map["both"]);
        Assert.Equal(Jdoe, map["jdoe"].Sid);
        Assert.Equal(Asmith, map["asmith"].Sid);
    }

    [Fact]
    public void AssignForComputer_ComparesSidsWithoutRegardToCase()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-15T08:00:00Z"));

        var map = Assign(new[] { Folder("jdoe", null, Jdoe.ToLowerInvariant(), Group) }, history);

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
    }

    [Fact]
    public void AssignForComputer_IgnoresAListEntryThatIsNotASid()
    {
        var map = Assign(new[] { Folder("jdoe", null, "garbage", Jdoe) });

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
    }

    // ---- AssignForComputer: one account on several folders (a rebuilt profile)

    private static ProfileFolder[] Rebuilt(DateTime? current = null, DateTime? old = null) => new[]
    {
        Folder("jdoe", current, Jdoe),
        Folder("jdoe.old", old, Jdoe)
    };

    [Fact]
    public void AssignForComputer_RebuiltProfile_OpenLogin_ChoosesTheFolderInUse()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));
        var use = new Dictionary<string, ProfileUse> { ["jdoe.old"] = ProfileUse.InUse, ["jdoe"] = ProfileUse.NotInUse };

        var map = Assign(Rebuilt(), history, name => use[name]);

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe.old"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe"]);
    }

    [Theory]
    [InlineData(ProfileUse.NotInUse, ProfileUse.NotInUse)]
    [InlineData(ProfileUse.Unknown, ProfileUse.Unknown)]
    [InlineData(ProfileUse.NotInUse, ProfileUse.Unknown)]
    [InlineData(ProfileUse.InUse, ProfileUse.InUse)]
    public void AssignForComputer_RebuiltProfile_OpenLogin_MatchesNeitherWhenNoneOrBothAreInUse(ProfileUse current, ProfileUse old)
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));
        var use = new Dictionary<string, ProfileUse> { ["jdoe"] = current, ["jdoe.old"] = old };

        var map = Assign(Rebuilt(), history, name => use[name]);

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe.old"]);
    }

    [Fact]
    public void AssignForComputer_RebuiltProfile_LoggedOut_ChoosesTheFolderWrittenAtTheLogout()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));
        var folders = Rebuilt(TestHistory.T("2026-10-14T20:46:30Z").LocalDateTime, new DateTime(2026, 9, 1));

        var map = Assign(folders, history);

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe.old"]);
    }

    [Theory]
    [InlineData("2026-10-14T20:46:30Z", "2026-10-14T20:47:00Z")] // both written at the logout
    [InlineData("2026-09-01T10:00:00Z", "2026-09-02T10:00:00Z")] // neither written at the logout
    [InlineData("2026-10-14T20:49:09Z", "2026-10-14T20:43:07Z")] // both just outside the two minutes
    public void AssignForComputer_RebuiltProfile_LoggedOut_MatchesNeitherWhenTheFileTimesDoNotSingleOneOut(string current, string old)
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));
        var folders = Rebuilt(TestHistory.T(current).LocalDateTime, TestHistory.T(old).LocalDateTime);

        var map = Assign(folders, history);

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe.old"]);
    }

    [Fact]
    public void AssignForComputer_RebuiltProfile_LoggedOut_IgnoresATimeThatIsOnlyThatOfTheFolder()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));
        var folders = new[]
        {
            Folder("jdoe", TestHistory.T("2026-10-14T20:46:30Z").LocalDateTime, Jdoe),
            new ProfileFolder("jdoe.old", TestHistory.T("2026-10-14T20:46:10Z").LocalDateTime, ActivitySource.Folder, Builtin.Append(Jdoe).ToList())
        };

        var map = Assign(folders, history);

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe.old"]);
    }

    [Fact]
    public void AssignForComputer_RebuiltProfile_LoggedOut_UsesATolerance_OfAboutTwoMinutes()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:00Z"));
        var folders = Rebuilt(TestHistory.T("2026-10-14T20:47:30Z").LocalDateTime, TestHistory.T("2026-10-14T20:48:30Z").LocalDateTime);

        var map = Assign(folders, history);

        // 90 seconds after the logout counts, 150 seconds does not
        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AnotherProfileOfTheAccount), map["jdoe.old"]);
    }

    [Fact]
    public void AssignForComputer_RebuiltProfile_WithoutRecords_KeepsTheAccountOnBothFolders()
    {
        var history = History(TestHistory.Login(Asmith, "2026-10-15T09:00:00Z"));

        var map = Assign(Rebuilt(), history);

        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe"]);
        Assert.Equal(FolderMatch.Of(Jdoe), map["jdoe.old"]);
    }

    // ---- MatchForUser

    private static string[] List(params string[] accounts) => Builtin.Concat(accounts).ToArray();

    [Fact]
    public void MatchForUser_UsesTheResolvedSidWhenItIsOnTheList()
    {
        var match = ProfileFolderMatcher.MatchForUser(List(Jdoe, Tech), Jdoe);

        Assert.Equal(FolderMatch.Of(Jdoe), match.Match);
        Assert.False(match.NeedsRecords);
    }

    [Fact]
    public void MatchForUser_ComparesSidsWithoutRegardToCase()
    {
        var match = ProfileFolderMatcher.MatchForUser(List(Jdoe.ToLowerInvariant(), Tech), Jdoe);

        Assert.Equal(FolderMatch.Of(Jdoe), match.Match);
    }

    [Fact]
    public void MatchForUser_RefusesAFolderThatDoesNotListTheResolvedSid()
    {
        var match = ProfileFolderMatcher.MatchForUser(List(Asmith), Jdoe);

        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.OtherAccountWithTheSameName), match.Match);
        Assert.False(match.NeedsRecords);
    }

    [Fact]
    public void MatchForUser_TakesTheOnlyAccountWhenTheNameCannotBeResolved()
    {
        Assert.Equal(FolderMatch.Of(Jdoe), ProfileFolderMatcher.MatchForUser(List(Jdoe), null).Match);
        Assert.Equal(FolderMatch.Of(Jdoe), ProfileFolderMatcher.MatchForUser(List(Jdoe), "not a sid").Match);
    }

    [Fact]
    public void MatchForUser_AsksForRecordsWhenSeveralAccountsRemain()
    {
        var match = ProfileFolderMatcher.MatchForUser(List(Jdoe, Group), null);

        Assert.True(match.NeedsRecords);
        Assert.Equal(new[] { Jdoe, Group }, match.Ambiguous);
        Assert.False(match.Match.IsMatch);
    }

    [Fact]
    public void MatchForUser_RefusesAnUnreadableListAndAFolderWithoutAccounts()
    {
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.AccessListUnreadable), ProfileFolderMatcher.MatchForUser(null, Jdoe).Match);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.NoAccount), ProfileFolderMatcher.MatchForUser(List(), Jdoe).Match);
        Assert.Equal(FolderMatch.None(ProfileFolderMatcher.NoAccount), ProfileFolderMatcher.MatchForUser(List("garbage"), null).Match);
    }

    // ---- ChooseByRecords

    [Fact]
    public void ChooseByRecords_PicksTheCandidateWithRecords()
    {
        var history = History(TestHistory.Login(Asmith, "2026-10-15T09:00:00Z"));

        Assert.Equal(FolderMatch.Of(Asmith), ProfileFolderMatcher.ChooseByRecords(new[] { Jdoe, Asmith }, history));
    }

    [Fact]
    public void ChooseByRecords_TakesTheFirstWhenNoneHasRecords()
    {
        var history = History(TestHistory.Login(Tech, "2026-10-15T09:00:00Z"));

        Assert.Equal(FolderMatch.Of(Jdoe), ProfileFolderMatcher.ChooseByRecords(new[] { Jdoe, Asmith }, history));
    }

    [Fact]
    public void ChooseByRecords_RefusesWhenSeveralHaveRecords()
    {
        var history = History(TestHistory.Login(Jdoe, "2026-10-15T08:00:00Z"), TestHistory.Login(Asmith, "2026-10-15T09:00:00Z"));

        Assert.Equal(
            FolderMatch.None(ProfileFolderMatcher.Ambiguous),
            ProfileFolderMatcher.ChooseByRecords(new[] { Jdoe, Asmith }, history));
    }
}
