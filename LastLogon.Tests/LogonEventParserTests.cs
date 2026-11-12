using LastLogon.Core;

namespace LastLogon.Tests;

public class LogonEventParserTests
{
    [Fact]
    public void Parse_4624_MapsTargetAccountLogonTypeAndAddress()
    {
        const string eventXml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <EventID>4624</EventID>
                <TimeCreated SystemTime="2025-03-04T05:06:07.0000000Z" />
              </System>
              <EventData>
                <Data Name="TargetUserName">jdoe</Data>
                <Data Name="TargetDomainName">CORP</Data>
                <Data Name="TargetUserSid">S-1-5-21-100</Data>
                <Data Name="LogonType">2</Data>
                <Data Name="IpAddress">192.0.2.10</Data>
              </EventData>
            </Event>
            """;

        var result = LogonEventParser.Parse("pc01", eventXml);

        Assert.Equal("pc01", result.Computer);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero), result.TimeUtc);
        Assert.Equal(4624, result.EventId);
        Assert.Equal("Successful logon", result.EventName);
        Assert.Equal("jdoe", result.AccountName);
        Assert.Equal("CORP", result.AccountDomain);
        Assert.Equal("S-1-5-21-100", result.AccountSid);
        Assert.Equal(2, result.LogonType);
        Assert.Equal("Interactive", result.LogonTypeName);
        Assert.Equal("192.0.2.10", result.SourceAddress);
        Assert.Equal("jdoe", result.Data["TargetUserName"]);
        Assert.Equal("CORP", result.Data["TargetDomainName"]);
    }

    [Fact]
    public void Parse_4672_FallsBackToSubjectAccount()
    {
        const string eventXml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <EventID>4672</EventID>
                <TimeCreated SystemTime="2025-03-04T05:06:07Z" />
              </System>
              <EventData>
                <Data Name="SubjectUserName">svc-admin</Data>
                <Data Name="SubjectDomainName">CORP</Data>
                <Data Name="SubjectUserSid">S-1-5-21-200</Data>
              </EventData>
            </Event>
            """;

        var result = LogonEventParser.Parse("pc01", eventXml);

        Assert.Equal("svc-admin", result.AccountName);
        Assert.Equal("CORP", result.AccountDomain);
        Assert.Equal("S-1-5-21-200", result.AccountSid);
    }

    [Theory]
    [InlineData(4624, "Successful logon")]
    [InlineData(4625, "Failed logon")]
    [InlineData(4634, "Logoff")]
    [InlineData(4647, "User-initiated logoff")]
    [InlineData(4800, "Workstation locked")]
    [InlineData(4801, "Workstation unlocked")]
    [InlineData(4672, "Special logon")]
    public void Parse_MapsAllSupportedEventNames(int eventId, string expectedName)
    {
        var eventXml = EventXml(eventId);

        var result = LogonEventParser.Parse("pc01", eventXml);

        Assert.Equal(expectedName, result.EventName);
    }

    [Fact]
    public void Parse_EventWithMissingOptionalFields_LeavesValuesEmpty()
    {
        var result = LogonEventParser.Parse("pc01", EventXml(4624));

        Assert.Null(result.AccountName);
        Assert.Null(result.SourceAddress);
        Assert.Null(result.LogonType);
    }

    [Fact]
    public void Parse_UnknownLogonType_PreservesNumber()
    {
        const string eventXml = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <EventID>4624</EventID>
                <TimeCreated SystemTime="2025-03-04T05:06:07Z" />
              </System>
              <EventData>
                <Data Name="LogonType">99</Data>
              </EventData>
            </Event>
            """;

        var result = LogonEventParser.Parse("pc01", eventXml);

        Assert.Equal(99, result.LogonType);
        Assert.Contains("Unknown", result.LogonTypeName);
    }

    [Fact]
    public void Parse_InvalidXml_ThrowsFormatException()
        => Assert.Throws<FormatException>(() => LogonEventParser.Parse("pc01", "<Event>"));

    private static string EventXml(int eventId)
        => $$"""
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <EventID>{{eventId}}</EventID>
                <TimeCreated SystemTime="2025-03-04T05:06:07Z" />
              </System>
              <EventData />
            </Event>
            """;
}
