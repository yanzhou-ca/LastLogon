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
}
