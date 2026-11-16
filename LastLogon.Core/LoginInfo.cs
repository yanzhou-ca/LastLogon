using System.Globalization;

namespace LastLogon.Core;

public enum LoggedInState { Unknown, No, Yes }

/// <summary>
/// What a profile row says about logins: whether the user is logged in now and since when, the last login and logout,
/// and where each time came from. <see cref="LoginAssessor"/> decides the values, this type turns them into the text
/// and the number of days of the row. Every time is a record time unless it is <see cref="LastLogoutFile"/>.
/// </summary>
/// <param name="LoggedInSince">The newest open login, only with <see cref="LoggedInState.Yes"/>. Null when the time is unknown.</param>
/// <param name="LastLogin">The time of the newest login record.</param>
/// <param name="LastLogoutRecord">The time of the newest logout record.</param>
/// <param name="LastLogoutFile">
/// The time of the profile file, local, set only when it is shown as the last logout. The row is then approximate.
/// </param>
/// <param name="RecordsBegin">The date the records begin. Set only when <paramref name="RecordsUsed"/>.</param>
/// <param name="RecordsUsed">Whether the login records could be used for this row.</param>
/// <param name="Details">The sentences of the Details column about what the row rests on.</param>
public sealed record LoginInfo(
    LoggedInState LoggedIn,
    DateTimeOffset? LoggedInSince,
    DateTimeOffset? LastLogin,
    DateTimeOffset? LastLogoutRecord,
    DateTime? LastLogoutFile,
    DateTimeOffset? RecordsBegin,
    bool RecordsUsed,
    string Details)
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>A time of the row comes from the profile file instead of the records.</summary>
    public bool IsApproximate => LastLogoutFile is not null;

    /// <summary>The newest login in local time, "Before 2026-05-12" when there is none since the records begin, blank when the records were not used.</summary>
    public string LastLoginText
    {
        get
        {
            if (LastLogin is { } login)
            {
                return Local(login, TimeFormat);
            }

            return RecordsUsed && RecordsBegin is { } begin ? "Before " + Local(begin, DateFormat) : string.Empty;
        }
    }

    /// <summary>The logout record, else the time of the profile file when it is shown as the logout.</summary>
    public string LastLogoutText
    {
        get
        {
            if (LastLogoutRecord is { } record)
            {
                return Local(record, TimeFormat);
            }

            return FileTime is { } file ? Local(file, TimeFormat) : string.Empty;
        }
    }

    /// <summary>"Yes, 29 days", "Yes, over 185 days" when the login is older than the records, "Yes", "No", or blank when unknown.</summary>
    public string LoggedInNowText(DateTimeOffset now) => LoggedIn switch
    {
        LoggedInState.Yes when LoggedInSince is { } since => "Yes, " + Duration(now - since),
        LoggedInState.Yes when RecordsUsed && RecordsBegin is { } begin => "Yes, over " + Duration(now - begin),
        LoggedInState.Yes => "Yes",
        LoggedInState.No => "No",
        _ => string.Empty
    };

    /// <summary>
    /// Whole days since the user was last known to be on the computer: 0 when logged in, otherwise since the newest of
    /// the login, the logout and the file time the row shows. When the row shows none of them, the days since the
    /// records begin, as a lower bound. Null when nothing is known.
    /// </summary>
    public (int Days, bool AtLeast)? DaysAgo(DateTimeOffset now)
    {
        if (LoggedIn == LoggedInState.Yes)
        {
            return (0, false);
        }

        if (Newest(LastLogin, LastLogoutRecord, FileTime) is { } seen)
        {
            return (WholeDays(now - seen), false);
        }

        return RecordsUsed && RecordsBegin is { } begin ? (WholeDays(now - begin), true) : null;
    }

    /// <summary>The newest of the exact times the row holds, in local time. It orders the rows.</summary>
    public DateTime? LastSeen => Newest(LoggedInSince, LastLogin, LastLogoutRecord, FileTime)?.LocalDateTime;

    private DateTimeOffset? FileTime => LastLogoutFile is { } file ? new DateTimeOffset(file) : null;

    private static string Local(DateTimeOffset time, string format)
        => time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    // Durations are counted in UTC and never below zero: a login that lies in the future because of a clock
    // difference counts as now, and a change of daylight saving time does not shift them.
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalDays >= 1)
        {
            return Plural.Count((int)span.TotalDays, "day");
        }

        return span.TotalHours >= 1
            ? Plural.Count((int)span.TotalHours, "hour")
            : Plural.Count((int)span.TotalMinutes, "minute");
    }

    private static int WholeDays(TimeSpan span) => (int)Math.Max(0, span.TotalDays);

    private static DateTimeOffset? Newest(params DateTimeOffset?[] times)
    {
        DateTimeOffset? newest = null;
        foreach (var time in times)
        {
            if (time is { } candidate && (newest is null || candidate > newest))
            {
                newest = candidate;
            }
        }

        return newest;
    }
}
