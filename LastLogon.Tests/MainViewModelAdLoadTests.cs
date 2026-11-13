using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

/// <summary>
/// Loading computer names from Active Directory fills the list that is typed into the window. It
/// is not a scan: it hands back the names and a sentence about what happened, and the window
/// decides where to show them, because it works the same in every lookup.
/// </summary>
public class MainViewModelAdLoadTests
{
    private static MainViewModel Make(Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>> search)
        => new(new ProfileScanner(new FakeFs(), 4), searchAd: search);

    private static Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>> Finds(params string[] names)
        => (_, _, _) => Task.FromResult<IReadOnlyList<string>>(names);

    [Fact]
    public async Task ReturnsTheNamesAndSaysHowManyWereLoaded()
    {
        var vm = Make(Finds("PC-1", "PC-2"));

        var result = await vm.LoadFromAdAsync("PC", null);

        Assert.Equal(new[] { "PC-1", "PC-2" }, result.Names);
        Assert.Equal("2 computers loaded from AD.", result.Message);
    }

    [Fact]
    public async Task OneComputerIsNotPlural()
    {
        var vm = Make(Finds("PC-1"));

        var result = await vm.LoadFromAdAsync("", null);

        Assert.Equal("1 computer loaded from AD.", result.Message);
    }

    [Fact]
    public async Task PassesThePrefixAndTheOuToTheSearch()
    {
        string? seenPrefix = null;
        string? seenOu = null;
        var vm = Make((prefix, ou, _) =>
        {
            seenPrefix = prefix;
            seenOu = ou;
            return Task.FromResult<IReadOnlyList<string>>(new[] { "PC-1" });
        });

        await vm.LoadFromAdAsync("PC-", "OU=Sales");

        Assert.Equal("PC-", seenPrefix);
        Assert.Equal("OU=Sales", seenOu);
    }

    // An empty answer must not wipe the list the operator typed, so it carries no names, and the
    // sentence says what was asked for.
    [Fact]
    public async Task AnEmptyAnswerReturnsNoNamesAndNamesThePrefix()
    {
        var vm = Make(Finds());

        var result = await vm.LoadFromAdAsync("LAB", null);

        Assert.Empty(result.Names);
        Assert.Equal("No computer in AD has a name starting with LAB.", result.Message);
    }

    [Fact]
    public async Task AnEmptyAnswerWithoutAPrefixSaysNothingWasFound()
    {
        var vm = Make(Finds());

        var result = await vm.LoadFromAdAsync("", null);

        Assert.Empty(result.Names);
        Assert.Equal("No computers found in AD.", result.Message);
    }

    [Fact]
    public async Task AFailureReturnsNoNamesAndTheReason()
    {
        var vm = Make((_, _, _) => throw new InvalidOperationException("The server is not operational."));

        var result = await vm.LoadFromAdAsync("", null);

        Assert.Empty(result.Names);
        Assert.Equal("AD search failed: The server is not operational.", result.Message);
        Assert.False(vm.IsSearchingAd);
    }

    [Fact]
    public async Task CancellingStopsTheSearchAndSaysSo()
    {
        var started = new TaskCompletionSource();
        var vm = Make(async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<string>();
        });

        var pending = vm.LoadFromAdAsync("", null);
        await started.Task;
        Assert.True(vm.IsSearchingAd);

        vm.Cancel();
        var result = await pending;

        Assert.Empty(result.Names);
        Assert.Equal("AD search cancelled.", result.Message);
        Assert.False(vm.IsSearchingAd);
    }

    // The status line of a lookup describes its last scan. A load is not a scan, and in Logon
    // history there is no profile lookup for it to write into.
    [Fact]
    public async Task LeavesTheStatusLineOfTheLookupAlone()
    {
        var vm = Make(Finds("PC-1"));

        await vm.LoadFromAdAsync("", null);

        Assert.Equal("Ready", vm.StatusText);
    }
}
