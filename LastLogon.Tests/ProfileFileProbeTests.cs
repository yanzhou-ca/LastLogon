using LastLogon.Core;

namespace LastLogon.Tests;

public sealed class ProfileFileProbeTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("LastLogonProbe");

    private string NtUserDat => Path.Combine(_folder.FullName, "NTUSER.DAT");

    public void Dispose()
    {
        try
        {
            _folder.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void GetUse_IsInUseWhileTheFileIsHeldWithoutSharing()
    {
        File.WriteAllText(NtUserDat, "profile");
        using var held = new FileStream(NtUserDat, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(ProfileUse.InUse, ProfileFileProbe.GetUse(NtUserDat));
    }

    [Fact]
    public void GetUse_IsNotInUseWhenTheFileOpens_AndLeavesItClosed()
    {
        File.WriteAllText(NtUserDat, "profile");

        Assert.Equal(ProfileUse.NotInUse, ProfileFileProbe.GetUse(NtUserDat));

        // A handle the probe left open would stop the delete.
        File.Delete(NtUserDat);
        Assert.False(File.Exists(NtUserDat));
    }

    [Fact]
    public void GetUse_IsUnknownForAMissingFile()
        => Assert.Equal(ProfileUse.Unknown, ProfileFileProbe.GetUse(NtUserDat));

    [Fact]
    public void GetUse_IsUnknownForAMissingFolder()
        => Assert.Equal(ProfileUse.Unknown, ProfileFileProbe.GetUse(Path.Combine(_folder.FullName, "no-such-folder", "NTUSER.DAT")));

    [Fact]
    public void GetUse_IsUnknownWhenAccessIsRefused()
        // Opening a directory as a file is refused with UnauthorizedAccessException.
        => Assert.Equal(ProfileUse.Unknown, ProfileFileProbe.GetUse(_folder.FullName));

    [Fact]
    public void GetAccessSids_ListsOnlyWellFormedSids()
    {
        var sids = ProfileFileProbe.GetAccessSids(_folder.FullName);

        Assert.NotEmpty(sids);
        Assert.All(sids, sid =>
        {
            Assert.True(SidText.TryNormalize(sid, out var normalised));
            Assert.Equal(normalised, sid);
        });
    }
}
