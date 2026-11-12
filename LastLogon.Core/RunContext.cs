using System.Security.Principal;

namespace LastLogon.Core;

/// <summary>
/// Facts about the way the tool is running that explain a failed scan: which account is used
/// and whether it holds the full administrator token.
/// </summary>
public static class RunContext
{
    /// <summary>The account the tool runs as, for example CORP\jdoe.</summary>
    public static string Account { get; } = BuildAccount();

    /// <summary>
    /// True when the process holds the full administrator token. An account that belongs to the
    /// Administrators group still gets a filtered token when a program is started normally, and
    /// the administrative share (C$) of another computer refuses that.
    /// </summary>
    public static bool IsElevated { get; } = BuildElevation();

    private static string BuildAccount()
    {
        try
        {
            var user = Environment.UserName;
            var domain = Environment.UserDomainName;
            return string.IsNullOrWhiteSpace(domain)
                || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                ? user
                : domain + "\\" + user;
        }
        catch (Exception)
        {
            return Environment.UserName;
        }
    }

    private static bool BuildElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
