using System.Diagnostics.Eventing.Reader;

namespace LastLogon.Core;

/// <summary>
/// Reads the System log and the User Profile Service log of a computer at the same time and puts their login
/// records together. A log that fails is reported and does not hide the other. Nothing is thrown except the
/// cancellation of the scan.
/// </summary>
public sealed class LoginHistoryReader : ILoginHistorySource
{
    private const uint RpcServerUnavailable = 0x800706BA;

    private static readonly LoginLog[] Logs = { LoginLog.System, LoginLog.UserProfileService };

    private readonly ILoginLogSource _source;
    private readonly int _maxRecordsPerLog;

    public LoginHistoryReader(ILoginLogSource source, int maxRecordsPerLog = 20_000)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _maxRecordsPerLog = maxRecordsPerLog;
    }

    public LoginHistory Read(string computer, string? sid, TimeSpan limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;

        var reads = Logs.Select(log => Task.Run(() => ReadLog(computer, log, sid, token))).ToArray();
        Task.WaitAll(reads);
        cancellationToken.ThrowIfCancellationRequested();

        var results = reads.Select(read => read.Result).ToList();
        var records = results.SelectMany(result => result.Records).OrderByDescending(record => record.TimeUtc).ToList();
        var anyone = records.Count > 0 || results.Any(result => result.SomeoneHasRecords);
        return new LoginHistory(records, results.Select(result => result.Log).ToList(), anyone);
    }

    private LogRead ReadLog(string computer, LoginLog log, string? sid, CancellationToken token)
    {
        var records = new List<LoginRecord>();
        try
        {
            var cutShort = false;
            foreach (var xml in _source.ReadRecordXml(computer, log, sid, token))
            {
                token.ThrowIfCancellationRequested();
                LoginRecord record;
                try
                {
                    record = LoginRecordParser.Parse(xml);
                }
                catch (FormatException)
                {
                    continue;
                }

                records.Add(record);
                if (records.Count >= _maxRecordsPerLog)
                {
                    cutShort = true;
                    break;
                }
            }

            var begins = cutShort ? OldestOf(records) : ReadBegin(computer, log, token);

            // The first record of a log is not always its oldest: a clock that stepped back leaves later records with
            // earlier times. A log does not begin after a record it holds.
            if (!cutShort && begins is { } first && OldestOf(records) is { } oldestRead && oldestRead < first)
            {
                begins = oldestRead;
            }

            // A user with no record of their own: is it that nobody has any, or that this user has none?
            var someoneHasRecords = false;
            if (sid is not null && records.Count == 0)
            {
                someoneHasRecords = _source.ReadRecordXml(computer, log, null, token).Any();
            }

            var outcome = cutShort ? LoginLogOutcome.CutShort : LoginLogOutcome.Complete;
            return new LogRead(new LoginLogResult(log, outcome, begins, null), records, someoneHasRecords);
        }
        catch (OperationCanceledException)
        {
            // Either the scan was cancelled, which Read reports, or the time limit of this read was reached.
            return new LogRead(new LoginLogResult(log, LoginLogOutcome.TimedOut, OldestOf(records), null), records, false);
        }
        catch (Exception ex)
        {
            return new LogRead(
                new LoginLogResult(log, LoginLogOutcome.Failed, null, Describe(ex)),
                Array.Empty<LoginRecord>(),
                false);
        }
    }

    // A failure of this one question leaves the begin of the log unknown and does not fail the log.
    private DateTimeOffset? ReadBegin(string computer, LoginLog log, CancellationToken token)
    {
        try
        {
            return _source.ReadOldestRecordTime(computer, log, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static DateTimeOffset? OldestOf(List<LoginRecord> records)
        => records.Count == 0 ? null : records.Min(record => record.TimeUtc);

    private static string Describe(Exception exception) => exception switch
    {
        UnauthorizedAccessException =>
            "Access denied. The account has no permission to read the event logs on this computer. "
            + "It has to be an administrator there or a member of Event Log Readers.",
        EventLogException ex when (uint)ex.HResult == RpcServerUnavailable =>
            "The event log of the computer could not be reached. Check that it is on and that the "
            + "Remote Event Log Management firewall rules are enabled on it.",
        _ => "The event log could not be read: " + exception.Message
    };

    private sealed record LogRead(LoginLogResult Log, IReadOnlyList<LoginRecord> Records, bool SomeoneHasRecords);
}
