using System.Diagnostics.Eventing.Reader;

namespace LastLogon.Core;

/// <summary>
/// Reads the login and logout records from the System log and the User Profile Service log of a computer, over
/// the same event-log connection that Logon history uses.
/// </summary>
public sealed class WindowsLoginLogSource : ILoginLogSource
{
    private readonly Func<string, EventLogSession> _openSession;

    // The host is addressed the way the SMB scan addresses it, so that a short name cannot be resolved to a
    // different machine by short-name resolution.
    public WindowsLoginLogSource()
        : this(computer => new EventLogSession(ComputerTarget.Qualify(computer)))
    {
    }

    /// <summary>For tests: a session factory that reads the logs of this computer.</summary>
    internal WindowsLoginLogSource(Func<string, EventLogSession> openSession)
    {
        _openSession = openSession ?? throw new ArgumentNullException(nameof(openSession));
    }

    public static string LogName(LoginLog log) => log switch
    {
        LoginLog.System => "System",
        LoginLog.UserProfileService => "Microsoft-Windows-User Profile Service/Operational",
        _ => throw new ArgumentOutOfRangeException(nameof(log))
    };

    /// <summary>
    /// The filter for the login and logout records of a log, for one user when <paramref name="sid"/> is given.
    /// The SID goes into the query, so it must parse as a SID: anything else is refused with an
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static string BuildRecordXPath(LoginLog log, string? sid)
    {
        var user = string.Empty;
        if (sid is not null)
        {
            if (!SidText.TryNormalize(sid, out user))
            {
                throw new ArgumentException("The text is not a SID.", nameof(sid));
            }
        }

        var forOneUser = sid is not null;
        return log switch
        {
            LoginLog.System => forOneUser
                ? $"*[System[Provider[@Name='{LoginRecordParser.WinlogonProvider}'] and (EventID=7001 or EventID=7002)] and EventData[Data[@Name='UserSid']='{user}']]"
                : $"*[System[Provider[@Name='{LoginRecordParser.WinlogonProvider}'] and (EventID=7001 or EventID=7002)]]",
            LoginLog.UserProfileService => forOneUser
                ? $"*[System[(EventID=1 or EventID=3) and Security[@UserID='{user}']]]"
                : "*[System[(EventID=1 or EventID=3)]]",
            _ => throw new ArgumentOutOfRangeException(nameof(log))
        };
    }

    public IEnumerable<string> ReadRecordXml(string computer, LoginLog log, string? sid, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var xpath = BuildRecordXPath(log, sid);
        using var session = _openSession(computer);
        var query = new EventLogQuery(LogName(log), PathType.LogName, xpath)
        {
            Session = session,
            ReverseDirection = true
        };
        using var reader = new EventLogReader(query);
        using var cancellationRegistration = cancellationToken.Register(reader.CancelReading);

        EventRecord? record;
        while ((record = reader.ReadEvent()) is not null)
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record.ToXml();
            }
        }
    }

    public DateTimeOffset? ReadOldestRecordTime(string computer, LoginLog log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var session = _openSession(computer);
        var query = new EventLogQuery(LogName(log), PathType.LogName, "*")
        {
            Session = session,
            ReverseDirection = false
        };
        using var reader = new EventLogReader(query);
        using var cancellationRegistration = cancellationToken.Register(reader.CancelReading);

        using var record = reader.ReadEvent();
        return record?.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime()) : null;
    }
}
