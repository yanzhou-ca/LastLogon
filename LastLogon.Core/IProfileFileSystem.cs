namespace LastLogon.Core;

/// <summary>Whether Windows holds the profile of a user open, which it does while the user is logged in.</summary>
public enum ProfileUse
{
    /// <summary>The check could not be made, or it failed.</summary>
    Unknown,

    /// <summary>The profile is held open: its user is logged in, or a service or program runs as that user.</summary>
    InUse,

    /// <summary>The profile could be opened, so nobody holds it.</summary>
    NotInUse
}

public interface IProfileFileSystem
{
    /// <summary>Folder names under C:\Users. Throws DirectoryNotFoundException, UnauthorizedAccessException or IOException.</summary>
    IReadOnlyList<string> ListUserFolders(string computer);

    /// <summary>Null when NTUSER.DAT is missing.</summary>
    DateTime? GetNtUserDatTime(string computer, string user);

    DateTime GetFolderTime(string computer, string user);

    /// <summary>
    /// The SIDs on the access list of a profile folder, as text. Throws like <see cref="GetNtUserDatTime"/> when the
    /// list cannot be read.
    /// </summary>
    IReadOnlyList<string> GetFolderAccessSids(string computer, string folder);

    /// <summary>
    /// Whether the profile in <paramref name="folder"/> is held open. Every failure to find out is
    /// <see cref="ProfileUse.Unknown"/>; only a computer or folder name that is not valid throws.
    /// </summary>
    ProfileUse GetProfileUse(string computer, string folder);
}

public sealed class SmbProfileFileSystem : IProfileFileSystem
{
    private static string Root(string computer)
    {
        var names = InputParser.ParseComputers(computer);
        if (names.Count != 1 || !names[0].Equals(computer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only one computer host name can be scanned.", nameof(computer));

        // A short name in domain-qualified mode must not silently fall back to short-name
        // resolution, which a name answer on the network can redirect. A fully qualified name
        // and the short-name mode do not need the domain at all.
        if (ComputerTarget.AddressingMode == ComputerTarget.Addressing.DomainQualified
            && InputParser.IsShortComputerName(names[0])
            && !ComputerTarget.IsValidDnsDomain(ComputerTarget.DnsDomain))
        {
            throw new InvalidOperationException(
                "The current DNS domain could not be determined. Enter the fully qualified "
                + "computer name, or choose the short name.");
        }

        return ComputerTarget.UncPath(computer, "Users");
    }

    public IReadOnlyList<string> ListUserFolders(string computer)
        => Directory.EnumerateDirectories(Root(computer)).Select(p => Path.GetFileName(p)!).ToList();

    public DateTime? GetNtUserDatTime(string computer, string user)
    {
        var path = Path.Combine(Root(computer), SafeSegment(user), "NTUSER.DAT");

        try
        {
            // File.GetAttributes tells a missing file apart from a file that cannot be read.
            // FileInfo.Exists answers "false" for both, which would report an access failure
            // as "NTUSER.DAT not found" and silently fall back to the folder time.
            File.GetAttributes(path);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        return new FileInfo(path).LastWriteTime;
    }

    public DateTime GetFolderTime(string computer, string user)
        => Directory.GetLastWriteTime(Path.Combine(Root(computer), SafeSegment(user)));

    public IReadOnlyList<string> GetFolderAccessSids(string computer, string folder)
        => ProfileFileProbe.GetAccessSids(Path.Combine(Root(computer), SafeSegment(folder)));

    public ProfileUse GetProfileUse(string computer, string folder)
        => ProfileFileProbe.GetUse(Path.Combine(Root(computer), SafeSegment(folder), "NTUSER.DAT"));

    private static string SafeSegment(string user)
    {
        if (!InputParser.IsSafeProfileFolderName(user))
            throw new ArgumentException("Only a plain profile folder name can be read.", nameof(user));

        return user;
    }
}
