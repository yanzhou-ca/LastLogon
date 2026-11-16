using System.Globalization;
using LastLogon.Core;

namespace LastLogon.Tests;

/// <summary>Builders for login records and histories that the tests of the login times share.</summary>
internal static class TestHistory
{
    /// <summary>A UTC time written as text, for example "2026-10-15T16:25:45Z".</summary>
    public static DateTimeOffset T(string utc)
        => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static LoginRecord Login(string sid, string utc, int? session = 2, LoginLog log = LoginLog.System)
        => new(T(utc), LoginRecordKind.Login, sid, session, log);

    public static LoginRecord Logout(string sid, string utc, int? session = 2, LoginLog log = LoginLog.System)
        => new(T(utc), LoginRecordKind.Logout, sid, session, log);

    /// <summary>
    /// A history in which both logs were read completely and begin at <paramref name="begins"/>, or in which neither
    /// could be read when <paramref name="read"/> is false. The records are put newest first.
    /// </summary>
    public static LoginHistory Of(IEnumerable<LoginRecord> records, string? begins = "2026-05-12T12:00:00Z", bool anyone = true, bool read = true)
    {
        DateTimeOffset? start = begins is null ? null : T(begins);
        var logs = new[] { LoginLog.System, LoginLog.UserProfileService }
            .Select(log => read
                ? new LoginLogResult(log, LoginLogOutcome.Complete, start, null)
                : new LoginLogResult(log, LoginLogOutcome.Failed, null, "The event log could not be read."))
            .ToList();
        return new LoginHistory(records.OrderByDescending(record => record.TimeUtc).ToList(), logs, anyone);
    }
}
