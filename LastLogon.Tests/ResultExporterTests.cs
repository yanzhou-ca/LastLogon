using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class ResultExporterTests
{
    static ProfileResult R(DateTime? t, string? msg = null)
        => new("pc1", "a,\"b\"", t, t is null ? null : ActivitySource.NtUserDat, ScanStatus.Ok, msg);

    [Fact]
    public void ToCsv_QuotesCommasAndQuotes()
    {
        var csv = ResultExporter.ToCsv(new[] { R(new DateTime(2025, 3, 4, 5, 6, 0)) });
        Assert.Contains("\"a,\"\"b\"\"\"", csv);
        Assert.Contains("2025-03-04 05:06", csv);
    }

    [Fact]
    public void ToCsv_NullDate_EmptyCell()
    {
        var line = ResultExporter.ToCsv(new[] { R(null) }).Split("\r\n")[1];
        Assert.Contains(",,,,Ok,", line);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData(" +1+1", "' +1+1")]
    [InlineData("\t@SUM(1)", "'\t@SUM(1)")]
    [InlineData("-1+1", "'-1+1")]
    public void ToCsv_PrefixesFormulaLeadingTextCells(string untrusted, string expected)
    {
        var row = new ProfileResult(untrusted, untrusted, null, null, ScanStatus.Error, untrusted);
        var line = ResultExporter.ToCsv(new[] { row }).Split("\r\n")[1];
        Assert.StartsWith($"{expected},{expected},,,,Error,{expected}", line);
    }

    [Fact]
    public void ToTabSeparated_HasHeaderRow()
        => Assert.StartsWith("Computer\tUsername\tLastProfileActivity", ResultExporter.ToTabSeparated(new[] { R(null) }));

    [Fact]
    public void ToLogonEventCsv_HasFixedHeaderAndEventFields()
    {
        var eventTimeUtc = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var row = new LogonEventRecord(
            "pc01",
            eventTimeUtc,
            4624,
            "Successful logon",
            "jdoe",
            "CORP",
            "S-1-5-21-100",
            2,
            "Interactive",
            "192.0.2.10",
            new Dictionary<string, string> { ["TargetUserName"] = "jdoe" });

        var csv = ResultExporter.ToLogonEventCsv(new[] { row });
        var lines = csv.Split("\r\n");
        var cells = lines[1].Split(',');

        Assert.Equal(
            "Computer,LocalTime,TimeUtc,EventId,EventName,AccountName,AccountDomain,AccountSid,LogonType,LogonTypeName,SourceAddress,Details",
            lines[0]);
        Assert.Equal(12, cells.Length);
        Assert.Equal("pc01", cells[0]);
        Assert.Equal(eventTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), cells[1]);
        Assert.Equal("2025-03-04T05:06:07.0000000+00:00", cells[2]);
        Assert.Equal("4624", cells[3]);
        Assert.Equal("Successful logon", cells[4]);
        Assert.Equal("jdoe", cells[5]);
        Assert.Equal("CORP", cells[6]);
        Assert.Equal("S-1-5-21-100", cells[7]);
        Assert.Equal("2", cells[8]);
        Assert.Equal("Interactive", cells[9]);
        Assert.Equal("192.0.2.10", cells[10]);
        Assert.Equal("\"{\"\"TargetUserName\"\":\"\"jdoe\"\"}\"", cells[11]);
    }

    [Fact]
    public void ToLogonEventCsv_QuotesCommasAndQuotesInFields()
    {
        var row = new LogonEventRecord(
            "pc01",
            new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero),
            4624,
            "Successful logon",
            "a,\"b\"",
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());

        var csv = ResultExporter.ToLogonEventCsv(new[] { row });

        Assert.Contains("\"a,\"\"b\"\"\"", csv);
    }

    [Fact]
    public void ToLogonEventCsv_ProtectsFormulaPrefixes()
    {
        var row = new LogonEventRecord(
            "pc01",
            new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero),
            4624,
            "Successful logon",
            "=1+1",
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());

        var dataRow = ResultExporter.ToLogonEventCsv(new[] { row }).Split("\r\n")[1];

        Assert.Contains(",'=1+1,", dataRow);
    }

    [Fact]
    public void ToLogonEventTabSeparated_HasHeaderAndSanitizesTabs()
    {
        var row = new LogonEventRecord(
            "pc\t01",
            new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero),
            4624,
            "Successful logon",
            "jdoe\tadmin",
            null,
            null,
            null,
            null,
            null,
            new Dictionary<string, string>());

        var lines = ResultExporter.ToLogonEventTabSeparated(new[] { row }).Split("\r\n");
        var cells = lines[1].Split('\t');

        Assert.StartsWith("Computer\tLocalTime\tTimeUtc", lines[0]);
        Assert.Equal(12, cells.Length);
        Assert.Equal("pc 01", cells[0]);
        Assert.Equal("jdoe admin", cells[5]);
    }
}
