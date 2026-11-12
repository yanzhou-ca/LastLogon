using System.Net;
using System.Net.NetworkInformation;

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

        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
            domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
        domain = domain?.Trim().TrimEnd('.');
        if (!IsValidDnsDomain(domain))
            throw new InvalidOperationException("The current DNS domain could not be determined.");

        return $@"\\{computer}.{domain}\C$\Users";
    }

    private static bool IsValidDnsDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253
            || Uri.CheckHostName(domain) != UriHostNameType.Dns)
            return false;

        return domain.Split('.').All(label =>
            label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0])
            && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    public IReadOnlyList<string> ListUserFolders(string computer)
        => Directory.EnumerateDirectories(Root(computer)).Select(p => Path.GetFileName(p)!).ToList();

    public DateTime? GetNtUserDatTime(string computer, string user)
    {
        var fi = new FileInfo(Path.Combine(Root(computer), user, "NTUSER.DAT"));
        // -Force equivalent: FileInfo sees hidden/system files
        return fi.Exists ? fi.LastWriteTime : null;
    }

    public DateTime GetFolderTime(string computer, string user)
        => Directory.GetLastWriteTime(Path.Combine(Root(computer), user));
}
