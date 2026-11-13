using LastLogon.Core;
using Xunit;

namespace LastLogon.Tests;

public class ProfileResultTests
{
    private static ProfileResult Row(string username, ScanStatus status)
        => new("pc1", username, null, null, status, null);

    [Fact]
    public void CanOpenFolder_IsTrueForAProfileThatWasFound()
        => Assert.True(Row("alice", ScanStatus.Ok).CanOpenFolder);

    // A profile that exists but could not be read is still worth opening: the operator may have
    // more luck in Explorer than the scan had.
    [Fact]
    public void CanOpenFolder_IsTrueForAProfileThatCouldNotBeRead()
        => Assert.True(Row("alice", ScanStatus.AccessDenied).CanOpenFolder);

    // The folder of a user who has no profile on the computer does not exist, so Explorer would
    // only report an error.
    [Fact]
    public void CanOpenFolder_IsFalseForAComputerWithoutThatProfile()
        => Assert.False(Row("ghost", ScanStatus.NoProfile).CanOpenFolder);

    [Fact]
    public void CanOpenFolder_IsFalseForARowThatNamesNoProfile()
        => Assert.False(Row("", ScanStatus.Offline).CanOpenFolder);

    // The grid shows these texts to an operator, so they must not be the names of enum members.
    [Theory]
    [InlineData(ActivitySource.NtUserDat, "NTUSER.DAT")]
    [InlineData(ActivitySource.Folder, "Folder time")]
    public void SourceText_NamesTheSourceTheWayAnOperatorReadsIt(ActivitySource source, string expected)
        => Assert.Equal(expected, new ProfileResult("pc1", "alice", null, source, ScanStatus.Ok, null).SourceText);

    [Fact]
    public void SourceText_IsEmptyWhenTheRowHasNoSource()
        => Assert.Equal("", Row("alice", ScanStatus.Offline).SourceText);

    [Theory]
    [InlineData(ScanStatus.NoProfile, "No profile")]
    [InlineData(ScanStatus.AccessDenied, "Access denied")]
    [InlineData(ScanStatus.Offline, "Offline")]
    [InlineData(ScanStatus.TimedOut, "Timed out")]
    [InlineData(ScanStatus.Error, "Error")]
    public void ResultText_SaysWhatHappenedInWords(ScanStatus status, string expected)
        => Assert.Equal(expected, Row("alice", status).ResultText);

    [Fact]
    public void ResultText_IsOkWhenTheTimeCameFromNtUserDat()
        => Assert.Equal(
            "OK",
            new ProfileResult("pc1", "alice", new DateTime(2025, 1, 1), ActivitySource.NtUserDat, ScanStatus.Ok, null).ResultText);

    // A folder time is only an approximation of the activity, so the row must not read as a plain OK.
    [Fact]
    public void ResultText_IsApproximateWhenOnlyTheFolderTimeIsKnown()
        => Assert.Equal(
            "Approximate",
            new ProfileResult("pc1", "alice", new DateTime(2025, 1, 1), ActivitySource.Folder, ScanStatus.Ok, "NTUSER.DAT not found; using folder time").ResultText);

    [Fact]
    public void DetailText_IsTheMessageOfTheRow()
        => Assert.Equal(
            "Offline or C$ unreachable",
            new ProfileResult("pc1", "alice", null, null, ScanStatus.Offline, "Offline or C$ unreachable").DetailText);

    [Fact]
    public void DetailText_IsEmptyWhenThereIsNoMessage()
        => Assert.Equal("", Row("alice", ScanStatus.Ok).DetailText);
}
