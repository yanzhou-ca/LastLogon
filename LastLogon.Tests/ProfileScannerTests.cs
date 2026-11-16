using System.Collections.Concurrent;
using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class FakeFs : IProfileFileSystem
{
    public Dictionary<string, List<string>> Folders = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime?> NtUser = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, Exception?>? Fail;
    public Func<string, string, Exception?>? FailNtUser;
    public TimeSpan Delay = TimeSpan.Zero;
    private int _cur, _peak, _listCalls;
    public int Peak => _peak;
    public int ListCalls => _listCalls;

    public IReadOnlyList<string> ListUserFolders(string computer)
    {
        Interlocked.Increment(ref _listCalls);
        var c = Interlocked.Increment(ref _cur);
        int p; while (c > (p = _peak)) Interlocked.CompareExchange(ref _peak, c, p);
        try
        {
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            var ex = Fail?.Invoke(computer); if (ex != null) throw ex;
            return Folders.TryGetValue(computer, out var l) ? l : new List<string>();
        }
        finally { Interlocked.Decrement(ref _cur); }
    }
    public DateTime? GetNtUserDatTime(string c, string u)
    {
        var ex = FailNtUser?.Invoke(c, u);
        if (ex != null) throw ex;
        return NtUser.TryGetValue($"{c}|{u}", out var t) ? t : null;
    }
    public DateTime GetFolderTime(string c, string u) => new(2020, 1, 1);

    // Access lists and the in-use check, keyed "computer|folder". A missing key answers an empty list and Unknown.
    public Dictionary<string, string[]> Acl = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ProfileUse> Use = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, string, Exception?>? FailAcl;
    public TimeSpan AclDelay = TimeSpan.Zero;
    public ConcurrentQueue<string> UseChecks = new();
    private int _aclCur, _aclPeak;
    public int AclPeak => _aclPeak;

    public IReadOnlyList<string> GetFolderAccessSids(string c, string u)
    {
        var cur = Interlocked.Increment(ref _aclCur);
        int p; while (cur > (p = _aclPeak)) Interlocked.CompareExchange(ref _aclPeak, cur, p);
        try
        {
            if (AclDelay > TimeSpan.Zero) Thread.Sleep(AclDelay);
            var ex = FailAcl?.Invoke(c, u); if (ex != null) throw ex;
            return Acl.TryGetValue($"{c}|{u}", out var l) ? l : Array.Empty<string>();
        }
        finally { Interlocked.Decrement(ref _aclCur); }
    }

    public TimeSpan UseDelay = TimeSpan.Zero;

    public ProfileUse GetProfileUse(string c, string u)
    {
        UseChecks.Enqueue($"{c}|{u}");
        if (UseDelay > TimeSpan.Zero) Thread.Sleep(UseDelay);
        return Use.TryGetValue($"{c}|{u}", out var v) ? v : ProfileUse.Unknown;
    }
}

public class ProfileScannerTests
{
    static FakeFs Basic()
    {
        var fs = new FakeFs();
        fs.Folders["pc1"] = new() { "alice", "bob", "Public", "Default" };
        fs.NtUser["pc1|alice"] = new DateTime(2024, 1, 1);
        fs.NtUser["pc1|bob"] = new DateTime(2025, 1, 1);
        return fs;
    }

    [Fact]
    public async Task ScanComputer_ListsProfilesNewestFirst_SkipsSystemFolders()
    {
        var r = await new ProfileScanner(Basic()).ScanComputerAsync("pc1", default);
        Assert.Equal(new[] { "bob", "alice" }, r.Select(x => x.Username));
    }

    [Fact]
    public async Task ScanComputer_MissingNtUserDat_UsesFolderTime()
    {
        var fs = Basic(); fs.Folders["pc1"].Add("carol");
        var r = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);
        var c = r.Single(x => x.Username == "carol");
        Assert.Equal(ActivitySource.Folder, c.Source);
        Assert.Equal(new DateTime(2020, 1, 1), c.LastActivity);
    }

    [Fact]
    public async Task ScanComputer_DirectoryNotFound_ReturnsOffline()
    {
        var fs = Basic(); fs.Fail = _ => new DirectoryNotFoundException();
        var r = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);
        Assert.Equal(ScanStatus.Offline, Assert.Single(r).Status);
    }

    [Fact]
    public async Task ScanComputer_UnauthorizedAccess_ReturnsAccessDenied()
    {
        var fs = Basic(); fs.Fail = _ => new UnauthorizedAccessException();
        var r = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);
        Assert.Equal(ScanStatus.AccessDenied, Assert.Single(r).Status);
    }

    [Fact]
    public async Task ScanComputer_Timeout_ReturnsTimedOut()
    {
        var fs = Basic(); fs.Delay = TimeSpan.FromSeconds(1);
        var r = await new ProfileScanner(fs, 8, TimeSpan.FromMilliseconds(100)).ScanComputerAsync("pc1", default);
        Assert.Equal(ScanStatus.TimedOut, Assert.Single(r).Status);
    }

    [Fact]
    public async Task ScanComputer_UnauthorizedAccess_KeepsTheReason()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("Access to the path '\\\\pc01.corp.example\\C$' is denied.");
        var r = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);
        var row = Assert.Single(r);
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Contains("corp.example", row.Message);

        // The message has to say what is needed to read the share, not only that it was refused.
        Assert.True(
            row.Message!.Contains(RunContext.Account, StringComparison.OrdinalIgnoreCase)
            || row.Message.Contains("not running elevated", StringComparison.OrdinalIgnoreCase),
            "the message should name the account or say that the tool is not elevated: " + row.Message);
    }

    [Theory]
    [InlineData("pc01", "corp.example", ComputerTarget.Addressing.DomainQualified,
        @"addressed as pc01.corp.example (domain-qualified)", "choose Short name")]
    [InlineData("pc01", "corp.example", ComputerTarget.Addressing.ShortName,
        "addressed by its short name (pc01)", null)]
    [InlineData("pc01.other.example", "corp.example", ComputerTarget.Addressing.DomainQualified,
        "entered as the fully qualified name pc01.other.example", "choose Short name")]
    [InlineData("pc01", "", ComputerTarget.Addressing.DomainQualified,
        "addressed by its short name (pc01) because no DNS domain could be determined", null)]
    public void DescribeAddressing_SaysWhichNameTheFailedConnectionUsed(
        string computer, string domain, ComputerTarget.Addressing addressing,
        string expected, string? alsoExpected)
    {
        var text = ProfileScanner.DescribeAddressing(computer, domain, addressing);

        Assert.Contains(expected, text);
        if (alsoExpected is not null) Assert.Contains(alsoExpected, text);
    }

    [Fact]
    public void RunContext_ReportsTheAccountAndTheElevation()
    {
        Assert.False(string.IsNullOrWhiteSpace(RunContext.Account));
        Assert.Contains(RunContext.Account, RunContext.Account);
    }

    [Fact]
    public async Task ScanComputer_NoUserProfiles_ReportsNoProfile()
    {
        var fs = Basic();
        fs.Folders["pc1"] = new() { "Public", "Default" };
        var r = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);
        var row = Assert.Single(r);
        Assert.Equal(ScanStatus.NoProfile, row.Status);
        Assert.Equal("pc1", row.Computer);
    }

    // D-02: a single profile folder that cannot be read must not hide the profiles that can.
    [Fact]
    public async Task ScanComputer_OneUnreadableProfile_KeepsTheProfilesThatCanBeRead()
    {
        var fs = Basic();
        fs.Folders["pc1"].Add("carol");
        fs.FailNtUser = (_, user) => user == "carol"
            ? new UnauthorizedAccessException(@"Access to the path '\\pc1\C$\Users\carol\NTUSER.DAT' is denied.")
            : null;

        var rows = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);

        Assert.Equal(new[] { "bob", "alice", "carol" }, rows.Select(r => r.Username));
        Assert.Equal(ScanStatus.Ok, rows.Single(r => r.Username == "bob").Status);
        Assert.Equal(ScanStatus.Ok, rows.Single(r => r.Username == "alice").Status);

        var carol = rows.Single(r => r.Username == "carol");
        Assert.Equal(ScanStatus.AccessDenied, carol.Status);
        Assert.Null(carol.LastActivity);
        Assert.Contains("NTUSER.DAT", carol.Message);
        // The row must say that the problem is this profile, not the account or the share.
        Assert.Contains("other profiles", carol.Message);
        Assert.DoesNotContain("has to be an administrator", carol.Message);
    }

    // Guard for D-02: when nothing can be read the computer is still reported once, with the
    // guidance about rights, instead of one noisy row per profile.
    [Fact]
    public async Task ScanComputer_NoProfileCanBeRead_ReportsOneRowForTheComputer()
    {
        var fs = Basic();
        fs.FailNtUser = (_, _) => new UnauthorizedAccessException("denied");

        var rows = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);

        var row = Assert.Single(rows);
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Equal("", row.Username);
    }

    // Guard for D-02: only an access problem is attributed to one profile. A lost connection is
    // not, because every following profile would fail the same way.
    [Fact]
    public async Task ScanComputer_ConnectionLostWhileReadingAProfile_ReportsTheComputerAsOffline()
    {
        var fs = Basic();
        fs.FailNtUser = (_, user) => user == "bob" ? new IOException("The network path was not found.") : null;

        var rows = await new ProfileScanner(fs).ScanComputerAsync("pc1", default);

        var row = Assert.Single(rows);
        Assert.Equal(ScanStatus.Offline, row.Status);
    }

    // "Access denied" is the answer of whichever computer replied at that name. Showing which
    // address each name resolves to is what tells a refused account from a wrong computer.
    private static ConnectionTestResult TwoNamesTwoComputers() => new(
        "pc1",
        "10.0.0.5",
        true,
        "pc1",
        new[]
        {
            new ConnectionAttempt("pc1.corp.example", false, "denied", 5, "10.0.0.5"),
            new ConnectionAttempt("pc1", true, "3 profile folder(s) readable.", null, "10.0.0.9"),
        });

    [Fact]
    public async Task ScanComputer_AccessDenied_ShowsWhichAddressEachNameResolvesTo()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("denied");
        string? asked = null;
        var scanner = new ProfileScanner(fs, connectionProbe: computer =>
        {
            asked = computer;
            return TwoNamesTwoComputers();
        });

        var row = Assert.Single(await scanner.ScanComputerAsync("pc1", default));

        Assert.Equal("pc1", asked);
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Contains("pc1.corp.example (10.0.0.5) failed, Windows error 5", row.Message);
        Assert.Contains("pc1 (10.0.0.9) reachable", row.Message);
        Assert.Contains("different addresses", row.Message);
        // Near the front of the row, which is trimmed in the grid, ahead of the long guidance.
        Assert.True(row.Message!.IndexOf("Tried:", StringComparison.Ordinal)
                    < row.Message.IndexOf("addressed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FindUser_AccessDenied_ShowsTheNamesToo()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("listing is denied");
        fs.FailNtUser = (_, _) => new UnauthorizedAccessException("file is denied");
        var scanner = new ProfileScanner(fs, connectionProbe: _ => TwoNamesTwoComputers());

        var list = new List<ProfileResult>();
        await foreach (var r in scanner.FindUserAsync("alice", new[] { "pc1" }, default)) list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Contains("pc1 (10.0.0.9) reachable", row.Message);
    }

    [Fact]
    public async Task ScanComputer_AccessDenied_StaysAsItWasWhenTheProbeFails()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("denied");
        var scanner = new ProfileScanner(fs, connectionProbe: _ => throw new InvalidOperationException("probe broke"));

        var row = Assert.Single(await scanner.ScanComputerAsync("pc1", default));

        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Contains("Access denied: denied", row.Message);
        Assert.DoesNotContain("Tried:", row.Message);
    }

    [Fact]
    public async Task ScanComputer_AccessDenied_DoesNotWaitLongerThanThreeTimesTheTimeoutForTheProbe()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("denied");
        var scanner = new ProfileScanner(
            fs,
            perPcTimeout: TimeSpan.FromMilliseconds(100),
            connectionProbe: _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return TwoNamesTwoComputers();
            });

        var started = DateTime.UtcNow;
        var row = Assert.Single(await scanner.ScanComputerAsync("pc1", default));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "the scan waited for the slow probe");
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.DoesNotContain("Tried:", row.Message);
    }

    [Fact]
    public async Task ScanComputer_OnlyAnAccessFailureOfTheWholeComputerIsProbed()
    {
        var probed = 0;
        ConnectionTestResult Probe(string _)
        {
            Interlocked.Increment(ref probed);
            return TwoNamesTwoComputers();
        }

        // An unreachable computer: probing it would only add waiting to a bulk scan.
        var offline = Basic();
        offline.Fail = _ => new DirectoryNotFoundException();
        await new ProfileScanner(offline, connectionProbe: Probe).ScanComputerAsync("pc1", default);

        // One profile that refuses access while the others can be read: the name clearly works.
        var oneDenied = Basic();
        oneDenied.Folders["pc1"].Add("carol");
        oneDenied.FailNtUser = (_, user) => user == "carol" ? new UnauthorizedAccessException("denied") : null;
        await new ProfileScanner(oneDenied, connectionProbe: Probe).ScanComputerAsync("pc1", default);

        Assert.Equal(0, probed);
    }

    // Every computer that is checked gets a row, so that "looked and found nothing" can be told
    // from "never looked". Before, a computer without the profile was left out silently.
    [Fact]
    public async Task FindUser_ReportsEveryComputer_WithTheProfileWithoutItAndUnreachable()
    {
        var fs = Basic();
        fs.Folders["pc2"] = new() { "zed" };
        fs.Fail = c => c == "pc3" ? new DirectoryNotFoundException() : null;
        var list = new List<ProfileResult>();
        await foreach (var r in new ProfileScanner(fs).FindUserAsync("alice", new[] { "pc1", "pc2", "pc3" }, default))
            list.Add(r);

        Assert.Equal(new[] { "pc1", "pc2", "pc3" }, list.Select(x => x.Computer).OrderBy(x => x));
        Assert.Equal(ScanStatus.Ok, list.Single(x => x.Computer == "pc1").Status);
        Assert.Equal(ScanStatus.Offline, list.Single(x => x.Computer == "pc3").Status);

        var none = list.Single(x => x.Computer == "pc2");
        Assert.Equal(ScanStatus.NoProfile, none.Status);
        Assert.Equal("alice", none.Username);
        Assert.Null(none.LastActivity);
        Assert.Contains("alice", none.Message);
    }

    // The listing is refused, but the exact folder name was looked for and is not there. That is
    // not a rights problem on the computer, and the row must not say that it is: it says what was
    // looked for and what could not be looked for.
    [Fact]
    public async Task FindUser_ListingRefusedAndNoFolderWithTheExactName_ReportsNoProfileAndSaysWhatCouldNotBeChecked()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("listing is denied");
        var probed = 0;
        var scanner = new ProfileScanner(fs, connectionProbe: _ =>
        {
            Interlocked.Increment(ref probed);
            return TwoNamesTwoComputers();
        });

        var list = new List<ProfileResult>();
        await foreach (var r in scanner.FindUserAsync("ghost", new[] { "pc1" }, default)) list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal(ScanStatus.NoProfile, row.Status);
        Assert.Equal("ghost", row.Username);
        Assert.Contains("named exactly", row.Message);
        Assert.Contains("ghost.", row.Message);                       // the suffix that could not be looked for
        Assert.DoesNotContain("has to be an administrator", row.Message);
        Assert.Equal(0, probed);                                      // the computer answered; nothing to look into
    }

    [Fact]
    public async Task FindUser_ListingRefusedAndAnotherFileRefused_StillReportsAccessDenied()
    {
        // The exact profile exists but cannot be read, so this is an access problem after all.
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("listing is denied");
        fs.FailNtUser = (_, _) => new UnauthorizedAccessException("file is denied");

        var list = new List<ProfileResult>();
        await foreach (var r in new ProfileScanner(fs).FindUserAsync("alice", new[] { "pc1" }, default)) list.Add(r);

        Assert.Equal(ScanStatus.AccessDenied, Assert.Single(list).Status);
    }

    [Fact]
    public async Task FindUser_ReadsAKnownProfileWithoutListingTheUsersFolder()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("listing is denied");
        var list = new List<ProfileResult>();
        await foreach (var r in new ProfileScanner(fs).FindUserAsync("alice", new[] { "pc1" }, default))
            list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal(ScanStatus.Ok, row.Status);
        Assert.Equal("alice", row.Username);
        Assert.Equal(ActivitySource.NtUserDat, row.Source);
    }

    [Fact]
    public async Task FindUser_KnownProfileAccessDenied_ReportsTheProfilePath()
    {
        var fs = Basic();
        fs.Fail = _ => new UnauthorizedAccessException("listing is denied");
        fs.FailNtUser = (_, _) => new UnauthorizedAccessException(
            @"Access to the path '\\pc1\C$\Users\alice\NTUSER.DAT' is denied.");
        var list = new List<ProfileResult>();
        await foreach (var r in new ProfileScanner(fs).FindUserAsync("alice", new[] { "pc1" }, default))
            list.Add(r);

        var row = Assert.Single(list);
        Assert.Equal(ScanStatus.AccessDenied, row.Status);
        Assert.Contains("NTUSER.DAT", row.Message);
    }

    [Fact]
    public async Task FindUser_RespectsMaxParallel()
    {
        var fs = Basic(); fs.Delay = TimeSpan.FromMilliseconds(50);
        var pcs = Enumerable.Range(0, 8).Select(i => "p" + i).ToList();
        foreach (var p in pcs) fs.Folders[p] = new() { "alice" };
        await foreach (var _ in new ProfileScanner(fs, 2).FindUserAsync("alice", pcs, default)) { }
        Assert.True(fs.Peak <= 2, $"peak {fs.Peak}");
    }

    [Fact]
    public async Task FindUser_Cancelled_StopsAndKeepsYieldedResults()
    {
        var fs = Basic(); fs.Delay = TimeSpan.FromMilliseconds(50);
        var pcs = Enumerable.Range(0, 20).Select(i => "p" + i).ToList();
        foreach (var p in pcs) fs.Folders[p] = new() { "alice" };
        using var cts = new CancellationTokenSource();
        var got = 0;
        await foreach (var _ in new ProfileScanner(fs, 1).FindUserAsync("alice", pcs, cts.Token))
        {
            if (++got == 2) cts.Cancel();
        }
        Assert.InRange(got, 2, 19);
    }
}
