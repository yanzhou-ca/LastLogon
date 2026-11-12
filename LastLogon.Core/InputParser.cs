namespace LastLogon.Core;

public static class InputParser
{
    public static IReadOnlyList<string> ParseComputers(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { '\r', '\n', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (!IsValidComputerName(name))
                throw new FormatException("Computer names must be short hostnames without path syntax.");
            if (seen.Add(name)) list.Add(name);
        }
        return list;
    }

    private static bool IsValidComputerName(string name)
    {
        if (name.Length is 0 or > 63) return false;
        if (!IsAsciiLetterOrDigit(name[0]) || !IsAsciiLetterOrDigit(name[^1])) return false;
        return name.All(c => IsAsciiLetterOrDigit(c) || c == '-');
    }

    private static bool IsAsciiLetterOrDigit(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    public static string NormalizeUsername(string input)
    {
        var u = (input ?? "").Trim();
        var slash = u.LastIndexOf('\\');
        if (slash >= 0) u = u[(slash + 1)..];
        var at = u.IndexOf('@');
        if (at >= 0) u = u[..at];
        return u.Trim();
    }
}
