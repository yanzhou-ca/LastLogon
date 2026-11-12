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

    /// <summary>
    /// True when the name is a plain host name. One label is a short host name, several labels
    /// are a fully qualified name. Separators, ports and wildcards are not allowed, so the name
    /// can only ever address a host and never a path or a different kind of target.
    /// </summary>
    public static bool IsValidComputerName(string name)
    {
        if (name.Length is 0 or > 253) return false;

        var labels = name.Split('.');
        if (labels.Length > 8) return false;

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63) return false;
            if (!IsAsciiLetterOrDigit(label[0]) || !IsAsciiLetterOrDigit(label[^1])) return false;
            if (!label.All(c => IsAsciiLetterOrDigit(c) || c == '-')) return false;
        }

        return true;
    }

    /// <summary>
    /// True when the name is a single label, that is, a name that still needs the domain.
    /// </summary>
    public static bool IsShortComputerName(string name)
        => !name.Contains('.');

    /// <summary>
    /// True when the name can be used as a single path segment and handed to another program.
    /// <para>
    /// Windows allows characters such as <c>=</c> in a directory name, so this only rejects what
    /// could break out of a path or a command line: separators, quotes, wildcards and control
    /// characters. Every other character a profile folder can legitimately contain is accepted.
    /// </para>
    /// </summary>
    public static bool IsSafeProfileFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;

        foreach (var character in name)
        {
            if (char.IsControl(character)) return false;
            if (character is '"' or '\\' or '/' or '|' or '<' or '>' or ':' or '*' or '?') return false;
        }

        return true;
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
