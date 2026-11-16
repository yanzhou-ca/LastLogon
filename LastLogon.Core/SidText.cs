using System.Text.RegularExpressions;

namespace LastLogon.Core;

/// <summary>
/// Security identifiers as text. A SID that came from a remote computer goes into an event-log query only after
/// <see cref="TryNormalize"/> accepted it: what it returns is built from ASCII digits and dashes alone.
/// </summary>
public static class SidText
{
    private static readonly Regex WellFormed = new(
        @"^[Ss]-1-[0-9]{1,10}(-[0-9]{1,10}){1,15}\z", RegexOptions.CultureInvariant);

    /// <summary>Trims the text and upper-cases it when it is a well-formed SID, as <c>S-1-5-21-1-2-3-1001</c>.</summary>
    public static bool TryNormalize(string? text, out string sid)
    {
        var candidate = text?.Trim();
        if (candidate is not null && WellFormed.IsMatch(candidate))
        {
            sid = "S" + candidate[1..];
            return true;
        }

        sid = string.Empty;
        return false;
    }

    /// <summary>True for the SID of a user or group account (domain, local or Microsoft account), not for SYSTEM, built-in groups or app containers.</summary>
    public static bool IsAccountSid(string sid)
        => sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
           || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

    public static bool Same(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
