using System.DirectoryServices.ActiveDirectory;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace LastLogon.Core;

/// <summary>
/// Turns the short computer names entered by the operator into the fully qualified names that
/// are used for network connections.
/// <para>
/// Every network path in the application (the SMB profile scan, the Security-log query and the
/// Explorer window that opens a profile folder) goes through this type, so that all of them
/// address the same host and none of them falls back to short-name resolution, which an
/// on-path attacker can poison.
/// </para>
/// </summary>
public static class ComputerTarget
{
    /// <summary>How a short computer name is turned into the name that is connected to.</summary>
    public enum Addressing
    {
        /// <summary>
        /// The name is completed with the domain of this computer. The result is unambiguous and
        /// cannot be redirected by a name answer on the network.
        /// </summary>
        DomainQualified,

        /// <summary>
        /// The name is used as it is and resolved the way Windows resolves a short name. Some
        /// estates need this because the DNS name of a computer is not the short name with the
        /// domain of the operator's computer.
        /// </summary>
        ShortName,
    }

    /// <summary>How long the domain lookup may take before the machine's own DNS suffix is used.</summary>
    private static readonly TimeSpan DomainLookupTimeout = TimeSpan.FromSeconds(3);

    private static string? _dnsDomain;
    private static Addressing _addressing = Addressing.DomainQualified;

    /// <summary>
    /// How a short computer name is turned into the name that is connected to. It is a setting
    /// of the running application, because the operator chooses it and every network path in
    /// the tool has to use the same one.
    /// </summary>
    public static Addressing AddressingMode
    {
        get => _addressing;
        set => _addressing = value;
    }

    /// <summary>
    /// The DNS domain used to qualify short computer names.
    /// <para>
    /// It is resolved once from this computer's own configuration. It can be overridden for a
    /// different domain, which is what the tests do.
    /// </para>
    /// </summary>
    public static string DnsDomain
    {
        get => _dnsDomain ??= ResolveDnsDomain();
        internal set => _dnsDomain = string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('.');
    }

    /// <summary>
    /// Qualifies <paramref name="computer"/> for a connection. A name that already carries a
    /// domain is used as it is, a short name is completed with the given DNS domain unless the
    /// short name is asked for.
    /// <para>
    /// The addressing mode can be given explicitly. Callers that have to try both forms, such
    /// as the connection test, must not be influenced by the mode the operator selected.
    /// </para>
    /// </summary>
    public static string Qualify(
        string computer,
        string? dnsDomain = null,
        Addressing? addressing = null)
    {
        var name = (computer ?? "").Trim();
        if (name.Contains('.')) return name;
        if ((addressing ?? _addressing) == Addressing.ShortName) return name;

        var domain = dnsDomain ?? DnsDomain;
        return IsValidDnsDomain(domain) ? $"{name}.{domain}" : name;
    }

    /// <summary>Builds a UNC path into the administrative share of <paramref name="computer"/>.</summary>
    public static string UncPath(string computer, params string[] segments)
        => UncPathInDomain(computer, DnsDomain, segments);

    /// <summary>Builds a UNC path into the administrative share of a computer in a given domain.</summary>
    public static string UncPathInDomain(string computer, string? dnsDomain, params string[] segments)
    {
        var path = @"\\" + Qualify(computer, dnsDomain) + @"\C$";
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)) continue;
            path = path.TrimEnd('\\') + "\\" + segment.Trim('\\');
        }

        return path;
    }

    /// <summary>True when <paramref name="domain"/> is a syntactically valid DNS domain name.</summary>
    public static bool IsValidDnsDomain(string? domain)
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

    private static string ResolveDnsDomain()
    {
        // The domain this computer is joined to is the authoritative name, and it is the same
        // domain the directory search runs in. It cannot be influenced by anything the user
        // changes, unlike the USERDNSDOMAIN environment variable.
        var joined = GetActiveDirectoryDomain();
        if (IsValidDnsDomain(joined)) return joined.Trim().TrimEnd('.');

        // The machine's own DNS suffix. It is normally the same domain, but it can be set to
        // something else, and then the name above is the better answer.
        var suffix = GetDnsDomainName();
        if (suffix.Contains('.') && IsValidDnsDomain(suffix)) return suffix.Trim().TrimEnd('.');

        // The user's environment and the NetBIOS domain are the last resorts.
        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        if (string.IsNullOrWhiteSpace(domain))
            domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;

        domain = domain?.Trim().TrimEnd('.') ?? string.Empty;
        return IsValidDnsDomain(domain) ? domain : string.Empty;
    }

    /// <summary>
    /// The DNS name of the domain this computer is joined to, or an empty string when the
    /// computer is not joined to a domain.
    /// </summary>
    private static string GetActiveDirectoryDomain()
    {
        try
        {
            // The lookup is normally answered from the local machine, but it can still block
            // on a slow network, so it is given a moment to answer and then given up on.
            var lookup = Task.Run(() =>
            {
                using var domain = Domain.GetComputerDomain();
                return domain?.Name ?? string.Empty;
            });

            return lookup.Wait(DomainLookupTimeout) ? lookup.Result : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string GetDnsDomainName()
    {
        try
        {
            uint size = 0;
            if (!GetComputerNameEx(ComputerNameType.DnsDomain, null, ref size) || size == 0)
                return string.Empty;

            var buffer = new StringBuilder((int)size);
            return GetComputerNameEx(ComputerNameType.DnsDomain, buffer, ref size)
                ? buffer.ToString()
                : string.Empty;
        }
        catch (DllNotFoundException) { return string.Empty; }
        catch (EntryPointNotFoundException) { return string.Empty; }
    }

    private enum ComputerNameType
    {
        NetBios = 0,
        DnsHostname = 1,
        DnsDomain = 2,
        DnsFullyQualified = 3,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComputerNameEx(ComputerNameType nameType, StringBuilder? buffer, ref uint size);
}
