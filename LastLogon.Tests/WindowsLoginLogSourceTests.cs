using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using LastLogon.Core;

namespace LastLogon.Tests;

public class WindowsLoginLogSourceTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void LogName_NamesTheTwoChannels()
    {
        Assert.Equal("System", WindowsLoginLogSource.LogName(LoginLog.System));
        Assert.Equal(
            "Microsoft-Windows-User Profile Service/Operational",
            WindowsLoginLogSource.LogName(LoginLog.UserProfileService));
    }

    [Fact]
    public void BuildRecordXPath_System_ForEveryUser() => Assert.Equal(
        "*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and (EventID=7001 or EventID=7002)]]",
        WindowsLoginLogSource.BuildRecordXPath(LoginLog.System, null));

    [Fact]
    public void BuildRecordXPath_System_ForOneUser() => Assert.Equal(
        $"*[System[Provider[@Name='Microsoft-Windows-Winlogon'] and (EventID=7001 or EventID=7002)] and EventData[Data[@Name='UserSid']='{Sid}']]",
        WindowsLoginLogSource.BuildRecordXPath(LoginLog.System, Sid));

    [Fact]
    public void BuildRecordXPath_ProfileService_ForEveryUser() => Assert.Equal(
        "*[System[(EventID=1 or EventID=3)]]",
        WindowsLoginLogSource.BuildRecordXPath(LoginLog.UserProfileService, null));

    [Fact]
    public void BuildRecordXPath_ProfileService_ForOneUser() => Assert.Equal(
        $"*[System[(EventID=1 or EventID=3) and Security[@UserID='{Sid}']]]",
        WindowsLoginLogSource.BuildRecordXPath(LoginLog.UserProfileService, Sid));

    [Fact]
    public void BuildRecordXPath_PutsTheNormalisedSidInTheQuery() => Assert.Equal(
        $"*[System[(EventID=1 or EventID=3) and Security[@UserID='{Sid}']]]",
        WindowsLoginLogSource.BuildRecordXPath(LoginLog.UserProfileService, " " + Sid.ToLowerInvariant() + "\r\n"));

    [Theory]
    [InlineData("S-1-5-21-1' or 'a'='a")]
    [InlineData("S-1-5-21-1-2-3-1001'] | //*[1")]
    [InlineData("")]
    [InlineData("jdoe")]
    public void BuildRecordXPath_RefusesTextThatIsNotASid(string text)
        => Assert.Throws<ArgumentException>(() => WindowsLoginLogSource.BuildRecordXPath(LoginLog.System, text));

    // The two tests below read this computer's own logs. A session without a computer name reads the local logs, so
    // the query, the reverse order and the parser meet real records. Zero records on a fresh machine is fine; an
    // exception is not.
    [Fact]
    public void ReadsTheLogsOfThisComputer_AndEveryRecordParses()
    {
        var source = new WindowsLoginLogSource(_ => new EventLogSession());
        foreach (var log in new[] { LoginLog.System, LoginLog.UserProfileService })
        {
            var records = source.ReadRecordXml("this-computer", log, null, CancellationToken.None)
                .Take(2000)
                .Select(LoginRecordParser.Parse)
                .ToList();
            Assert.All(records, record => Assert.Equal(log, record.Log));

            var times = records.Select(record => record.TimeUtc).ToList();
            Assert.Equal(times.OrderByDescending(time => time).ToList(), times);

            var oldest = source.ReadOldestRecordTime("this-computer", log, CancellationToken.None);
            Assert.True(oldest is null || oldest <= DateTimeOffset.UtcNow);
            if (times.Count > 0)
            {
                // The first record of the log is not newer than the newest login or logout record. With the direction
                // mixed up it would be the newest event of the log. (Not compared with the oldest login record: a clock
                // that stepped back can leave one older than the first record, as it did on the development PC.)
                Assert.NotNull(oldest);
                Assert.True(oldest <= times.Max());
            }
        }
    }

    [Fact]
    public void ReadsOnlyTheRecordsOfTheSidThatWasAsked()
    {
        var me = WindowsIdentity.GetCurrent().User!.Value;
        var source = new WindowsLoginLogSource(_ => new EventLogSession());

        var records = source.ReadRecordXml("this-computer", LoginLog.UserProfileService, me, CancellationToken.None)
            .Take(20)
            .Select(LoginRecordParser.Parse)
            .ToList();

        Assert.All(records, record => Assert.True(SidText.Same(record.Sid, me)));
    }

    [Fact]
    public void ReadRecordXml_StopsWhenTheTokenIsAlreadyCancelled()
    {
        var opened = false;
        var source = new WindowsLoginLogSource(_ =>
        {
            opened = true;
            return new EventLogSession();
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => source.ReadRecordXml("this-computer", LoginLog.System, null, cts.Token).ToList());
        Assert.False(opened);
    }
}
