namespace LastLogon.Core;

public enum ScanStatus { Ok, Offline, AccessDenied, NoProfile, Error }

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
    public string StatusText => Message is null ? Status.ToString() : $"{Status}: {Message}";
}
