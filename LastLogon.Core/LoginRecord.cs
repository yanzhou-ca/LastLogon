namespace LastLogon.Core;

public enum LoginRecordKind { Login, Logout }

/// <summary>The two event logs the login and logout records are read from.</summary>
public enum LoginLog { System, UserProfileService }

/// <summary>One login or logout of one user, as Windows recorded it. <see cref="TimeUtc"/> is UTC.</summary>
/// <param name="Sid">The user's SID, normalised by <see cref="SidText.TryNormalize"/>.</param>
/// <param name="Session">The session number the record names, null when it has none.</param>
public sealed record LoginRecord(DateTimeOffset TimeUtc, LoginRecordKind Kind, string Sid, int? Session, LoginLog Log);

/// <summary>
/// Questions about login records. Every method takes the records of one user, in any order. The same login
/// shows up in both logs a second apart, so a login is only closed or open as a session, never as a single record.
/// </summary>
public static class LoginRecords
{
    /// <summary>
    /// The time of the newest login that has no later logout for the same session number, or null when
    /// every session was closed. A session without a number pairs only with other records without one.
    /// A logout at the same time as a login closes it.
    /// </summary>
    public static DateTimeOffset? NewestOpenLogin(IEnumerable<LoginRecord> records)
    {
        DateTimeOffset? newestOpen = null;
        foreach (var session in records.GroupBy(record => record.Session))
        {
            var latest = session.Max(record => record.TimeUtc);
            var closed = session.Any(record => record.TimeUtc == latest && record.Kind == LoginRecordKind.Logout);
            if (!closed && (newestOpen is null || latest > newestOpen))
            {
                newestOpen = latest;
            }
        }

        return newestOpen;
    }

    public static DateTimeOffset? NewestLogin(IEnumerable<LoginRecord> records) => Newest(records, LoginRecordKind.Login);

    public static DateTimeOffset? NewestLogout(IEnumerable<LoginRecord> records) => Newest(records, LoginRecordKind.Logout);

    private static DateTimeOffset? Newest(IEnumerable<LoginRecord> records, LoginRecordKind kind)
    {
        DateTimeOffset? newest = null;
        foreach (var record in records)
        {
            if (record.Kind == kind && (newest is null || record.TimeUtc > newest))
            {
                newest = record.TimeUtc;
            }
        }

        return newest;
    }
}
