using System.Globalization;

namespace LastLogon.Core;

public enum ScanStatus { Ok, Offline, TimedOut, AccessDenied, NoProfile, Error }

/// <summary>Where the time of a row comes from: the profile file, the folder, or the login records of the event logs.</summary>
public enum ActivitySource { NtUserDat, Folder, LoginRecords }

/// <param name="Login">What the login records say about the row. Null for a row that was not read from them.</param>
public record ProfileResult(
    string Computer,
    string Username,
    DateTime? LastActivity,
    ActivitySource? Source,
    ScanStatus Status,
    string? Message,
    LoginInfo? Login = null)
{
    /// <summary>
    /// Whole days since the user was last on the computer. A user who is logged in is 0 days ago, however old the profile
    /// file is. Without login information it is counted from the time of the profile file.
    /// </summary>
    public int? DaysAgo => Login is { } login
        ? login.DaysAgo(DateTimeOffset.UtcNow)?.Days
        : LastActivity is null ? null : (int)(DateTime.Now - LastActivity.Value).TotalDays;

    public string LastActivityText => LastActivity?.ToString("yyyy-MM-dd HH:mm") ?? "";

    /// <summary>The days as text, with a plus sign when only a lower bound is known ("185+").</summary>
    public string DaysAgoText
    {
        get
        {
            if (Login is { } login)
            {
                return login.DaysAgo(DateTimeOffset.UtcNow) is { } days
                    ? days.Days.ToString(CultureInfo.InvariantCulture) + (days.AtLeast ? "+" : "")
                    : "";
            }

            return DaysAgo?.ToString() ?? "";
        }
    }

    public string SourceText => Source switch
    {
        ActivitySource.NtUserDat => "NTUSER.DAT",
        ActivitySource.Folder => "Folder time",
        ActivitySource.LoginRecords => "Login records",
        _ => "",
    };

    public string LastLoginText => Login?.LastLoginText ?? "";

    public string LoggedInNowText => Login?.LoggedInNowText(DateTimeOffset.UtcNow) ?? "";

    /// <summary>The last logout. For a row without login information it is the time of the profile file, as it always was.</summary>
    public string LastLogoutText => Login is { } login ? login.LastLogoutText : LastActivityText;

    public bool IsLoggedIn => Login?.LoggedIn == LoggedInState.Yes;

    /// <summary>The newest time the row knows of, in local time. It orders the rows.</summary>
    public DateTime? LastSeen => Login is { } login ? login.LastSeen : LastActivity;

    // The keys the columns sort by: what the column shows, as a time.
    public DateTime? LastLoginSort => Login?.LastLogin?.LocalDateTime;

    public DateTime? LastLogoutSort
        => Login is { } login ? login.LastLogoutRecord?.LocalDateTime ?? login.LastLogoutFile : LastActivity;

    public int LoggedInSort => Login?.LoggedIn switch
    {
        LoggedInState.Yes => 2,
        LoggedInState.No => 1,
        _ => 0
    };

    /// <summary>
    /// The order of the rows of a scan: users who are logged in first, then the newest time on the computer, then the
    /// rows that have no time. Rows that compare equal keep the order they arrived in when a stable sort is used.
    /// </summary>
    public static int CompareForDisplay(ProfileResult a, ProfileResult b)
    {
        var byLoggedIn = b.IsLoggedIn.CompareTo(a.IsLoggedIn);
        if (byLoggedIn != 0)
        {
            return byLoggedIn;
        }

        if (a.LastSeen is not { } seenA)
        {
            return b.LastSeen is null ? 0 : 1;
        }

        return b.LastSeen is { } seenB ? seenB.CompareTo(seenA) : -1;
    }

    /// <summary>
    /// True when the row names a profile folder that may exist, so that Explorer can be pointed
    /// at it. A computer that was reached but has no profile for the user has no such folder.
    /// </summary>
    public bool CanOpenFolder => Status != ScanStatus.NoProfile && !string.IsNullOrEmpty(Username);

    /// <summary>
    /// What happened, in words an operator reads. A time that only came from the profile file or the folder is an
    /// approximation, so it is not allowed to read as a plain OK.
    /// </summary>
    public string ResultText => Status switch
    {
        ScanStatus.Ok when Source == ActivitySource.Folder || Login?.IsApproximate == true => "Approximate",
        ScanStatus.Ok => "OK",
        ScanStatus.Offline => "Offline",
        ScanStatus.TimedOut => "Timed out",
        ScanStatus.AccessDenied => "Access denied",
        ScanStatus.NoProfile => "No profile",
        _ => "Error",
    };

    /// <summary>The full explanation behind <see cref="ResultText"/>, or nothing when there is none.</summary>
    public string DetailText => Message ?? "";
}
