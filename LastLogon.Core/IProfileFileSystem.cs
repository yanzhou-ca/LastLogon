namespace LastLogon.Core;

public interface IProfileFileSystem
{
    /// <summary>Folder names under C:\Users. Throws DirectoryNotFoundException, UnauthorizedAccessException or IOException.</summary>
    IReadOnlyList<string> ListUserFolders(string computer);

    /// <summary>Null when NTUSER.DAT is missing.</summary>
    DateTime? GetNtUserDatTime(string computer, string user);

    DateTime GetFolderTime(string computer, string user);
}

public sealed class SmbProfileFileSystem : IProfileFileSystem
{
    private static string Root(string computer)
    {
        var names = InputParser.ParseComputers(computer);
        if (names.Count != 1 || !names[0].Equals(computer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only a short computer hostname can be scanned.", nameof(computer));

        if (!ComputerTarget.IsValidDnsDomain(ComputerTarget.DnsDomain))
            throw new InvalidOperationException("The current DNS domain could not be determined.");

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

    private static string SafeSegment(string user)
    {
        if (!InputParser.IsSafeProfileFolderName(user))
            throw new ArgumentException("Only a plain profile folder name can be read.", nameof(user));

        return user;
    }
}
