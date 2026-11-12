using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LastLogon.Core;

public static class LogonEventParser
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static LogonEventRecord Parse(string computer, string eventXml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(eventXml);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Event XML is malformed.", ex);
        }

        var root = document.Root ?? throw new FormatException("Event XML has no root element.");
        var system = root.Element(EventNamespace + "System")
            ?? throw new FormatException("Event XML has no System element.");
        var eventId = int.Parse(
            RequiredValue(system.Element(EventNamespace + "EventID")?.Value, "EventID"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);
        var timeText = RequiredValue(
            system.Element(EventNamespace + "TimeCreated")?.Attribute("SystemTime")?.Value,
            "TimeCreated SystemTime");
        var timeUtc = DateTimeOffset.Parse(
            timeText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var eventData = root.Element(EventNamespace + "EventData");
        if (eventData is not null)
        {
            foreach (var item in eventData.Elements(EventNamespace + "Data"))
            {
                var name = item.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name))
                {
                    data[name] = item.Value;
                }
            }
        }

        var logonType = ParseOptionalInt(data, "LogonType");

        return new LogonEventRecord(
            computer,
            timeUtc,
            eventId,
            GetEventName(eventId),
            GetPreferredValue(data, "TargetUserName", "SubjectUserName"),
            GetPreferredValue(data, "TargetDomainName", "SubjectDomainName"),
            GetPreferredValue(data, "TargetUserSid", "SubjectUserSid"),
            logonType,
            GetLogonTypeName(logonType),
            GetValue(data, "IpAddress"),
            data);
    }

    private static string RequiredValue(string? value, string fieldName)
        => value ?? throw new FormatException($"Event XML is missing {fieldName}.");

    private static int? ParseOptionalInt(IReadOnlyDictionary<string, string> data, string key)
    {
        var value = GetValue(data, key);
        return string.IsNullOrEmpty(value)
            ? null
            : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static string? GetPreferredValue(
        IReadOnlyDictionary<string, string> data,
        string preferredKey,
        string fallbackKey)
    {
        var preferred = GetValue(data, preferredKey);
        return string.IsNullOrWhiteSpace(preferred) ? GetValue(data, fallbackKey) : preferred;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var value) ? value : null;

    private static string GetEventName(int eventId)
        => eventId switch
        {
            4624 => "Successful logon",
            4625 => "Failed logon",
            4634 => "Logoff",
            4647 => "User-initiated logoff",
            4672 => "Special logon",
            4800 => "Workstation locked",
            4801 => "Workstation unlocked",
            _ => $"Unknown event ({eventId})"
        };

    private static string? GetLogonTypeName(int? logonType)
        => logonType switch
        {
            null => null,
            0 => "System",
            2 => "Interactive",
            3 => "Network",
            4 => "Batch",
            5 => "Service",
            7 => "Unlock",
            8 => "Network cleartext",
            9 => "New credentials",
            10 => "Remote interactive",
            11 => "Cached interactive",
            12 => "Cached remote interactive",
            13 => "Cached unlock",
            _ => $"Unknown ({logonType.Value})"
        };
}
