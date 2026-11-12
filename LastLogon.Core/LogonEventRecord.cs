namespace LastLogon.Core;

public sealed record LogonEventRecord(
    string Computer,
    DateTimeOffset TimeUtc,
    int EventId,
    string EventName,
    string? AccountName,
    string? AccountDomain,
    string? AccountSid,
    int? LogonType,
    string? LogonTypeName,
    string? SourceAddress,
    IReadOnlyDictionary<string, string> Data);
