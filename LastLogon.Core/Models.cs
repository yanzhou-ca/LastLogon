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
    public string SourceText => Source?.ToString() ?? "";

    /// <summary>
    /// True when the row names a profile folder that may exist, so that Explorer can be pointed
    /// at it. A computer that was reached but has no profile for the user has no such folder.
    /// </summary>
    public bool CanOpenFolder => Status != ScanStatus.NoProfile && !string.IsNullOrEmpty(Username);
    public string StatusText => Message is null ? Status.ToString() : $"{Status}: {Message}";
}
