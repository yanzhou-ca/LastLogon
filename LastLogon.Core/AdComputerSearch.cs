using System.DirectoryServices;
using System.Text;

namespace LastLogon.Core;

public static class AdComputerSearch
{
    /// <summary>How long a single directory search may take before it is abandoned.</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public static string BuildFilter(string namePrefix)
    {
        var sb = new StringBuilder();
        foreach (var c in namePrefix ?? "")
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }
        return $"(&(objectCategory=computer)(name={sb}*))";
    }

    public static Task<IReadOnlyList<string>> SearchAsync(string namePrefix, string? ouDistinguishedName, CancellationToken ct)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            using var root = new DirectoryEntry();
            using var scope = string.IsNullOrWhiteSpace(ouDistinguishedName)
                ? null
                : new DirectoryEntry("LDAP://" + ResolveDistinguishedName(ouDistinguishedName, root));

            using var searcher = new DirectorySearcher(scope ?? root, BuildFilter(namePrefix), new[] { "name" })
            {
                PageSize = 500,
                SizeLimit = 5000,
                ClientTimeout = SearchTimeout,
            };
            var names = new List<string>();
            using var results = searcher.FindAll();
            foreach (SearchResult r in results)
            {
                ct.ThrowIfCancellationRequested();
                if (r.Properties["name"].Count > 0) names.Add((string)r.Properties["name"][0]);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }, ct);

    /// <summary>
    /// Validates the operator supplied OU and returns the distinguished name to bind to.
    /// <para>
    /// The name is rejected when it carries a server component or when it points at a domain
    /// other than this computer's own: either would make the bind leave the operator's domain
    /// and offer their credentials to a server of someone else's choosing. A name without any
    /// domain component is relative to this computer's default naming context and is accepted.
    /// </para>
    /// </summary>
    private static string ResolveDistinguishedName(string ouDistinguishedName, DirectoryEntry root)
    {
        var dn = ouDistinguishedName.Trim();
        var defaultNamingContext = GetDefaultNamingContext(root);

        if (!IsValidDistinguishedName(dn, defaultNamingContext))
            throw new ArgumentException(
                defaultNamingContext.Length > 0
                    ? $"Enter a distinguished name inside {defaultNamingContext}, for example OU=Sales."
                    : "Enter a distinguished name such as OU=Sales.",
                nameof(ouDistinguishedName));

        return dn;
    }

    internal static bool IsValidDistinguishedName(string dn, string defaultNamingContext)
    {
        if (string.IsNullOrWhiteSpace(dn) || dn.Length > 1024) return false;
        if (dn.Any(char.IsControl)) return false;
        if (dn.Contains('/')) return false;                                  // server component
        if (dn.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)) return false;

        var rdns = SplitRelativeNames(dn);
        if (rdns.Count == 0) return false;

        foreach (var rdn in rdns)
        {
            var separator = rdn.IndexOf('=');
            if (separator <= 0) return false;

            var type = rdn[..separator].Trim();
            var value = rdn[(separator + 1)..].Trim();
            if (type.Length == 0 || value.Length == 0) return false;
            if (!IsValidAttributeType(type)) return false;
        }

        // The domain components have to match this computer's own domain, otherwise the bind
        // would be redirected to another directory.
        var requested = DomainComponents(rdns);
        if (requested.Count == 0) return true;                              // relative to the default context

        var local = DomainComponents(SplitRelativeNames(defaultNamingContext));
        if (local.Count == 0) return false;

        return requested.Count == local.Count
            && requested.Zip(local).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDefaultNamingContext(DirectoryEntry root)
    {
        try
        {
            var property = root.Properties["defaultNamingContext"];
            return property.Count > 0 ? (property[0] as string ?? string.Empty) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static List<string> SplitRelativeNames(string dn)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var escaped = false;

        foreach (var character in dn)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            switch (character)
            {
                case '\\':
                    current.Append(character);
                    escaped = true;
                    break;
                case ',':
                    result.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static List<string> DomainComponents(List<string> rdns)
    {
        var result = new List<string>();
        for (var index = rdns.Count - 1; index >= 0; index--)
        {
            var rdn = rdns[index];
            var separator = rdn.IndexOf('=');
            if (separator <= 0) break;

            var type = rdn[..separator].Trim();
            if (!type.Equals("DC", StringComparison.OrdinalIgnoreCase)) break;

            result.Insert(0, rdn.Trim());
        }

        return result;
    }

    private static bool IsValidAttributeType(string type)
    {
        if (type.Length is 0 or > 64) return false;
        if (!char.IsAsciiLetterOrDigit(type[0])) return false;
        return type.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '.');
    }
}
