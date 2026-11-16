using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace LastLogon.Core;

/// <summary>Reads one login or logout record from the XML of an event of the System or the User Profile Service log.</summary>
public static class LoginRecordParser
{
    public const string WinlogonProvider = "Microsoft-Windows-Winlogon";

    // The provider says "Profiles" and the channel says "Profile": checked on a real record.
    public const string ProfileServiceProvider = "Microsoft-Windows-User Profiles Service";

    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>
    /// Winlogon 7001 and 7002 (SID in the data as UserSid, session as TSId) and User Profile Service 1 and 3
    /// (SID in the security user of the event, session as Session). Any other event, a SID that is not well formed
    /// and a missing time throw <see cref="FormatException"/>.
    /// </summary>
    public static LoginRecord Parse(string eventXml)
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
        var provider = system.Element(EventNamespace + "Provider")?.Attribute("Name")?.Value;
        if (!int.TryParse(system.Element(EventNamespace + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            throw new FormatException("Event XML has no EventID.");
        }

        var (log, kind) = (provider, eventId) switch
        {
            (WinlogonProvider, 7001) => (LoginLog.System, LoginRecordKind.Login),
            (WinlogonProvider, 7002) => (LoginLog.System, LoginRecordKind.Logout),
            (ProfileServiceProvider, 1) => (LoginLog.UserProfileService, LoginRecordKind.Login),
            (ProfileServiceProvider, 3) => (LoginLog.UserProfileService, LoginRecordKind.Logout),
            _ => throw new FormatException($"Event {eventId} of {provider} is not a login record.")
        };

        var rawSid = log == LoginLog.System
            ? DataValue(root, "UserSid")
            : system.Element(EventNamespace + "Security")?.Attribute("UserID")?.Value;
        if (!SidText.TryNormalize(rawSid, out var sid))
        {
            throw new FormatException("Event XML has no usable user SID.");
        }

        var rawSession = DataValue(root, log == LoginLog.System ? "TSId" : "Session");
        int? session = int.TryParse(rawSession, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

        var timeText = system.Element(EventNamespace + "TimeCreated")?.Attribute("SystemTime")?.Value;
        if (!DateTimeOffset.TryParse(
                timeText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timeUtc))
        {
            throw new FormatException("Event XML is missing TimeCreated SystemTime.");
        }

        return new LoginRecord(timeUtc, kind, sid, session, log);
    }

    private static string? DataValue(XElement root, string name)
        => root.Element(EventNamespace + "EventData")?
            .Elements(EventNamespace + "Data")
            .FirstOrDefault(item => (string?)item.Attribute("Name") == name)?.Value;
}
