namespace LastLogon.Core;

public enum ScanStatus { Ok, Offline, TimedOut, AccessDenied, NoProfile, Error }

public enum ActivitySource { NtUserDat, Folder }

public record ProfileResult(
    string Computer,
    string Username,
    DateTime? LastActivity,
    ActivitySource? Source,
    ScanStatus Status,
    string? Message)
{
    public int? DaysAgo => LastActivity is null ? null : (int)(DateTime.Now - LastActivity.Value).TotalDays;

    public string LastActivityText => LastActivity?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public string DaysAgoText => DaysAgo?.ToString() ?? "";
    public string SourceText => Source switch
    {
        ActivitySource.NtUserDat => "NTUSER.DAT",
        ActivitySource.Folder => "Folder time",
        _ => "",
    };

    /// <summary>
    /// True when the row names a profile folder that may exist, so that Explorer can be pointed
    /// at it. A computer that was reached but has no profile for the user has no such folder.
    /// </summary>
    public bool CanOpenFolder => Status != ScanStatus.NoProfile && !string.IsNullOrEmpty(Username);

    /// <summary>
    /// What happened, in words an operator reads. A time that only came from the folder is an
    /// approximation, so it is not allowed to read as a plain OK.
    /// </summary>
    public string ResultText => Status switch
    {
        ScanStatus.Ok when Source == ActivitySource.Folder => "Approximate",
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
