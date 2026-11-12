using System.Collections.Specialized;
using LastLogon.Core;

namespace LastLogon.Tests;

public class LogonHistoryViewModelTests
{
    [Fact]
    public async Task ScanAsync_ConvertsOneSharedLocalRangeToUtc()
    {
        var scanner = new FakeScanner((_, _, _, _) => Updates());
        var viewModel = new LogonHistoryViewModel(scanner)
        {
            ComputersText = "pc01\r\npc02",
            StartLocal = new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.FromHours(2)),
            EndLocal = new DateTimeOffset(2025, 3, 5, 8, 0, 0, TimeSpan.FromHours(2))
        };

        await viewModel.ScanAsync();

        Assert.Equal(new[] { "pc01", "pc02" }, scanner.Computers);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 3, 0, 0, TimeSpan.Zero), scanner.FromUtc);
        Assert.Equal(new DateTimeOffset(2025, 3, 5, 6, 0, 0, TimeSpan.Zero), scanner.ToUtc);
    }

    [Fact]
    public async Task ScanAsync_RejectsEmptyComputerListAndReversedRange()
    {
        var emptyScanner = new FakeScanner((_, _, _, _) => Updates());
        var emptyViewModel = new LogonHistoryViewModel(emptyScanner) { ComputersText = " , \r\n" };

        await emptyViewModel.ScanAsync();

        Assert.Equal(0, emptyScanner.CallCount);
        Assert.Equal("Computer name: Enter at least one computer name.", emptyViewModel.StatusText);

        var reversedScanner = new FakeScanner((_, _, _, _) => Updates());
        var reversedViewModel = new LogonHistoryViewModel(reversedScanner)
        {
            ComputersText = "pc01",
            StartLocal = new DateTimeOffset(2025, 3, 5, 8, 0, 0, TimeSpan.Zero),
            EndLocal = new DateTimeOffset(2025, 3, 5, 7, 0, 0, TimeSpan.Zero)
        };

        await reversedViewModel.ScanAsync();

        Assert.Equal(0, reversedScanner.CallCount);
        Assert.Equal("Start time must be earlier than end time.", reversedViewModel.StatusText);
    }

    [Fact]
    public async Task ScanAsync_StreamsEventsAndCompletionStatuses()
    {
        var eventAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventRecord = Record("pc01", "alice", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero));
        var scanner = new FakeScanner((_, _, _, token) =>
            EventThenCompletions(eventRecord, continueScan.Task, token));
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01 pc02" };
        viewModel.Events.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add)
            {
                eventAdded.TrySetResult();
            }
        };

        var scan = viewModel.ScanAsync();
        await eventAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { eventRecord }, viewModel.Events);
        Assert.Empty(viewModel.ComputerStatuses);

        continueScan.SetResult();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, viewModel.ComputerStatuses.Count);
        Assert.Contains(viewModel.ComputerStatuses, item => item.Computer == "pc01" && item.Status == LogonQueryStatus.Success);
        Assert.Contains(viewModel.ComputerStatuses, item => item.Computer == "pc02" && item.Status == LogonQueryStatus.AccessDenied);
    }

    [Fact]
    public async Task Cancel_KeepsAlreadyReceivedEvents()
    {
        var eventAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new FakeScanner((_, _, _, token) => EventThenWaitForCancellation(
            Record("pc01", "alice", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero)),
            token));
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };
        viewModel.Events.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add)
            {
                eventAdded.TrySetResult();
            }
        };

        var scan = viewModel.ScanAsync();
        await eventAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.Cancel();
        await scan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(viewModel.Events);
        Assert.Contains("Cancelled", viewModel.StatusText);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Filters_DefaultToAllAndRetainUnknownAccountsAndTypes()
    {
        var unknown = Record(
            "pc01",
            "SYSTEM",
            "NT AUTHORITY",
            99,
            new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero));
        var scanner = ScannerWithEvents(unknown);
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };

        await viewModel.ScanAsync();

        Assert.Equal(LogonAccountFilter.All, viewModel.AccountFilter);
        Assert.Equal(LogonTypeFilter.All, viewModel.TypeFilter);
        Assert.Equal(new[] { unknown }, viewModel.FilteredEvents);
    }

    [Fact]
    public async Task Filters_LocalDomainAndLogonTypeIndependently()
    {
        var localNetwork = Record(
            "pc01", "local-user", "PC01", 3, new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero));
        var domainInteractive = Record(
            "pc01", "domain-user", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 1, 0, TimeSpan.Zero));
        var domainUnknown = Record(
            "pc01", "unknown-user", "CORP", 99, new DateTimeOffset(2025, 3, 4, 5, 2, 0, TimeSpan.Zero));
        var authorityService = Record(
            "pc01", "service-user", "NT AUTHORITY", 5, new DateTimeOffset(2025, 3, 4, 5, 3, 0, TimeSpan.Zero));
        var scanner = ScannerWithEvents(localNetwork, domainInteractive, domainUnknown, authorityService);
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };

        await viewModel.ScanAsync();

        viewModel.AccountFilter = LogonAccountFilter.Local;
        Assert.Equal(new[] { localNetwork }, viewModel.FilteredEvents);

        viewModel.AccountFilter = LogonAccountFilter.Domain;
        Assert.Equal(new[] { domainUnknown, domainInteractive }, viewModel.FilteredEvents);

        viewModel.AccountFilter = LogonAccountFilter.All;
        viewModel.TypeFilter = LogonTypeFilter.Interactive;
        Assert.Equal(new[] { domainInteractive }, viewModel.FilteredEvents);

        viewModel.TypeFilter = LogonTypeFilter.Network;
        Assert.Equal(new[] { localNetwork }, viewModel.FilteredEvents);

        viewModel.TypeFilter = LogonTypeFilter.Service;
        Assert.Equal(new[] { authorityService }, viewModel.FilteredEvents);
    }

    [Fact]
    public async Task Events_ArrivingOutOfOrder_AreInsertedNewestFirst()
    {
        var earliest = Record("pc01", "earliest", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero));
        var latest = Record("pc01", "latest", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 2, 0, TimeSpan.Zero));
        var middle = Record("pc01", "middle", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 1, 0, TimeSpan.Zero));
        var scanner = ScannerWithEvents(earliest, latest, middle);
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };

        await viewModel.ScanAsync();

        Assert.Equal(new[] { latest, middle, earliest }, viewModel.Events);
    }

    [Fact]
    public void SelectedEvent_ExposesAvailableDetails()
    {
        var selected = Record(
            "pc01",
            "alice",
            "CORP",
            2,
            new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero),
            new Dictionary<string, string> { ["TargetUserName"] = "alice", ["LogonType"] = "2" });
        var viewModel = new LogonHistoryViewModel(new FakeScanner((_, _, _, _) => Updates()))
        {
            SelectedEvent = selected
        };

        Assert.Equal(
            new[]
            {
                new KeyValuePair<string, string>("TargetUserName", "alice"),
                new KeyValuePair<string, string>("LogonType", "2")
            },
            viewModel.SelectedDetails);
    }

    [Fact]
    public async Task ScanAsync_RejectsATimeRangeThatIsTooLong()
    {
        var scanner = new FakeScanner((_, _, _, _) => Updates());
        var viewModel = new LogonHistoryViewModel(scanner)
        {
            ComputersText = "pc01",
            StartLocal = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            EndLocal = new DateTimeOffset(2025, 3, 5, 0, 0, 0, TimeSpan.Zero)
        };

        await viewModel.ScanAsync();

        Assert.Equal(0, scanner.CallCount);
        Assert.Contains("must not exceed", viewModel.StatusText);
    }

    [Fact]
    public async Task ScanAsync_KeepsTheEventLimitAndReportsThatResultsAreIncomplete()
    {
        var events = Enumerable.Range(0, 50)
            .Select(index => Record(
                "pc01",
                "user" + index,
                "CORP",
                2,
                new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero).AddMinutes(index)))
            .ToArray();
        var scanner = new FakeScanner((_, _, _, _) => Updates(
            events.Select(item => (LogonScanUpdate)new LogonEventFound(item)).ToArray()));
        var viewModel = new LogonHistoryViewModel(scanner, maxEvents: 10) { ComputersText = "pc01" };

        await viewModel.ScanAsync();

        Assert.Equal(10, viewModel.Events.Count);
        Assert.True(viewModel.IsTruncated);
        Assert.Contains("incomplete", viewModel.StatusText);
        Assert.Contains("event limit", viewModel.StatusText);
    }

    [Fact]
    public async Task ScanAsync_DoesNotRebuildTheFilteredCollectionForEveryEvent()
    {
        var events = Enumerable.Range(0, 200)
            .Select(index => Record(
                "pc01",
                "user" + index,
                "CORP",
                2,
                new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero).AddMinutes(index)))
            .ToArray();
        var scanner = new FakeScanner((_, _, _, _) => Updates(
            events.Select(item => (LogonScanUpdate)new LogonEventFound(item)).ToArray()));
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };
        var filteredChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LogonHistoryViewModel.FilteredEvents)) filteredChanges++;
        };

        await viewModel.ScanAsync();

        // The bound collection keeps its identity, so the grid is never handed a new item
        // source and does not rebuild every row for every event.
        Assert.Equal(0, filteredChanges);
        Assert.Equal(200, viewModel.FilteredEvents.Count);
        Assert.True(viewModel.HasVisibleEvents);
    }

    [Fact]
    public async Task Filters_RebuildTheFilteredCollectionWithoutTouchingTheEventList()
    {
        var local = Record(
            "pc01", "local-user", "PC01", 3, new DateTimeOffset(2025, 3, 4, 5, 0, 0, TimeSpan.Zero));
        var domain = Record(
            "pc01", "domain-user", "CORP", 2, new DateTimeOffset(2025, 3, 4, 5, 1, 0, TimeSpan.Zero));
        var scanner = new FakeScanner((_, _, _, _) => Updates(
            new LogonEventFound(local), new LogonEventFound(domain)));
        var viewModel = new LogonHistoryViewModel(scanner) { ComputersText = "pc01" };

        await viewModel.ScanAsync();

        viewModel.AccountFilter = LogonAccountFilter.Local;
        Assert.Equal(new[] { local }, viewModel.FilteredEvents);
        Assert.Equal(2, viewModel.Events.Count);

        viewModel.AccountFilter = LogonAccountFilter.All;
        Assert.Equal(2, viewModel.FilteredEvents.Count);
    }

    private static FakeScanner ScannerWithEvents(params LogonEventRecord[] events)
        => new((_, _, _, _) => Updates(
            events.Select(item => (LogonScanUpdate)new LogonEventFound(item))
                .Append(new LogonComputerCompleted("pc01", LogonQueryStatus.Success, null, events.Length))
                .ToArray()));

    private static LogonEventRecord Record(
        string computer,
        string accountName,
        string? accountDomain,
        int? logonType,
        DateTimeOffset timeUtc,
        IReadOnlyDictionary<string, string>? data = null)
        => new(
            computer,
            timeUtc,
            4624,
            "Successful logon",
            accountName,
            accountDomain,
            null,
            logonType,
            logonType == 99 ? "Unknown (99)" : logonType?.ToString(),
            null,
            data ?? new Dictionary<string, string>());

    private static async IAsyncEnumerable<LogonScanUpdate> Updates(
        params LogonScanUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return update;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<LogonScanUpdate> EventThenCompletions(
        LogonEventRecord eventRecord,
        Task continueScan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new LogonEventFound(eventRecord);
        await continueScan.WaitAsync(cancellationToken);
        yield return new LogonComputerCompleted("pc01", LogonQueryStatus.Success, null, 1);
        yield return new LogonComputerCompleted("pc02", LogonQueryStatus.AccessDenied, "Access denied.", 0);
    }

    private static async IAsyncEnumerable<LogonScanUpdate> EventThenWaitForCancellation(
        LogonEventRecord eventRecord,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new LogonEventFound(eventRecord);
        await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            .Task.WaitAsync(cancellationToken);
    }

    private sealed class FakeScanner(
        Func<IEnumerable<string>, DateTimeOffset, DateTimeOffset, CancellationToken, IAsyncEnumerable<LogonScanUpdate>> scan)
        : ILogonEventScanner
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<string> Computers { get; private set; } = Array.Empty<string>();
        public DateTimeOffset FromUtc { get; private set; }
        public DateTimeOffset ToUtc { get; private set; }

        public IAsyncEnumerable<LogonScanUpdate> ScanAsync(
            IEnumerable<string> computers,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Computers = computers.ToArray();
            FromUtc = fromUtc;
            ToUtc = toUtc;
            return scan(Computers, fromUtc, toUtc, cancellationToken);
        }
    }
}
