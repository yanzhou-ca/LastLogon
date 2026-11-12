using System.DirectoryServices;
using System.Text;

namespace LastLogon.Core;

public static class AdComputerSearch
{
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
            using var root = string.IsNullOrWhiteSpace(ouDistinguishedName)
                ? new DirectoryEntry()
                : new DirectoryEntry("LDAP://" + ouDistinguishedName);
            using var searcher = new DirectorySearcher(root, BuildFilter(namePrefix), new[] { "name" })
            {
                PageSize = 500,
                SizeLimit = 5000,
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
}
