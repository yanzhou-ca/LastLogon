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
}
