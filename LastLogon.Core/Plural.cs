using System.Globalization;

namespace LastLogon.Core;

/// <summary>Counts for the status line: "1 computer", "2 computers", never "computer(s)".</summary>
public static class Plural
{
    public static string Count(int count, string singular, string? plural = null)
        => count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural ?? singular + "s");
}
