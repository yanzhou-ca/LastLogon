using System.Net;

namespace LastLogon.Core;

/// <summary>One attempt to reach the profile folder of a computer.</summary>
public sealed record ConnectionAttempt(string AddressedAs, bool Succeeded, string? Message, int? Win32Error);

/// <summary>
/// The outcome of trying to reach a computer, including every way of addressing it that was
/// tried.
/// </summary>
public sealed record ConnectionTestResult(
    string Computer,
    string? ResolvedAddress,
    bool Succeeded,
    string AddressedAs,
    IReadOnlyList<ConnectionAttempt> Attempts)
{
    /// <summary>One line describing what the operator should do next.</summary>
    public string Summary => Succeeded
        ? $"Reachable as {AddressedAs}."
        : Attempts.Count > 1
            ? $"Not reachable. {Attempts[^1].Message} {Hint(Attempts[^1].Win32Error)}"
            : $"Not reachable. {Attempts[^1].Message} {Hint(Attempts[^1].Win32Error)}";

    /// <summary>
    /// Turns the Windows error into something an administrator can act on. The code says much
    /// more than "access denied" does: it separates a wrong name from a wrong account from a
    /// share that is not there.
    /// </summary>
    internal static string Hint(int? error) => error switch
    {
        5 => "The server refused the access: the account is not an administrator on that computer, "
             + "or the C$ share is turned off, or the name reaches a different computer.",
        53 => "The computer could not be found under that name. The name or the domain is wrong.",
        64 => "The computer could not be found under that name. The name or the domain is wrong.",
        67 => "The name does not exist. The computer is not registered under that name.",
        1326 => "The account was refused by the server. The credentials were not accepted.",
        _ => string.Empty,
    };
}

/// <summary>
/// Tries to reach the profile folder of a computer the way the scan would, and reports what
/// happened for each way of addressing it. It is what tells an operator whether the name is
/// wrong, the account is missing rights, or the share is not there.
/// </summary>
public static class ConnectionTest
{
    /// <summary>How long a single attempt may take.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    public static ConnectionTestResult Run(string computer, ComputerTarget.Addressing mode)
    {
        var attempts = new List<ConnectionAttempt>();
        var resolved = Resolve(computer);

        foreach (var candidate in Candidates(computer, mode))
        {
            var attempt = Try(candidate);
            attempts.Add(attempt);
            if (attempt.Succeeded)
            {
                return new ConnectionTestResult(computer, resolved, true, candidate, attempts);
            }
        }

        return new ConnectionTestResult(
            computer,
            resolved,
            false,
            attempts.Count > 0 ? attempts[^1].AddressedAs : computer,
            attempts);
    }

    /// <summary>The addresses that are tried, in the order the operator asked for them.</summary>
    private static IEnumerable<string> Candidates(string computer, ComputerTarget.Addressing mode)
    {
        if (computer.Contains('.'))
        {
            // A name that already carries a domain is used exactly as it is.
            yield return computer;
            yield break;
        }

        var qualified = ComputerTarget.Qualify(computer, ComputerTarget.DnsDomain);
        var bare = computer;

        if (mode == ComputerTarget.Addressing.ShortName)
        {
            yield return bare;
            if (qualified != bare) yield return qualified;
        }
        else
        {
            yield return qualified;
            if (bare != qualified) yield return bare;
        }
    }

    private static string? Resolve(string computer)
    {
        try
        {
            var entry = Dns.GetHostEntry(computer);
            return entry.AddressList.Count() > 0 ? entry.AddressList[0].ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ConnectionAttempt Try(string address)
    {
        var path = $@"\\{address}\C$\Users";
        try
        {
            var folders = Directory.GetDirectories(path);
            return new ConnectionAttempt(
                address,
                true,
                $"{folders.Length} profile folder(s) readable.",
                null);
        }
        catch (Exception ex)
        {
            return new ConnectionAttempt(address, false, ex.Message, ErrorCode(ex));
        }
    }

    /// <summary>The Windows error code behind the exception, for example 5 for access denied.</summary>
    private static int? ErrorCode(Exception exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code == 0 ? null : code;
    }
}
