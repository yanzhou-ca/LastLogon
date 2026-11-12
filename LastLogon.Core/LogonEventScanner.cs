using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LastLogon.Core;

public enum LogonQueryStatus
{
    Success,
    AccessDenied,
    Offline,
    TimedOut,
    Error
}

public abstract record LogonScanUpdate;

public sealed record LogonEventFound(LogonEventRecord Event) : LogonScanUpdate;

public sealed record LogonComputerCompleted(
    string Computer,
    LogonQueryStatus Status,
    string? Message,
    int EventCount) : LogonScanUpdate;

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
    private readonly ILogonEventSource _source;
    private readonly int _maxParallel;
    private readonly TimeSpan _perComputerTimeout;

    public LogonEventScanner(
        ILogonEventSource source,
        int maxParallel = 4,
        TimeSpan? perComputerTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallel);

        var timeout = perComputerTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(perComputerTimeout));
        }

        _source = source;
        _maxParallel = maxParallel;
        _perComputerTimeout = timeout;
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
                LogonQueryStatus.Error,
                "Scan cancelled before the query started.",
                0));
            return;
        }

        try
        {
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(scanCancellationToken);
            timeoutCancellation.CancelAfter(_perComputerTimeout);
            var eventCount = 0;
            var readTask = Task.Run<Exception?>(() =>
            {
                try
                {
                    foreach (var eventXml in _source.ReadEventXml(
                        computer,
                        fromUtc,
                        toUtc,
                        timeoutCancellation.Token))
                    {
                        timeoutCancellation.Token.ThrowIfCancellationRequested();
                        var record = LogonEventParser.Parse(computer, eventXml);
                        if (!writer.TryWrite(new LogonEventFound(record)))
                        {
                            return null;
                        }

                        Interlocked.Increment(ref eventCount);
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
                var readError = await readTask.WaitAsync(timeoutCancellation.Token);
                var (status, message) = readError is null
                    ? (LogonQueryStatus.Success, (string?)null)
                    : GetFailure(readError);
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    status,
                    message,
                    Volatile.Read(ref eventCount)));
            }
            catch (OperationCanceledException) when (scanCancellationToken.IsCancellationRequested)
            {
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    LogonQueryStatus.Error,
                    "Scan cancelled before the query finished.",
                    Volatile.Read(ref eventCount)));
                await readTask;
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                writer.TryWrite(new LogonComputerCompleted(
                    computer,
                    LogonQueryStatus.TimedOut,
                    $"Timed out after {_perComputerTimeout.TotalSeconds:0.##} seconds.",
                    Volatile.Read(ref eventCount)));
                await readTask;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static (LogonQueryStatus Status, string Message) GetFailure(Exception exception)
        => exception switch
        {
            UnauthorizedAccessException => (LogonQueryStatus.AccessDenied, "Access denied."),
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
