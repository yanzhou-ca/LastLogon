namespace LastLogon.Core;

public enum LoginLogOutcome
{
    /// <summary>Every login and logout record of the log was read.</summary>
    Complete,

    /// <summary>The read stopped at the bound on records, so the oldest are missing.</summary>
    CutShort,

    /// <summary>The time limit was reached after some records had been read. What was read is kept. A read that got nothing before the limit is <see cref="Failed"/>.</summary>
    TimedOut,

    /// <summary>The log could not be read. None of its records are used.</summary>
    Failed
}

/// <summary>How reading one of the two logs of a computer went.</summary>
/// <param name="Begins">
/// The time the log begins: its oldest record of any kind, because a log has recorded every login since then.
/// For a read that was cut short or timed out it is the oldest login or logout record that was read. Null when unknown.
/// </param>
/// <param name="Failure">What went wrong, for a log that could not be read, written for the person who runs the tool.</param>
public sealed record LoginLogResult(LoginLog Log, LoginLogOutcome Outcome, DateTimeOffset? Begins, string? Failure)
{
    public bool WasRead => Outcome != LoginLogOutcome.Failed;
}

/// <summary>The login and logout records of one computer, read from its two logs, with what is known about how far back they go.</summary>
/// <param name="Records">The records read, newest first. Only one user's when the read was for one SID.</param>
/// <param name="AnyRecordOfAnyone">
/// Whether the logs hold a login or logout record of any user. For a read of one user it is only looked up when that user has none.
/// </param>
public sealed record LoginHistory(IReadOnlyList<LoginRecord> Records, IReadOnlyList<LoginLogResult> Logs, bool AnyRecordOfAnyone)
{
    public bool AnyLogRead => Logs.Any(log => log.WasRead);

    /// <summary>The earliest time at which a log that was read begins, or null when none of them could tell.</summary>
    public DateTimeOffset? RecordsBegin
    {
        get
        {
            DateTimeOffset? earliest = null;
            foreach (var log in Logs)
            {
                if (log.WasRead && log.Begins is { } begins && (earliest is null || begins < earliest))
                {
                    earliest = begins;
                }
            }

            return earliest;
        }
    }

    /// <summary>A history for a computer whose logs could not be read at all.</summary>
    public static LoginHistory Unavailable(string reason) => new(
        Array.Empty<LoginRecord>(),
        new[]
        {
            new LoginLogResult(LoginLog.System, LoginLogOutcome.Failed, null, reason),
            new LoginLogResult(LoginLog.UserProfileService, LoginLogOutcome.Failed, null, reason)
        },
        false);
}

/// <summary>The boundary to the event logs of one computer. The Windows implementation is <see cref="WindowsLoginLogSource"/>.</summary>
public interface ILoginLogSource
{
    /// <summary>
    /// The login and logout records of the log as event XML, newest first, and only those of the user with
    /// <paramref name="sid"/> when one is given. Stops reading when the token is cancelled.
    /// </summary>
    IEnumerable<string> ReadRecordXml(string computer, LoginLog log, string? sid, CancellationToken cancellationToken);

    /// <summary>The time of the oldest record of the log, of any kind, or null when the log is empty.</summary>
    DateTimeOffset? ReadOldestRecordTime(string computer, LoginLog log, CancellationToken cancellationToken);
}

/// <summary>Reads the login history of a computer. <see cref="LoginHistoryReader"/> is the implementation.</summary>
public interface ILoginHistorySource
{
    /// <summary>
    /// Reads both logs of <paramref name="computer"/>, for one user when <paramref name="sid"/> is given, within
    /// <paramref name="limit"/>. Failures of a log are reported in the result. Throws only <see cref="OperationCanceledException"/>
    /// when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    LoginHistory Read(string computer, string? sid, TimeSpan limit, CancellationToken cancellationToken);
}
