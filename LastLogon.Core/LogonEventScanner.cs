using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LastLogon.Core;

public enum LogonQueryStatus
{
    Success,
    AccessDenied,
    Offline,
    TimedOut,
    Cancelled,
    Error
}

public abstract record LogonScanUpdate;

public sealed record LogonEventFound(LogonEventRecord Event) : LogonScanUpdate;

public sealed record LogonComputerCompleted(
    string Computer,
    LogonQueryStatus Status,
    string? Message,
    int EventCount,
    bool Truncated = false) : LogonScanUpdate
{
    /// <summary>What happened to this computer, in words that fit a narrow column.</summary>
    public string StatusText => Status switch
    {
        LogonQueryStatus.Success => "OK",
        LogonQueryStatus.AccessDenied => "Access denied",
        LogonQueryStatus.Offline => "Offline",
        LogonQueryStatus.TimedOut => "Timed out",
        LogonQueryStatus.Cancelled => "Cancelled",
        _ => "Error",
    };
}

public interface ILogonEventScanner
{
    IAsyncEnumerable<LogonScanUpdate> ScanAsync(
        IEnumerable<string> computers,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}

public sealed class LogonEventScanner : ILogonEventScanner
{
    /// <summary>The RPC error Windows returns when the remote Event Log service cannot be reached.</summary>
    private const uint RpcServerUnavailable = 0x800706BA;

    /// <summary>How long a reader that was interrupted may take to actually stop.</summary>
    private static readonly TimeSpan ReaderShutdownGrace = TimeSpan.FromSeconds(30);

    private readonly ILogonEventSource _source;
    private readonly int _maxParallel;
    private readonly TimeSpan _perComputerTimeout;
    private readonly int _maxEventsPerComputer;

    public LogonEventScanner(
        ILogonEventSource source,
        int maxParallel = 4,
        TimeSpan? perComputerTimeout = null,
        int maxEventsPerComputer = 50_000)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEventsPerComputer);

        var timeout = perComputerTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perComputerTimeout));
        }

        _source = source;
        _maxParallel = maxParallel;
        _perComputerTimeout = timeout;
        _maxEventsPerComputer = maxEventsPerComputer;
    }

    public async IAsyncEnumerable<LogonScanUpdate> ScanAsync(
        IEnumerable<string> computers,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(computers);

        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var channel = Channel.CreateUnbounded<LogonScanUpdate>();
        var computerTasks = computers
            .Select(computer => ScanComputerAsync(computer, fromUtc, toUtc, gate, channel.Writer, scanCancellation.Token))
            .ToArray();
        var completionTask = CompleteWriterAsync(computerTasks, channel.Writer);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                yield return update;
            }

            await completionTask;
        }
        finally
        {
            scanCancellation.Cancel();
            await completionTask;
        }
    }

    private async Task ScanComputerAsync(
        string computer,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        SemaphoreSlim gate,
        ChannelWriter<LogonScanUpdate> writer,
        CancellationToken scanCancellationToken)
    {
        try
        {
            await gate.WaitAsync(scanCancellationToken);
        }
        catch (OperationCanceledException) when (scanCancellationToken.IsCancellationRequested)
        {
            writer.TryWrite(new LogonComputerCompleted(
                computer,
                LogonQueryStatus.Cancelled,
                "Scan cancelled before the query started.",
                0));
            return;
        }

        try
        {
            // A separate source for the timeout keeps "the query ran out of time" apart from
            // "the operator cancelled the scan": both cancel the same linked token.
            using var timeoutSource = new CancellationTokenSource(_perComputerTimeout);
            using var queryCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(scanCancellationToken, timeoutSource.Token);

            var eventCount = 0;
            var truncated = 0;
            var skipped = 0;

            var readTask = Task.Run<Exception?>(() =>
            {
                try
                {
                    foreach (var eventXml in _source.ReadEventXml(
                        computer,
                        fromUtc,
                        toUtc,
                        queryCancellation.Token))
                    {
                        queryCancellation.Token.ThrowIfCancellationRequested();

                        LogonEventRecord record;
                        try
                        {
                            record = LogonEventParser.Parse(computer, eventXml);
                        }
                        catch (FormatException)
                        {
                            // One record that cannot be read must not throw away the rest of
                            // the machine's history.
                            Interlocked.Increment(ref skipped);
                            continue;
                        }

                        if (!writer.TryWrite(new LogonEventFound(record)))
                        {
                            return null;
                        }

                        Interlocked.Increment(ref eventCount);

                        // Stop reading a host that floods the log. Leaving the enumeration here
                        // disposes the reader, which ends the remote query.
                        if (Volatile.Read(ref eventCount) >= _maxEventsPerComputer)
                        {
                            Volatile.Write(ref truncated, 1);
                            break;
                        }
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }, CancellationToken.None);

            try
            {
                var readError = await readTask.WaitAsync(queryCancellation.Token);
                var (status, message) = readError is null
                    ? (LogonQueryStatus.Success, (string?)null)
                    : GetFailure(
                        readError,
                        timedOut: timeoutSource.IsCancellationRequested,
                        cancelled: scanCancellationToken.IsCancellationRequested);
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    status,
                    Describe(message, Volatile.Read(ref skipped), Volatile.Read(ref truncated) == 1),
                    Volatile.Read(ref eventCount),
                    Volatile.Read(ref truncated) == 1));
            }
            catch (OperationCanceledException) when (scanCancellationToken.IsCancellationRequested)
            {
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    LogonQueryStatus.Cancelled,
                    "Scan cancelled before the query finished.",
                    Volatile.Read(ref eventCount),
                    Volatile.Read(ref truncated) == 1));
                await WaitForReaderAsync(readTask);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    LogonQueryStatus.TimedOut,
                    $"Timed out after {_perComputerTimeout.TotalSeconds:0.##} seconds.",
                    Volatile.Read(ref eventCount),
                    Volatile.Read(ref truncated) == 1));
                await WaitForReaderAsync(readTask);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Waits for a reader that was interrupted to actually stop, but only for a bounded grace
    /// period. The slot is held until then so that the number of live queries stays bounded, and
    /// a host that never answers cannot keep the scan waiting forever.
    /// </summary>
    private static async Task WaitForReaderAsync(Task readTask)
    {
        try
        {
            await readTask.WaitAsync(ReaderShutdownGrace);
        }
        catch (TimeoutException)
        {
            // The reader is stuck in a call that cannot be interrupted. The read task observes
            // its own exception, so it is safe to leave it running.
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Adds the caveats that make a result set easier to read correctly: records that could not
    /// be parsed and a result set that was cut short.
    /// </summary>
    private static string? Describe(string? message, int skipped, bool truncated)
    {
        var notes = new List<string>();
        if (message is not null) notes.Add(message);
        if (skipped > 0) notes.Add($"{skipped} record(s) could not be read and were skipped.");
        if (truncated) notes.Add("The event limit was reached; the results are incomplete.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private static (LogonQueryStatus Status, string Message) GetFailure(
        Exception exception,
        bool timedOut,
        bool cancelled)
        => exception switch
        {
            UnauthorizedAccessException => (
                LogonQueryStatus.AccessDenied,
                "Access denied. The account has no permission to read the Security log on this computer. "
                + "It has to be an administrator there or a member of Event Log Readers."),
            OperationCanceledException when cancelled => (LogonQueryStatus.Cancelled, "Scan cancelled."),
            OperationCanceledException when timedOut => (LogonQueryStatus.TimedOut, "The query timed out."),
            EventLogException ex when (uint)ex.HResult == RpcServerUnavailable =>
                (LogonQueryStatus.Offline,
                    "The computer is unreachable. Check that it is on and that the Remote Event Log "
                    + "Management firewall rules are enabled on it."),
            EventLogException ex => (LogonQueryStatus.Error, ex.Message),
            DirectoryNotFoundException => (LogonQueryStatus.Offline, "Computer or Security log was not found."),
            IOException => (LogonQueryStatus.Offline, exception.Message),
            TimeoutException => (LogonQueryStatus.TimedOut, exception.Message),
            _ => (LogonQueryStatus.Error, exception.Message)
        };

    private static async Task CompleteWriterAsync(
        IEnumerable<Task> computerTasks,
        ChannelWriter<LogonScanUpdate> writer)
    {
        try
        {
            await Task.WhenAll(computerTasks);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }
}
