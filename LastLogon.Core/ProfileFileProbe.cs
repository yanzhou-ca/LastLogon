using System.Security.AccessControl;
using System.Security.Principal;

namespace LastLogon.Core;

/// <summary>
/// The two reads of a profile folder that the login times need. They take a path, so the tests can point them
/// at a folder of this computer. <see cref="SmbProfileFileSystem"/> builds the path through the share.
/// </summary>
internal static class ProfileFileProbe
{
    // ERROR_SHARING_VIOLATION: the low 16 bits of the HResult of an IOException caused by a file that is held open.
    private const int SharingViolation = 32;

    /// <summary>
    /// Windows keeps the NTUSER.DAT of a logged-in user open and shares it with nobody, so an open for reading that
    /// allows every kind of sharing is refused with a sharing violation while the profile is in use. The file is
    /// closed at once and nothing is read from it. A missing file or folder, a refusal of access or any other error
    /// means the check cannot tell.
    /// </summary>
    public static ProfileUse GetUse(string ntUserDatPath)
    {
        try
        {
            using var file = File.Open(
                ntUserDatPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ProfileUse.NotInUse;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == SharingViolation)
        {
            return ProfileUse.InUse;
        }
        catch (Exception)
        {
            return ProfileUse.Unknown;
        }
    }

    /// <summary>
    /// The SIDs that the access list of the folder gives access to, as text. The SIDs are never translated to
    /// names: that would ask a domain controller for every entry. Throws like the file calls of the share do.
    /// </summary>
    public static IReadOnlyList<string> GetAccessSids(string folderPath)
    {
        var rules = new DirectoryInfo(folderPath)
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        var sids = new List<string>();
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier identity
                && SidText.TryNormalize(identity.Value, out var sid)
                && !sids.Contains(sid))
            {
                sids.Add(sid);
            }
        }

        return sids;
    }
}
