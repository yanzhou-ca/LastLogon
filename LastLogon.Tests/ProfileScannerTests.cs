using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class FakeFs : IProfileFileSystem
{
    public Dictionary<string, List<string>> Folders = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTime?> NtUser = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, Exception?>? Fail;
    public TimeSpan Delay = TimeSpan.Zero;
    private int _cur, _peak;
    public int Peak => _peak;

    public IReadOnlyList<string> ListUserFolders(string computer)
    {
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
    public DateTime? GetNtUserDatTime(string c, string u) => NtUser.TryGetValue($"{c}|{u}", out var t) ? t : null;
    public DateTime GetFolderTime(string c, string u) => new(2020, 1, 1);
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

    [Fact]
    public async Task FindUser_ReturnsOnlyComputersWithProfile_IncludingErrors()
    {
        var fs = Basic();
        fs.Folders["pc2"] = new() { "zed" };
        fs.Fail = c => c == "pc3" ? new DirectoryNotFoundException() : null;
        var list = new List<ProfileResult>();
        await foreach (var r in new ProfileScanner(fs).FindUserAsync("alice", new[] { "pc1", "pc2", "pc3" }, default))
            list.Add(r);
        Assert.Equal(new[] { "pc1", "pc3" }, list.Select(x => x.Computer).OrderBy(x => x));
        Assert.Equal(ScanStatus.Offline, list.Single(x => x.Computer == "pc3").Status);
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
