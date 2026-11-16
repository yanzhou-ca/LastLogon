using System.Xml.Linq;
using LastLogon.Core;

namespace LastLogon.Tests;

public class LoginRecordParserTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void Parse_ReadsAWinlogonLogon() => Assert.Equal(
        new LoginRecord(TestHistory.T("2026-10-15T16:25:45.5513703Z"), LoginRecordKind.Login, Sid, 2, LoginLog.System),
        LoginRecordParser.Parse(LoginXml.Winlogon(7001, "2026-10-15T16:25:45.5513703Z", Sid)));

    [Fact]
    public void Parse_ReadsAWinlogonLogoff() => Assert.Equal(
        new LoginRecord(TestHistory.T("2026-10-14T20:46:08Z"), LoginRecordKind.Logout, Sid, 2, LoginLog.System),
        LoginRecordParser.Parse(LoginXml.Winlogon(7002, "2026-10-14T20:46:08Z", Sid)));

    [Fact]
    public void Parse_ReadsAProfileServiceLogon() => Assert.Equal(
        new LoginRecord(TestHistory.T("2026-10-15T16:25:45Z"), LoginRecordKind.Login, Sid, 2, LoginLog.UserProfileService),
        LoginRecordParser.Parse(LoginXml.Profile(1, "2026-10-15T16:25:45Z", Sid)));

    [Fact]
    public void Parse_ReadsAProfileServiceLogoff() => Assert.Equal(
        new LoginRecord(TestHistory.T("2026-10-14T20:46:08Z"), LoginRecordKind.Logout, Sid, 2, LoginLog.UserProfileService),
        LoginRecordParser.Parse(LoginXml.Profile(3, "2026-10-14T20:46:08Z", Sid)));

    [Fact]
    public void Parse_SessionIsOptional() => Assert.Null(
        LoginRecordParser.Parse(LoginXml.Winlogon(7001, "2026-10-15T16:25:45Z", Sid, session: null)).Session);

    [Fact]
    public void Parse_NormalisesTheSidToUpperCase() => Assert.Equal(
        Sid, LoginRecordParser.Parse(LoginXml.Winlogon(7001, "2026-10-15T16:25:45Z", Sid.ToLowerInvariant())).Sid);

    // Review focus 5: an event that only shares an id with a login record
    [Theory]
    [InlineData("Service Control Manager", 7001)]
    [InlineData("Microsoft-Windows-Winlogon", 7000)]
    [InlineData("Microsoft-Windows-Winlogon", 1)]
    [InlineData("Microsoft-Windows-User Profiles Service", 7001)]
    [InlineData("Microsoft-Windows-User Profiles Service", 2)]
    [InlineData("Microsoft-Windows-User Profiles Service", 5)]
    public void Parse_RejectsEventsThatAreNotLoginRecords(string provider, int id)
        => Assert.Throws<FormatException>(() => LoginRecordParser.Parse(
            LoginXml.Build(provider, id, "2026-10-15T16:25:45Z", Sid, new[] { ("UserSid", Sid), ("Session", "2") })));

    [Fact]
    public void Parse_RejectsASidThatIsNotWellFormed() => Assert.Throws<FormatException>(
        () => LoginRecordParser.Parse(LoginXml.Winlogon(7001, "2026-10-15T16:25:45Z", "not-a-sid")));

    [Fact]
    public void Parse_RejectsMalformedXml() => Assert.Throws<FormatException>(() => LoginRecordParser.Parse("<Event"));

    [Fact]
    public void Parse_RejectsARecordWithoutATime() => Assert.Throws<FormatException>(
        () => LoginRecordParser.Parse(LoginXml.Winlogon(7001, "", Sid)));
}

/// <summary>Event XML in the two shapes the login records have (masked samples were taken from a real PC).</summary>
internal static class LoginXml
{
    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    public static string Build(string provider, int id, string timeUtc, string securityUserId, IEnumerable<(string Name, string Value)> data)
        => new XElement(EventNamespace + "Event",
                new XElement(EventNamespace + "System",
                    new XElement(EventNamespace + "Provider", new XAttribute("Name", provider)),
                    new XElement(EventNamespace + "EventID", id),
                    new XElement(EventNamespace + "TimeCreated", new XAttribute("SystemTime", timeUtc)),
                    new XElement(EventNamespace + "Security", new XAttribute("UserID", securityUserId))),
                new XElement(EventNamespace + "EventData",
                    data.Select(item => new XElement(EventNamespace + "Data", new XAttribute("Name", item.Name), item.Value))))
            .ToString(SaveOptions.DisableFormatting);

    /// <summary>The System log's Winlogon record: the user's SID is in the data, SYSTEM is the security user.</summary>
    public static string Winlogon(int eventId, string timeUtc, string sid, int? session = 2)
    {
        var data = new List<(string Name, string Value)>();
        if (session is not null)
        {
            data.Add(("TSId", session.Value.ToString()));
        }
        data.Add(("UserSid", sid));
        return Build("Microsoft-Windows-Winlogon", eventId, timeUtc, "S-1-5-18", data);
    }

    /// <summary>The User Profile Service record: the user's SID is the security user of the event.</summary>
    public static string Profile(int eventId, string timeUtc, string sid, int? session = 2)
    {
        var data = new List<(string Name, string Value)>();
        if (session is not null)
        {
            data.Add(("Session", session.Value.ToString()));
        }
        return Build("Microsoft-Windows-User Profiles Service", eventId, timeUtc, sid, data);
    }
}
