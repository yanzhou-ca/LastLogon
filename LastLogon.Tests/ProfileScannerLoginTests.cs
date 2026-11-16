using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using LastLogon.Core;
using ActivitySource = LastLogon.Core.ActivitySource;

namespace LastLogon.Tests;

/// <summary>A source of login histories that answers from a delegate and remembers what it was asked.</summary>
internal sealed class FakeHistory : ILoginHistorySource
{
    public Func<string, string?, LoginHistory> Answer { get; set; } = (_, _) => TestHistory.Of(Array.Empty<LoginRecord>());

    public ConcurrentQueue<(string Computer, string? Sid)> Reads { get; } = new();

    /// <summary>How long a read takes. It waits on the token, so that a cancel ends it, unless <see cref="IgnoreCancel"/> is set.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Stands in for a call that cannot be interrupted: the read sleeps through a cancel.</summary>
    public bool IgnoreCancel { get; set; }

    public LoginHistory Read(string computer, string? sid, TimeSpan limit, CancellationToken cancellationToken)
    {
        Reads.Enqueue((computer, sid));
        if (Delay > TimeSpan.Zero)
        {
            if (IgnoreCancel)
            {
                Thread.Sleep(Delay);
            }
            else if (cancellationToken.WaitHandle.WaitOne(Delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return Answer(computer, sid);
    }
}

public class ProfileScannerLoginTests
{
    private const string Jdoe = "S-1-5-21-1-2-3-1001";
    private const string Asmith = "S-1-5-21-1-2-3-1002";
    private const string Carol = "S-1-5-21-1-2-3-1004";
    private static readonly string[] Builtin = { "S-1-5-18", "S-1-5-32-544", "S-1-15-3-65536-1-2-3-4-5-6-7" };

    private static DateTime At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture).LocalDateTime;

    /// <summary>A computer pc1 with these profiles: the folder, the SID on its access list and the time of its NTUSER.DAT.</summary>
    private static FakeFs Pc(params (string Folder, string Sid, DateTime File)[] profiles)
    {
        var fs = new FakeFs();
        fs.Folders["pc1"] = profiles.Select(profile => profile.Folder).ToList();
        foreach (var (folder, sid, file) in profiles)
        {
            fs.NtUser[$"pc1|{folder}"] = file;
            fs.Acl[$"pc1|{folder}"] = Builtin.Append(sid).ToArray();
        }

        return fs;
    }

    private static FakeHistory HistoryOf(params LoginRecord[] records)
        => new() { Answer = (_, _) => TestHistory.Of(records) };

    private static ProfileScanner Scanner(FakeFs fs, FakeHistory? history, TimeSpan? perPc = null, TimeSpan? historyLimit = null, int parallel = 4)
        => new(fs, parallel, perPc, loginHistory: history, loginHistoryTimeout: historyLimit);

    // ---- By computer

    [Fact]
    public async Task ScanComputer_FillsTheLoginFacts()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")), ("asmith", Asmith, At("2026-11-12T21:31:00Z")));
        fs.Use["pc1|jdoe"] = ProfileUse.InUse;
        var history = HistoryOf(
            TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"), TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"),
            TestHistory.Login(Asmith, "2026-11-12T12:02:00Z"), TestHistory.Logout(Asmith, "2026-11-12T21:31:00Z"));

        var rows = await Scanner(fs, history).ScanComputerAsync("pc1", default);

        var jdoe = rows[0];
        var asmith = rows[1];
        Assert.Equal("jdoe", jdoe.Username);
        Assert.Equal(LoggedInState.Yes, jdoe.Login!.LoggedIn);
        Assert.Equal(TestHistory.T("2026-10-15T16:25:45Z"), jdoe.Login.LoggedInSince);
        Assert.Equal(TestHistory.T("2026-10-14T20:46:08Z"), jdoe.Login.LastLogoutRecord);
        Assert.Equal(ActivitySource.LoginRecords, jdoe.Source);
        Assert.Equal("OK", jdoe.ResultText);
        Assert.Equal("asmith", asmith.Username);
        Assert.Equal(LoggedInState.No, asmith.Login!.LoggedIn);
        // the user who logged out was not asked about, and every user's records were read once
        Assert.Equal(new[] { "pc1|jdoe" }, fs.UseChecks.ToArray());
        Assert.Equal(new[] { ("pc1", (string?)null) }, history.Reads.ToArray());
    }

    [Fact]
    public async Task ScanComputer_OrdersLoggedInUsersFirst()
    {
        var fs = Pc(("bob", Asmith, At("2026-11-12T21:31:00Z")), ("alice", Jdoe, At("2026-01-01T10:00:00Z")));
        fs.Use["pc1|alice"] = ProfileUse.InUse;
        var history = HistoryOf(
            TestHistory.Login(Asmith, "2026-11-12T12:02:00Z"), TestHistory.Logout(Asmith, "2026-11-12T21:31:00Z"),
            TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));

        var rows = await Scanner(fs, history).ScanComputerAsync("pc1", default);

        Assert.Equal(new[] { "alice", "bob" }, rows.Select(row => row.Username));
    }

    [Fact]
    public async Task ScanComputer_SeveralUsersLoggedInAtOnce_EachGetsTheirOwnState()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")), ("asmith", Asmith, At("2026-10-13T20:46:08Z")));
        fs.Use["pc1|jdoe"] = ProfileUse.InUse;
        fs.Use["pc1|asmith"] = ProfileUse.InUse;
        var history = HistoryOf(
            TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z", session: 2),
            TestHistory.Login(Asmith, "2026-10-20T09:00:00Z", session: 3));

        var rows = await Scanner(fs, history).ScanComputerAsync("pc1", default);

        Assert.All(rows, row => Assert.Equal(LoggedInState.Yes, row.Login!.LoggedIn));
        Assert.Equal(TestHistory.T("2026-10-15T16:25:45Z"), rows.Single(row => row.Username == "jdoe").Login!.LoggedInSince);
        Assert.Equal(TestHistory.T("2026-10-20T09:00:00Z"), rows.Single(row => row.Username == "asmith").Login!.LoggedInSince);
    }

    [Fact]
    public async Task ScanComputer_KeepsTheExistingMessage()
    {
        var fs = Pc(("carol", Carol, At("2026-11-01T10:00:00Z")));
        fs.NtUser.Remove("pc1|carol");
        var history = HistoryOf(TestHistory.Login(Carol, "2026-11-02T08:00:00Z"), TestHistory.Logout(Carol, "2026-11-02T17:00:00Z"));

        var row = Assert.Single(await Scanner(fs, history).ScanComputerAsync("pc1", default));

        Assert.StartsWith("NTUSER.DAT not found; using folder time " + LoginAssessor.FromRecords, row.Message);
    }

    [Fact]
    public async Task ScanComputer_WithoutAHistorySource_BehavesAsBefore()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));

        var row = Assert.Single(await Scanner(fs, null).ScanComputerAsync("pc1", default));

        Assert.Null(row.Login);
        Assert.Equal(ActivitySource.NtUserDat, row.Source);
        Assert.Empty(fs.UseChecks);
        Assert.Equal(0, fs.AclPeak);
    }

    [Fact]
    public async Task ScanComputer_ARowThatFailedAtTheShare_DoesNotReadTheRecords()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));
        fs.Fail = _ => new DirectoryNotFoundException();
        var history = HistoryOf();

        var row = Assert.Single(await Scanner(fs, history).ScanComputerAsync("pc1", default));

        Assert.Equal(ScanStatus.Offline, row.Status);
        Assert.Empty(history.Reads);
    }

    [Fact]
    public async Task ScanComputer_WhenTheRecordsCannotBeRead_RowsStayOkAndApproximate()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")), ("asmith", Asmith, At("2026-11-12T21:31:00Z")));
        var history = new FakeHistory { Answer = (_, _) => LoginHistory.Unavailable("The event log of the computer could not be reached.") };

        var rows = await Scanner(fs, history).ScanComputerAsync("pc1", default);

        Assert.All(rows, row =>
        {
            Assert.Equal(ScanStatus.Ok, row.Status);
            Assert.Equal("Approximate", row.ResultText);
            Assert.Contains("The event log of the computer could not be reached.", row.Message);
            Assert.NotNull(row.Login);
            Assert.False(row.Login!.RecordsUsed);
        });
    }

    [Fact]
    public async Task ScanComputer_WhenTheAccessListCannotBeRead_ThatFolderFallsBack()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")), ("asmith", Asmith, At("2026-11-12T21:31:00Z")));
        fs.FailAcl = (_, folder) => folder == "jdoe" ? new UnauthorizedAccessException("denied") : null;
        var history = HistoryOf(
            TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"),
            TestHistory.Login(Asmith, "2026-11-12T12:02:00Z"), TestHistory.Logout(Asmith, "2026-11-12T21:31:00Z"));

        var rows = await Scanner(fs, history).ScanComputerAsync("pc1", default);

        var jdoe = rows.Single(row => row.Username == "jdoe");
        var asmith = rows.Single(row => row.Username == "asmith");
        Assert.Equal("Approximate", jdoe.ResultText);
        Assert.Contains(ProfileFolderMatcher.AccessListUnreadable, jdoe.Message);
        Assert.Equal("OK", asmith.ResultText);
        Assert.True(asmith.Login!.RecordsUsed);
    }

    [Fact]
    public async Task ScanComputer_WhenTheHistoryReadHangs_RowsFallBackAfterTheLimit()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));
        var history = new FakeHistory { Delay = TimeSpan.FromSeconds(5), IgnoreCancel = true };

        var stopwatch = Stopwatch.StartNew();
        var rows = await Scanner(fs, history, historyLimit: TimeSpan.FromMilliseconds(100)).ScanComputerAsync("pc1", default);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        var row = Assert.Single(rows);
        Assert.Equal(ScanStatus.Ok, row.Status);
        Assert.Equal("Approximate", row.ResultText);
        Assert.Contains("timed out", row.Message);
    }

    // Review focus 1: the new share work hangs on a computer whose profiles could be read
    [Fact]
    public async Task ScanComputer_WhenTheAccessListsHang_RowsAreStillOkAndApproximate()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));
        fs.AclDelay = TimeSpan.FromSeconds(5);

        var rows = await Scanner(fs, HistoryOf(), perPc: TimeSpan.FromMilliseconds(100)).ScanComputerAsync("pc1", default);

        var row = Assert.Single(rows);
        Assert.Equal(ScanStatus.Ok, row.Status);
        Assert.Equal("Approximate", row.ResultText);
    }

    [Fact]
    public async Task ScanComputer_WhenTheInUseChecksHang_RowsAreStillOkAndStandOnTheRecords()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));
        fs.UseDelay = TimeSpan.FromSeconds(5);
        var history = HistoryOf(TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));

        var rows = await Scanner(fs, history, perPc: TimeSpan.FromMilliseconds(300)).ScanComputerAsync("pc1", default);

        var row = Assert.Single(rows);
        Assert.Equal(ScanStatus.Ok, row.Status);
        // the open login stands on the word of the records: the check could not tell
        Assert.Equal(LoggedInState.Yes, row.Login!.LoggedIn);
    }

    [Fact]
    public async Task ScanComputers_KeepsTheSlotWhileAnAbandonedAccessListReadStillRuns()
    {
        var fs = new FakeFs();
        foreach (var computer in new[] { "pc1", "pc2" })
        {
            fs.Folders[computer] = new() { "jdoe" };
            fs.NtUser[$"{computer}|jdoe"] = At("2026-10-14T20:46:08Z");
            fs.Acl[$"{computer}|jdoe"] = Builtin.Append(Jdoe).ToArray();
        }

        fs.AclDelay = TimeSpan.FromMilliseconds(400);
        var scanner = Scanner(fs, HistoryOf(), perPc: TimeSpan.FromMilliseconds(100), parallel: 1);

        var rows = new List<ProfileResult>();
        await foreach (var row in scanner.ScanComputersAsync(new[] { "pc1", "pc2" }, default))
        {
            rows.Add(row);
        }

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, fs.AclPeak);
    }

    // ---- By user

    private static FakeFs PcsWithJdoe(params string[] computers)
    {
        var fs = new FakeFs();
        foreach (var computer in computers)
        {
            fs.Folders[computer] = new() { "jdoe" };
            fs.NtUser[$"{computer}|jdoe"] = At("2026-10-14T20:46:08Z");
            fs.Acl[$"{computer}|jdoe"] = Builtin.Append(Jdoe).ToArray();
        }

        return fs;
    }

    private static ProfileScanner UserScanner(
        FakeFs fs, FakeHistory? history, Func<string, CancellationToken, Task<string?>>? resolver)
        => new(fs, 4, loginHistory: history, resolveUserSid: resolver);

    private static Func<string, CancellationToken, Task<string?>> Resolves(string? sid)
        => (_, _) => Task.FromResult(sid);

    private static async Task<List<ProfileResult>> Find(
        ProfileScanner scanner, string[] computers, string? typedName = null, string username = "jdoe")
    {
        var rows = new List<ProfileResult>();
        await foreach (var row in scanner.FindUserAsync(username, computers, default, typedName))
        {
            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public async Task FindUser_FillsTheLoginFactsOfTheUsersFolder()
    {
        var fs = PcsWithJdoe("pc1");
        fs.Use["pc1|jdoe"] = ProfileUse.InUse;
        var history = HistoryOf(
            TestHistory.Login(Asmith, "2026-11-12T08:00:00Z"),
            TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"), TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));

        var row = Assert.Single(await Find(UserScanner(fs, history, Resolves(Jdoe)), new[] { "pc1" }));

        Assert.Equal(new[] { ("pc1", (string?)Jdoe) }, history.Reads.ToArray());
        Assert.Equal(LoggedInState.Yes, row.Login!.LoggedIn);
        Assert.Equal(TestHistory.T("2026-10-15T16:25:45Z"), row.Login.LoggedInSince);
        Assert.Equal(ActivitySource.LoginRecords, row.Source);
        Assert.Equal("OK", row.ResultText);
    }

    [Fact]
    public async Task FindUser_ResolvesTheNameOncePerScan()
    {
        var fs = PcsWithJdoe("pc1", "pc2", "pc3");
        var asked = new ConcurrentQueue<string>();
        Func<string, CancellationToken, Task<string?>> resolver = (name, _) =>
        {
            asked.Enqueue(name);
            return Task.FromResult<string?>(Jdoe);
        };

        var rows = await Find(UserScanner(fs, HistoryOf(), resolver), new[] { "pc1", "pc2", "pc3" }, typedName: @"CORP\jdoe");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { @"CORP\jdoe" }, asked.ToArray());
    }

    [Fact]
    public async Task FindUser_AsksTheResolverForTheUsernameWhenNoTypedNameIsGiven()
    {
        var asked = new ConcurrentQueue<string>();
        Func<string, CancellationToken, Task<string?>> resolver = (name, _) =>
        {
            asked.Enqueue(name);
            return Task.FromResult<string?>(null);
        };

        await Find(UserScanner(PcsWithJdoe("pc1"), HistoryOf(), resolver), new[] { "pc1" });

        Assert.Equal(new[] { "jdoe" }, asked.ToArray());
    }

    [Fact]
    public async Task FindUser_WhenTheFolderDoesNotListTheResolvedSid_RowFallsBack()
    {
        var fs = PcsWithJdoe("pc1");
        fs.Acl["pc1|jdoe"] = Builtin.Append(Asmith).ToArray();
        var history = HistoryOf(TestHistory.Login(Jdoe, "2026-10-15T16:25:45Z"));

        var row = Assert.Single(await Find(UserScanner(fs, history, Resolves(Jdoe)), new[] { "pc1" }));

        Assert.Equal(ScanStatus.Ok, row.Status);
        Assert.Equal("Approximate", row.ResultText);
        Assert.Contains(ProfileFolderMatcher.OtherAccountWithTheSameName, row.Message);
        Assert.Empty(history.Reads);
    }

    [Fact]
    public async Task FindUser_WhenTheNameCannotBeResolved_TakesTheOnlyAccount()
    {
        var history = HistoryOf(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));

        var row = Assert.Single(await Find(UserScanner(PcsWithJdoe("pc1"), history, Resolves(null)), new[] { "pc1" }));

        Assert.Equal(new[] { ("pc1", (string?)Jdoe) }, history.Reads.ToArray());
        Assert.True(row.Login!.RecordsUsed);
        Assert.Equal("OK", row.ResultText);
    }

    [Fact]
    public async Task FindUser_WhenTheNameCannotBeResolved_AndSeveralAccountsRemain_ReadsEveryUsersRecords()
    {
        var fs = PcsWithJdoe("pc1");
        fs.Acl["pc1|jdoe"] = Builtin.Append(Jdoe).Append(Asmith).ToArray();
        var history = HistoryOf(
            TestHistory.Login(Asmith, "2026-11-12T08:00:00Z"), TestHistory.Logout(Asmith, "2026-11-12T17:00:00Z"));

        var row = Assert.Single(await Find(UserScanner(fs, history, Resolves(null)), new[] { "pc1" }));

        Assert.Equal(new[] { ("pc1", (string?)null) }, history.Reads.ToArray());
        Assert.True(row.Login!.RecordsUsed);
        Assert.Equal(TestHistory.T("2026-11-12T08:00:00Z"), row.Login.LastLogin);
    }

    [Fact]
    public async Task FindUser_ForAUserWithoutRecords_SaysBeforeTheDateTheRecordsBegin()
    {
        var history = HistoryOf(TestHistory.Login(Asmith, "2026-11-12T08:00:00Z"));

        var row = Assert.Single(await Find(UserScanner(PcsWithJdoe("pc1"), history, Resolves(Jdoe)), new[] { "pc1" }));

        Assert.StartsWith("Before ", row.LastLoginText);
        Assert.Equal("OK", row.ResultText);
    }

    [Fact]
    public async Task FindUser_WhenTheLogsHoldNoRecordAtAll_RestsOnTheProfileFile()
    {
        var history = new FakeHistory { Answer = (_, _) => TestHistory.Of(Array.Empty<LoginRecord>(), anyone: false) };

        var row = Assert.Single(await Find(UserScanner(PcsWithJdoe("pc1"), history, Resolves(Jdoe)), new[] { "pc1" }));

        Assert.Equal("Approximate", row.ResultText);
        Assert.Contains(LoginAssessor.NoRecordsAtAll, row.Message);
    }

    [Fact]
    public async Task FindUser_ResolverTimeoutOrFailure_IsTreatedAsUnresolved()
    {
        Func<string, CancellationToken, Task<string?>> failing = (_, _) => throw new InvalidOperationException("no domain controller");
        var history = HistoryOf(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));

        var row = Assert.Single(await Find(UserScanner(PcsWithJdoe("pc1"), history, failing), new[] { "pc1" }));

        Assert.Equal(ScanStatus.Ok, row.Status);
        Assert.True(row.Login!.RecordsUsed);
    }

    [Fact]
    public async Task FindUser_AResolverThatNeverAnswers_DoesNotHoldUpTheScan()
    {
        Func<string, CancellationToken, Task<string?>> never = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => (string?)null, CancellationToken.None);
        var history = HistoryOf(TestHistory.Login(Jdoe, "2026-10-14T08:00:00Z"), TestHistory.Logout(Jdoe, "2026-10-14T20:46:08Z"));
        var scanner = new ProfileScanner(PcsWithJdoe("pc1"), 4, TimeSpan.FromMilliseconds(200), loginHistory: history, resolveUserSid: never);

        var stopwatch = Stopwatch.StartNew();
        var row = Assert.Single(await Find(scanner, new[] { "pc1" }));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal(ScanStatus.Ok, row.Status);
    }

    [Fact]
    public async Task FindUser_AComputerWithoutTheProfile_StillHasItsNoProfileRow()
    {
        var fs = new FakeFs();
        fs.Folders["pc1"] = new() { "asmith" };
        var history = HistoryOf();

        var row = Assert.Single(await Find(UserScanner(fs, history, Resolves(Jdoe)), new[] { "pc1" }));

        Assert.Equal(ScanStatus.NoProfile, row.Status);
        Assert.Null(row.Login);
        Assert.Empty(history.Reads);
    }

    [Fact]
    public async Task FindUser_WithoutAHistorySource_BehavesAsBefore()
    {
        Func<string, CancellationToken, Task<string?>> mustNotBeCalled = (_, _) => throw new InvalidOperationException("resolved");

        var row = Assert.Single(await Find(UserScanner(PcsWithJdoe("pc1"), null, mustNotBeCalled), new[] { "pc1" }));

        Assert.Null(row.Login);
        Assert.Equal(ActivitySource.NtUserDat, row.Source);
    }

    // Review focus 4: Esc while the records are being read
    [Fact]
    public async Task ScanComputers_CancelledWhileTheRecordsAreRead_EndsPromptlyWithoutRows()
    {
        var fs = Pc(("jdoe", Jdoe, At("2026-10-14T20:46:08Z")));
        var history = new FakeHistory { Delay = TimeSpan.FromSeconds(2) };
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        var rows = new List<ProfileResult>();
        await foreach (var row in Scanner(fs, history).ScanComputersAsync(new[] { "pc1" }, cts.Token))
        {
            rows.Add(row);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5));
        Assert.Empty(rows);
    }
}
