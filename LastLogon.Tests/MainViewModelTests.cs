using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class MainViewModelTests
{
    static (MainViewModel vm, FakeFs fs) Make()
    {
        var fs = new FakeFs();
        fs.Folders["pc1"] = new() { "alice", "bob" };
        fs.NtUser["pc1|alice"] = new DateTime(2024, 1, 1);
        fs.NtUser["pc1|bob"] = new DateTime(2025, 1, 1);
        fs.Folders["pc2"] = new() { "alice" };
        fs.NtUser["pc2|alice"] = new DateTime(2025, 6, 1);
        return (new MainViewModel(new ProfileScanner(fs, 4)), fs);
    }

    [Fact]
    public async Task Scan_ByComputer_FillsResultsNewestFirst()
    {
        var (vm, _) = Make();
        vm.ComputersText = "pc1";
        await vm.ScanAsync();
        Assert.Equal(new[] { "bob", "alice" }, vm.Results.Select(r => r.Username));
    }

    [Fact]
    public async Task Scan_ByComputer_WithMultipleComputersIncludesEveryComputerNewestFirst()
    {
        var (vm, _) = Make();
        vm.ComputersText = "pc1 pc2";
        await vm.ScanAsync();
        Assert.Equal(new[] { "pc2", "pc1", "pc1" }, vm.Results.Select(r => r.Computer));
    }

    [Fact]
    public async Task Scan_ByComputer_WithoutComputerNamesIdentifiesComputerField()
    {
        var (vm, _) = Make();
        vm.ComputersText = " , ";
        await vm.ScanAsync();
        Assert.Equal("Computer name: Enter at least one computer name.", vm.StatusText);
    }

    [Fact]
    public async Task Scan_ByComputer_WithPathInputReportsInvalidComputerWithoutScanning()
    {
        var (vm, _) = Make();
        vm.ComputersText = @"\\attacker\share";
        await vm.ScanAsync();
        Assert.Equal("Computer name: Enter a short computer name; paths and domains are not allowed.", vm.StatusText);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public async Task Scan_ByComputer_WithInvalidInputClearsPreviousResults()
    {
        var (vm, _) = Make();
        vm.ComputersText = "pc1";
        await vm.ScanAsync();
        Assert.NotEmpty(vm.Results);

        vm.ComputersText = @"\\attacker\share";
        await vm.ScanAsync();

        Assert.Empty(vm.Results);
        Assert.Equal("Computer name: Enter a short computer name; paths and domains are not allowed.", vm.StatusText);
    }

    [Fact]
    public async Task Scan_ByUser_StreamsResultsAsTheyArrive()
    {
        var (vm, _) = Make();
        vm.Mode = LookupMode.ByUser; vm.Username = "CORP\\alice"; vm.ComputersText = "pc1 pc2";
        await vm.ScanAsync();
        Assert.Equal(new[] { "pc2", "pc1" }, vm.Results.Select(r => r.Computer));
    }

    [Fact]
    public async Task Scan_ByUser_BlankUsername_SetsStatusAndDoesNotScan()
    {
        var (vm, _) = Make();
        vm.Mode = LookupMode.ByUser; vm.ComputersText = "pc1";
        await vm.ScanAsync();
        Assert.Empty(vm.Results);
        Assert.Equal("Username: Enter a username.", vm.StatusText);
    }

    [Fact]
    public async Task Cancel_StopsScanAndKeepsPartialResults()
    {
        var (vm, fs) = Make();
        fs.Delay = TimeSpan.FromMilliseconds(80);
        var pcs = Enumerable.Range(0, 30).Select(i => "q" + i).ToList();
        foreach (var p in pcs) fs.Folders[p] = new() { "alice" };
        vm.Mode = LookupMode.ByUser; vm.Username = "alice"; vm.ComputersText = string.Join(" ", pcs);
        var scan = vm.ScanAsync();
        await Task.Delay(300);
        vm.Cancel();
        await scan;
        Assert.InRange(vm.Results.Count, 1, 29);
        Assert.False(vm.IsScanning);
        Assert.Matches(@"^Cancelled\. \d+ of 30 computer\(s\) checked: ", vm.StatusText);
    }

    // D-01: a scan started while another one runs replaces it. The replaced scan unwinding must
    // not report "not scanning" or overwrite the status of the scan that is still running.
    [Fact]
    public async Task Scan_StartedWhileAnotherIsRunning_KeepsScanningUntilTheNewScanEnds()
    {
        var (vm, fs) = Make();
        fs.Delay = TimeSpan.FromMilliseconds(600);

        vm.ComputersText = "pc1";
        var first = vm.ScanAsync();
        await Task.Delay(100);
        vm.ComputersText = "pc2";
        var second = vm.ScanAsync();

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsScanning, "the replaced scan ended, but the new scan is still running");
        Assert.Equal("Scanning…", vm.StatusText);

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsScanning);
        Assert.StartsWith("Done.", vm.StatusText);
        Assert.Equal(new[] { "pc2" }, vm.Results.Select(r => r.Computer));
    }

    // By user says how many computers were checked and what was found, so that "0 results" can no
    // longer mean either "nobody has the profile" or "nothing was looked at".
    [Fact]
    public async Task Scan_ByUser_SaysHowManyComputersWereCheckedAndWhatWasFound()
    {
        var (vm, fs) = Make();
        fs.Folders["pc3"] = new() { "bob" };
        fs.NtUser["pc3|bob"] = new DateTime(2025, 1, 1);
        fs.Fail = c => c == "pc4" ? new DirectoryNotFoundException() : null;

        vm.Mode = LookupMode.ByUser; vm.Username = "alice"; vm.ComputersText = "pc1 pc2 pc3 pc4";
        await vm.ScanAsync();

        Assert.Equal(4, vm.Results.Count);
        Assert.Equal(
            "Done. 4 computer(s) checked: 2 with a profile, 1 without, 1 could not be checked.",
            vm.StatusText);
    }

    [Fact]
    public async Task Scan_ByUser_NobodyHasTheProfile_StillShowsOneRowPerComputer()
    {
        var (vm, _) = Make();
        vm.Mode = LookupMode.ByUser; vm.Username = "ghost"; vm.ComputersText = "pc1 pc2";
        await vm.ScanAsync();

        Assert.All(vm.Results, r => Assert.Equal(ScanStatus.NoProfile, r.Status));
        Assert.Equal(new[] { "pc1", "pc2" }, vm.Results.Select(r => r.Computer).OrderBy(c => c));
        Assert.Equal("Done. 2 computer(s) checked: 0 with a profile, 2 without, 0 could not be checked.", vm.StatusText);
    }

    [Fact]
    public async Task Scan_ByComputer_KeepsTheResultCountStatus()
    {
        var (vm, _) = Make();
        vm.ComputersText = "pc1";
        await vm.ScanAsync();

        Assert.Equal("Done. 2 result(s).", vm.StatusText);
    }

    [Fact]
    public void IsStale_UsesStaleDays()
    {
        var (vm, _) = Make();
        ProfileResult At(int days) => new("c", "u", DateTime.Now.AddDays(-days), ActivitySource.NtUserDat, ScanStatus.Ok, null);
        Assert.True(vm.IsStale(At(91)));
        Assert.False(vm.IsStale(At(89)));
    }

    [Fact]
    public async Task Scan_ClearsPreviousResults()
    {
        var (vm, _) = Make();
        vm.ComputersText = "pc1";
        await vm.ScanAsync();
        vm.ComputersText = "pc2";
        await vm.ScanAsync();
        Assert.Single(vm.Results);
    }
}
