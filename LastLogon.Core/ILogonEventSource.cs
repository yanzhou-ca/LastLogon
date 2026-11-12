namespace LastLogon.Core;

public interface ILogonEventSource
{
    IEnumerable<string> ReadEventXml(
        string computer,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
