using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using LastLogon.Core;

namespace LastLogon.Tests;

public class LoginHistoryReaderTests
{
    private const string Jdoe = "S-1-5-21-1-2-3-1001";
    private const string Asmith = "S-1-5-21-1-2-3-1002";

    private static DateTimeOffset T(string utc) => TestHistory.T(utc);

    private static LoginHistory Read(FakeLogSource source, string? sid = null, int max = 1000, CancellationToken ct = default)
        => new LoginHistoryReader(source, max).Read("pc1", sid, TimeSpan.FromSeconds(5), ct);

    private static string[] SystemRecords(params (int Id, string Time)[] items)
        => items.Select(item => LoginXml.Winlogon(item.Id, item.Time, Jdoe)).ToArray();

    private static string[] ProfileRecords(params (int Id, string Time)[] items)
        => items.Select(item => LoginXml.Profile(item.Id, item.Time, Jdoe)).ToArray();

    private static LoginLogResult Of(LoginHistory history, LoginLog log) => history.Logs.Single(result => result.Log == log);

    [Fact]
    public void Read_MergesTheTwoLogsNewestFirst()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? SystemRecords((7001, "2026-10-15T16:25:45Z"), (7002, "2026-10-14T20:46:08Z"))
                : ProfileRecords((1, "2026-10-15T16:25:46Z"), (3, "2026-10-14T20:46:07Z"))
        };

        var history = Read(source);

        Assert.Equal(
            new[] { T("2026-10-15T16:25:46Z"), T("2026-10-15T16:25:45Z"), T("2026-10-14T20:46:08Z"), T("2026-10-14T20:46:07Z") },
            history.Records.Select(record => record.TimeUtc));
    }

    [Fact]
    public void Read_RecordsBegin_IsTheEarliestBeginOfTheLogsThatWereRead()
    {
        var source = new FakeLogSource
        {
            Oldest = log => log == LoginLog.System ? T("2026-10-01T00:00:00Z") : T("2026-05-12T12:00:00Z")
        };

        var history = Read(source);

        Assert.Equal(T("2026-05-12T12:00:00Z"), history.RecordsBegin);
        Assert.All(history.Logs, result => Assert.Equal(LoginLogOutcome.Complete, result.Outcome));
    }

    [Fact]
    public void Read_ALogThatCouldNotBeRead_DoesNotMoveTheBegin()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System ? throw new InvalidOperationException("boom") : Array.Empty<string>(),
            Oldest = log => log == LoginLog.System ? T("2026-01-01T00:00:00Z") : T("2026-10-01T00:00:00Z")
        };

        var history = Read(source);

        Assert.Equal(T("2026-10-01T00:00:00Z"), history.RecordsBegin);
        Assert.Equal(LoginLogOutcome.Failed, Of(history, LoginLog.System).Outcome);
    }

    [Fact]
    public void Read_WhenTheOldestRecordCannotBeRead_TheLogBeginsAtAnUnknownTime()
    {
        var onlySystemFails = new FakeLogSource
        {
            Oldest = log => log == LoginLog.System ? throw new InvalidOperationException("no oldest") : T("2026-10-01T00:00:00Z")
        };

        var history = Read(onlySystemFails);

        Assert.Equal(LoginLogOutcome.Complete, Of(history, LoginLog.System).Outcome);
        Assert.Null(Of(history, LoginLog.System).Begins);
        Assert.Equal(T("2026-10-01T00:00:00Z"), history.RecordsBegin);

        var bothFail = new FakeLogSource { Oldest = _ => throw new InvalidOperationException("no oldest") };
        Assert.Null(Read(bothFail).RecordsBegin);
    }

    [Fact]
    public void Read_WhenAReadIsCutShort_TheLogBeginsAtTheOldestRecordRead()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? SystemRecords((7001, "2026-10-15T10:00:00Z"), (7002, "2026-10-14T10:00:00Z"), (7001, "2026-10-13T10:00:00Z"))
                : Array.Empty<string>(),
            Oldest = _ => T("2020-01-01T00:00:00Z")
        };

        var history = Read(source, max: 2);

        var system = Of(history, LoginLog.System);
        Assert.Equal(2, history.Records.Count);
        Assert.Equal(LoginLogOutcome.CutShort, system.Outcome);
        Assert.Equal(T("2026-10-14T10:00:00Z"), system.Begins);
        Assert.DoesNotContain(LoginLog.System, source.OldestReads);
        Assert.Contains(LoginLog.UserProfileService, source.OldestReads);
    }

    // Review focus 5: a record that is malformed, and an event of another provider that shares the id 7001
    [Fact]
    public void Read_SkipsARecordThatCannotBeRead()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? new[]
                {
                    LoginXml.Winlogon(7001, "2026-10-15T10:00:00Z", Jdoe),
                    "<bad",
                    LoginXml.Build("Service Control Manager", 7001, "2026-10-14T10:00:00Z", Jdoe, Array.Empty<(string Name, string Value)>()),
                    LoginXml.Winlogon(7002, "2026-10-13T10:00:00Z", Jdoe)
                }
                : Array.Empty<string>()
        };

        var history = Read(source);

        Assert.Equal(new[] { T("2026-10-15T10:00:00Z"), T("2026-10-13T10:00:00Z") }, history.Records.Select(record => record.TimeUtc));
        Assert.Equal(LoginLogOutcome.Complete, Of(history, LoginLog.System).Outcome);
    }

    // Found on a real PC whose clock once stepped back: a login record is older than the first record of the log.
    [Fact]
    public void Read_WhenALoginRecordIsOlderThanTheFirstRecordOfTheLog_TheLogBeginsAtThatRecord()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? SystemRecords((7001, "2026-10-15T16:25:45Z"), (7002, "2026-10-01T18:08:15Z"))
                : Array.Empty<string>(),
            Oldest = _ => T("2026-10-01T21:00:44Z")
        };

        var history = Read(source);

        Assert.Equal(T("2026-10-01T18:08:15Z"), Of(history, LoginLog.System).Begins);
        Assert.Equal(T("2026-10-01T21:00:44Z"), Of(history, LoginLog.UserProfileService).Begins);
        Assert.Equal(T("2026-10-01T18:08:15Z"), history.RecordsBegin);
    }

    [Fact]
    public void Read_WithAUser_AsksOnlyForThatUsersRecords()
    {
        var source = new FakeLogSource
        {
            Records = (log, sid, _) => sid != Jdoe
                ? Array.Empty<string>()
                : log == LoginLog.System ? SystemRecords((7001, "2026-10-15T10:00:00Z")) : ProfileRecords((1, "2026-10-15T10:00:01Z"))
        };

        var history = Read(source, sid: Jdoe);

        Assert.Equal(2, source.Reads.Count);
        Assert.All(source.Reads, read => Assert.Equal(Jdoe, read.Sid));
        Assert.Equal(2, history.Records.Count);
        Assert.True(history.AnyRecordOfAnyone);
    }

    [Fact]
    public void Read_WithAUserWhoHasNoRecord_AsksWhetherAnyoneDoes()
    {
        var someoneElseHasRecords = new FakeLogSource
        {
            Records = (_, sid, _) => sid is null
                ? new[] { LoginXml.Winlogon(7001, "2026-10-15T10:00:00Z", Asmith) }
                : Array.Empty<string>()
        };

        var history = Read(someoneElseHasRecords, sid: Jdoe);

        Assert.Empty(history.Records);
        Assert.True(history.AnyRecordOfAnyone);
        Assert.Contains((LoginLog.System, (string?)null), someoneElseHasRecords.Reads);

        var nobodyHasRecords = new FakeLogSource();
        Assert.False(Read(nobodyHasRecords, sid: Jdoe).AnyRecordOfAnyone);
    }

    [Fact]
    public void Read_WithoutAUser_SaysThereAreRecordsOfAnyoneOnlyWhenThereAreRecords()
    {
        var empty = new FakeLogSource();
        Assert.False(Read(empty).AnyRecordOfAnyone);
        Assert.Equal(2, empty.Reads.Count);

        var withRecords = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System ? SystemRecords((7001, "2026-10-15T10:00:00Z")) : Array.Empty<string>()
        };
        Assert.True(Read(withRecords).AnyRecordOfAnyone);
    }

    [Fact]
    public void Read_WhenTheLimitIsReached_UsesWhatWasRead()
    {
        var source = new FakeLogSource { Records = (log, _, ct) => TwoRecordsThenWait(log, ct) };

        var stopwatch = Stopwatch.StartNew();
        var history = new LoginHistoryReader(source, 1000).Read("pc1", null, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.All(history.Logs, result =>
        {
            Assert.Equal(LoginLogOutcome.TimedOut, result.Outcome);
            Assert.Null(result.Failure);
        });
        Assert.Equal(4, history.Records.Count);
        Assert.Equal(T("2026-10-14T20:46:08Z"), Of(history, LoginLog.System).Begins);
        Assert.Equal(T("2026-10-14T20:46:07Z"), Of(history, LoginLog.UserProfileService).Begins);
    }

    [Fact]
    public void Read_WhenTheLimitIsReachedWhileAskingForTheOldestRecord_KeepsTheRecordsAndSaysTimedOut()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? SystemRecords((7001, "2026-10-15T16:25:45Z"), (7002, "2026-10-14T20:46:08Z"))
                : ProfileRecords((1, "2026-10-15T16:25:46Z"), (3, "2026-10-14T20:46:07Z")),
            OldestWithToken = (_, ct) =>
            {
                ct.WaitHandle.WaitOne();
                ct.ThrowIfCancellationRequested();
                return null;
            }
        };

        var history = new LoginHistoryReader(source, 1000).Read("pc1", null, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.All(history.Logs, result => Assert.Equal(LoginLogOutcome.TimedOut, result.Outcome));
        Assert.Equal(4, history.Records.Count);
        Assert.Equal(T("2026-10-14T20:46:08Z"), Of(history, LoginLog.System).Begins);
    }

    // A read that got nothing before the limit says nothing about the log. Reporting it as a log without records
    // would tell the operator that the computer does not record logins, which nobody found out.
    [Fact]
    public void Read_WhenTheLimitIsReachedBeforeAnyRecordWasRead_TheLogCouldNotBeRead()
    {
        var history = new LoginHistoryReader(FakeLogSource.NeverAnswers(), 1000)
            .Read("pc1", null, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.False(history.AnyLogRead);
        Assert.False(history.AnyRecordOfAnyone);
        Assert.Null(history.RecordsBegin);
        Assert.All(history.Logs, result =>
        {
            Assert.Equal(LoginLogOutcome.Failed, result.Outcome);
            Assert.Contains("timed out", result.Failure);
        });
    }

    [Fact]
    public void Read_WhenOnlyOneLogGotRecordsBeforeTheLimit_ThatLogTimedOutAndTheOtherCouldNotBeRead()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, ct) => log == LoginLog.System ? TwoRecordsThenWait(log, ct) : FakeLogSource.NothingUntilCancelled(ct)
        };

        var history = new LoginHistoryReader(source, 1000).Read("pc1", null, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Equal(LoginLogOutcome.TimedOut, Of(history, LoginLog.System).Outcome);
        Assert.Equal(LoginLogOutcome.Failed, Of(history, LoginLog.UserProfileService).Outcome);
        Assert.True(history.AnyLogRead);
        Assert.Equal(2, history.Records.Count);
    }

    [Fact]
    public void Read_UnauthorizedAccess_IsReportedAsNoPermission()
    {
        var source = new FakeLogSource { Records = (_, _, _) => throw new UnauthorizedAccessException() };

        var history = Read(source);

        Assert.False(history.AnyLogRead);
        Assert.Empty(history.Records);
        Assert.All(history.Logs, result =>
        {
            Assert.Equal(LoginLogOutcome.Failed, result.Outcome);
            Assert.Contains("no permission to read the event logs", result.Failure);
        });
    }

    [Fact]
    public void Read_AnRpcFailure_IsReportedAsUnreachable()
    {
        var source = new FakeLogSource
        {
            Records = (_, _, _) => throw new EventLogException("The RPC server is unavailable.") { HResult = unchecked((int)0x800706BA) }
        };

        var history = Read(source);

        Assert.All(history.Logs, result =>
        {
            Assert.Equal(LoginLogOutcome.Failed, result.Outcome);
            Assert.Contains("Remote Event Log Management", result.Failure);
        });
    }

    // Review focus 5: a log that throws something unexpected
    [Fact]
    public void Read_AnUnexpectedException_IsReportedWithItsMessage()
    {
        var source = new FakeLogSource { Records = (_, _, _) => throw new InvalidOperationException("boom") };

        var history = Read(source);

        Assert.All(history.Logs, result =>
        {
            Assert.Equal(LoginLogOutcome.Failed, result.Outcome);
            Assert.Contains("boom", result.Failure);
        });
    }

    [Fact]
    public void Read_AFailedLog_DoesNotHideTheOtherLog()
    {
        var source = new FakeLogSource
        {
            Records = (log, _, _) => log == LoginLog.System
                ? throw new InvalidOperationException("boom")
                : ProfileRecords((1, "2026-10-15T16:25:46Z"))
        };

        var history = Read(source);

        Assert.True(history.AnyLogRead);
        Assert.Equal(LoginLogOutcome.Failed, Of(history, LoginLog.System).Outcome);
        Assert.Equal(LoginLogOutcome.Complete, Of(history, LoginLog.UserProfileService).Outcome);
        Assert.Single(history.Records);
    }

    [Fact]
    public void Read_WhenCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        var source = new FakeLogSource { Records = (_, _, _) => OneRecordThenCancel(cts) };

        Assert.Throws<OperationCanceledException>(() => Read(source, ct: cts.Token));
    }

    [Fact]
    public void Read_WhenAlreadyCancelled_DoesNotAskTheSource()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var source = new FakeLogSource();

        Assert.Throws<OperationCanceledException>(() => Read(source, ct: cts.Token));
        Assert.Empty(source.Reads);
    }

    private static IEnumerable<string> TwoRecordsThenWait(LoginLog log, CancellationToken ct)
    {
        if (log == LoginLog.System)
        {
            yield return LoginXml.Winlogon(7001, "2026-10-15T16:25:45Z", Jdoe);
            yield return LoginXml.Winlogon(7002, "2026-10-14T20:46:08Z", Jdoe);
        }
        else
        {
            yield return LoginXml.Profile(1, "2026-10-15T16:25:46Z", Jdoe);
            yield return LoginXml.Profile(3, "2026-10-14T20:46:07Z", Jdoe);
        }

        ct.WaitHandle.WaitOne();
        ct.ThrowIfCancellationRequested();
    }

    private static IEnumerable<string> OneRecordThenCancel(CancellationTokenSource cts)
    {
        yield return LoginXml.Winlogon(7001, "2026-10-15T16:25:45Z", Jdoe);
        cts.Cancel();
        yield return LoginXml.Winlogon(7002, "2026-10-14T20:46:08Z", Jdoe);
    }
}

/// <summary>An event-log source that answers from delegates and remembers what it was asked.</summary>
internal sealed class FakeLogSource : ILoginLogSource
{
    private readonly object _gate = new();

    /// <summary>A source whose logs give nothing until the read is cancelled, as a computer that answers too slowly does.</summary>
    public static FakeLogSource NeverAnswers() => new() { Records = (_, _, ct) => NothingUntilCancelled(ct) };

    public static IEnumerable<string> NothingUntilCancelled(CancellationToken cancellationToken)
    {
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public Func<LoginLog, string?, CancellationToken, IEnumerable<string>> Records { get; set; } = (_, _, _) => Array.Empty<string>();

    public Func<LoginLog, DateTimeOffset?> Oldest { get; set; } = _ => null;

    /// <summary>When set, answers the question for the oldest record instead of <see cref="Oldest"/> and sees the token.</summary>
    public Func<LoginLog, CancellationToken, DateTimeOffset?>? OldestWithToken { get; set; }

    public List<(LoginLog Log, string? Sid)> Reads { get; } = new();

    public List<LoginLog> OldestReads { get; } = new();

    public IEnumerable<string> ReadRecordXml(string computer, LoginLog log, string? sid, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Reads.Add((log, sid));
        }

        return Records(log, sid, cancellationToken);
    }

    public DateTimeOffset? ReadOldestRecordTime(string computer, LoginLog log, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            OldestReads.Add(log);
        }

        return OldestWithToken is null ? Oldest(log) : OldestWithToken(log, cancellationToken);
    }
}
