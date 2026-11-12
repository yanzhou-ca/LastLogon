using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace LastLogon.Core;

public sealed class WindowsLogonEventSource : ILogonEventSource
{
    private static readonly int[] SupportedEventIds = { 4624, 4625, 4634, 4647, 4800, 4801, 4672 };

    public IEnumerable<string> ReadEventXml(
        string computer,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var session = new EventLogSession(computer);
        var query = new EventLogQuery("Security", PathType.LogName, BuildXPath(fromUtc, toUtc))
        {
            Session = session
        };
        using var reader = new EventLogReader(query);
        using var cancellationRegistration = cancellationToken.Register(reader.CancelReading);

        EventRecord? record;
        while ((record = reader.ReadEvent()) is not null)
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record.ToXml();
            }
        }
    }

    public static string BuildXPath(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var eventIds = string.Join(" or ", SupportedEventIds.Select(id => $"EventID={id}"));
        var start = fromUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        var end = toUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

        return $"*[System[({eventIds}) and TimeCreated[@SystemTime >= '{start}' and @SystemTime <= '{end}']]]";
    }
}
