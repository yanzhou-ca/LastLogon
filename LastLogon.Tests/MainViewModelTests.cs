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
