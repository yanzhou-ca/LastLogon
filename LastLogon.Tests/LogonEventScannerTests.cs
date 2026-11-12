using LastLogon.Core;

namespace LastLogon.Tests;

public class LogonEventScannerTests
{
    [Fact]
    public async Task ScanAsync_ReportsSuccessfulEmptySeparatelyFromFailure()
    {
        var source = new DelegateSource((computer, _) => computer == "denied"
            ? Throw(new UnauthorizedAccessException())
            : Array.Empty<string>());
        var scanner = new LogonEventScanner(source);

        var updates = await CollectAsync(scanner, new[] { "empty", "denied" });

        Assert.Equal(LogonQueryStatus.Success, CompletionFor(updates, "empty").Status);
        Assert.Equal(0, CompletionFor(updates, "empty").EventCount);
        Assert.Equal(LogonQueryStatus.AccessDenied, CompletionFor(updates, "denied").Status);
    }

    [Fact]
    public async Task ScanAsync_ReportsOfflineTimeoutAndOtherErrors()
    {
        var source = new DelegateSource((computer, token) => computer switch
        {
            "directory" => Throw(new DirectoryNotFoundException()),
            "network" => Throw(new IOException()),
            "timeout" => WaitUntilCancelled(token),
            _ => Throw(new InvalidOperationException())
        });
        var scanner = new LogonEventScanner(source, perComputerTimeout: TimeSpan.FromMilliseconds(100));

        var updates = await CollectAsync(scanner, new[] { "directory", "network", "timeout", "unexpected" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LogonQueryStatus.Offline, CompletionFor(updates, "directory").Status);
        Assert.Equal(LogonQueryStatus.Offline, CompletionFor(updates, "network").Status);
        Assert.Equal(LogonQueryStatus.TimedOut, CompletionFor(updates, "timeout").Status);
        Assert.Equal(LogonQueryStatus.Error, CompletionFor(updates, "unexpected").Status);
    }

    [Fact]
    public async Task ScanAsync_RespectsConcurrencyLimit()
    {
        using var release = new ManualResetEventSlim();
        var twoActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = 0;
        var peak = 0;
        var source = new DelegateSource((_, token) => WaitForRelease(
            () =>
            {
                var active = Interlocked.Increment(ref current);
                int observed;
                while (active > (observed = Volatile.Read(ref peak))
                    && Interlocked.CompareExchange(ref peak, active, observed) != observed)
                {
                }

                if (active == 2)
                {
                    twoActive.TrySetResult();
                }
            },
            () => Interlocked.Decrement(ref current),
            release,
            token));
        var scanner = new LogonEventScanner(source, maxParallel: 2);
        var computers = Enumerable.Range(0, 8).Select(index => $"pc{index}").ToArray();

        var scanTask = CollectAsync(scanner, computers);
        await twoActive.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.Set();
        var updates = await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8, updates.OfType<LogonComputerCompleted>().Count());
        Assert.InRange(peak, 1, 2);
    }

    [Fact]
    public async Task ScanAsync_TimedOutReadRetainsCapacityUntilItExits()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTimedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateSource((computer, _) => computer switch
        {
            "pc01" => WaitForReleaseIgnoringCancellation(() => firstStarted.TrySetResult(), releaseFirst),
            "pc02" => SignalStarted(secondStarted),
            _ => Array.Empty<string>()
        });
        var scanner = new LogonEventScanner(
            source,
            maxParallel: 1,
            perComputerTimeout: TimeSpan.FromMilliseconds(50));

        var scanTask = Task.Run(async () =>
        {
            var updates = new List<LogonScanUpdate>();
            await foreach (var update in scanner.ScanAsync(
                new[] { "pc01", "pc02" },
                DateTimeOffset.Parse("2025-03-04T00:00:00Z"),
                DateTimeOffset.Parse("2025-03-05T00:00:00Z"),
                CancellationToken.None))
            {
                updates.Add(update);
                if (update is LogonComputerCompleted
                    {
                        Computer: "pc01",
                        Status: LogonQueryStatus.TimedOut
                    })
                {
                    firstTimedOut.TrySetResult();
                }
            }

            return updates;
        });

        var secondStartedBeforeRelease = false;
        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstTimedOut.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondStartedBeforeRelease = await Task.WhenAny(
                secondStarted.Task,
                Task.Delay(TimeSpan.FromMilliseconds(100))) == secondStarted.Task;
        }
        finally
        {
            releaseFirst.Set();
        }

        var updates = await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(secondStartedBeforeRelease);
        Assert.Equal(LogonQueryStatus.TimedOut, CompletionFor(updates, "pc01").Status);
        Assert.Equal(LogonQueryStatus.Success, CompletionFor(updates, "pc02").Status);
    }

    [Fact]
    public async Task ScanAsync_Cancelled_KeepsAlreadyEmittedEvents()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new DelegateSource((_, token) => YieldEventThenWait(EventXml, token));
        var scanner = new LogonEventScanner(source);
        var updates = new List<LogonScanUpdate>();

        await foreach (var update in scanner.ScanAsync(
            new[] { "pc01" },
            DateTimeOffset.Parse("2025-03-04T00:00:00Z"),
            DateTimeOffset.Parse("2025-03-05T00:00:00Z"),
            cancellation.Token))
        {
            updates.Add(update);
            if (update is LogonEventFound)
            {
                cancellation.Cancel();
            }
        }

        Assert.Contains(updates, update => update is LogonEventFound);
        Assert.DoesNotContain(
            updates.OfType<LogonComputerCompleted>(),
            completed => completed.Status == LogonQueryStatus.Success);
    }

    [Fact]
    public async Task ScanAsync_CancelledWhileQueued_ReportsEveryComputer()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new DelegateSource((_, token) => YieldEventThenWait(EventXml, token));
        var scanner = new LogonEventScanner(source, maxParallel: 1);
        var updates = new List<LogonScanUpdate>();

        await foreach (var update in scanner.ScanAsync(
            new[] { "pc01", "pc02" },
            DateTimeOffset.Parse("2025-03-04T00:00:00Z"),
            DateTimeOffset.Parse("2025-03-05T00:00:00Z"),
            cancellation.Token))
        {
            updates.Add(update);
            if (update is LogonEventFound)
            {
                cancellation.Cancel();
            }
        }

        var completions = updates.OfType<LogonComputerCompleted>().ToArray();

        Assert.Equal(new[] { "pc01", "pc02" }, completions.Select(item => item.Computer).OrderBy(name => name));
        Assert.All(completions, item => Assert.Equal(LogonQueryStatus.Error, item.Status));
    }

    private static readonly string EventXml = """
        <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
          <System>
            <EventID>4624</EventID>
            <TimeCreated SystemTime="2025-03-04T05:06:07Z" />
          </System>
          <EventData />
        </Event>
        """;

    private static async Task<List<LogonScanUpdate>> CollectAsync(
        ILogonEventScanner scanner,
        IEnumerable<string> computers)
    {
        var updates = new List<LogonScanUpdate>();
        await foreach (var update in scanner.ScanAsync(
            computers,
            DateTimeOffset.Parse("2025-03-04T00:00:00Z"),
            DateTimeOffset.Parse("2025-03-05T00:00:00Z"),
            CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static LogonComputerCompleted CompletionFor(
        IEnumerable<LogonScanUpdate> updates,
        string computer)
        => updates.OfType<LogonComputerCompleted>().Single(update => update.Computer == computer);

    private static IEnumerable<string> Throw(Exception exception)
        => throw exception;

    private static IEnumerable<string> WaitUntilCancelled(CancellationToken cancellationToken)
    {
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static IEnumerable<string> WaitForRelease(
        Action onStarted,
        Action onFinished,
        ManualResetEventSlim release,
        CancellationToken cancellationToken)
    {
        onStarted();
        try
        {
            release.Wait(cancellationToken);
        }
        finally
        {
            onFinished();
        }

        yield break;
    }

    private static IEnumerable<string> WaitForReleaseIgnoringCancellation(
        Action onStarted,
        ManualResetEventSlim release)
    {
        onStarted();
        release.Wait();
        yield break;
    }

    private static IEnumerable<string> SignalStarted(TaskCompletionSource started)
    {
        started.TrySetResult();
        yield break;
    }

    private static IEnumerable<string> YieldEventThenWait(
        string eventXml,
        CancellationToken cancellationToken)
    {
        yield return eventXml;
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class DelegateSource(
        Func<string, CancellationToken, IEnumerable<string>> readEventXml) : ILogonEventSource
    {
        public IEnumerable<string> ReadEventXml(
            string computer,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
            => readEventXml(computer, cancellationToken);
    }
}
