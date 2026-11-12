using System.Net;
using System.Net.Sockets;

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
    public string Summary
    {
        get
        {
            if (Succeeded)
            {
                var readable = Attempts.Count > 0 ? Attempts[^1].Message : null;
                return string.IsNullOrEmpty(readable)
                    ? $"Reachable as {AddressedAs}."
                    : $"Reachable as {AddressedAs}. {readable}";
            }

            if (Attempts.Count == 0)
                return $"Not reachable as {AddressedAs}.";

            // The first attempt is the one the scan would use in the selected mode, so its
            // error is the one that explains the scan result.
            var primary = Attempts[0];
            var code = primary.Win32Error is int error ? $" (Windows error {error})" : string.Empty;
            var message = string.IsNullOrEmpty(primary.Message)
                ? "The connection failed."
                : primary.Message;
            var hint = Hint(primary.Win32Error);
            return hint.Length == 0
                ? $"Not reachable{code}. {message}"
                : $"Not reachable{code}. {message} {hint}";
        }
    }

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
        67 => "The network name cannot be found: the name is wrong, or the C$ share is turned off "
              + "on that computer.",
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
    /// <summary>How long a single attempt may take before it is reported as unanswered.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    public static ConnectionTestResult Run(string computer, ComputerTarget.Addressing mode)
    {
        var names = InputParser.ParseComputers(computer);
        if (names.Count != 1)
            throw new ArgumentException("Only one computer host name can be tested.", nameof(computer));
        computer = names[0];

        var attempts = new List<ConnectionAttempt>();

        foreach (var candidate in Candidates(computer, mode))
        {
            var attempt = Try(candidate);
            attempts.Add(attempt);
            if (attempt.Succeeded)
            {
                return new ConnectionTestResult(
                    computer,
                    Resolve(candidate),
                    true,
                    candidate,
                    attempts);
            }
        }

        // The first address is the one the scan would use in the selected mode, so it is the
        // one whose resolution explains the failure when nothing could be reached.
        var addressedAs = attempts.Count > 0 ? attempts[0].AddressedAs : computer;
        return new ConnectionTestResult(
            computer,
            Resolve(addressedAs),
            false,
            addressedAs,
            attempts);
    }

    /// <summary>
    /// The addresses that are tried, in the order the operator asked for them. Both forms are
    /// always produced, because finding out which of them works is the point of the test.
    /// </summary>
    internal static IEnumerable<string> Candidates(
        string computer,
        ComputerTarget.Addressing mode,
        string? dnsDomain = null)
    {
        if (computer.Contains('.'))
        {
            // A name that already carries a domain is used exactly as it is.
            yield return computer;
            yield break;
        }

        var qualified = ComputerTarget.Qualify(
            computer,
            dnsDomain ?? ComputerTarget.DnsDomain,
            ComputerTarget.Addressing.DomainQualified);
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

    /// <summary>
    /// The address a name resolves to, preferring IPv4 because that is the address an
    /// administrator compares with the one they expect.
    /// </summary>
    private static string? Resolve(string computer)
    {
        try
        {
            var entry = Dns.GetHostEntry(computer);
            var address = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? entry.AddressList.FirstOrDefault();
            return address?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ConnectionAttempt Try(string address)
    {
        // A computer that does not answer can hold a directory call for a long time. The work
        // runs on its own thread so that it cannot starve the thread pool, and it is left to
        // finish on its own; what matters is that the operator gets an answer.
        var work = Task.Factory.StartNew(
            () => TryOnce(address),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        return work.Wait(AttemptTimeout)
            ? work.Result
            : new ConnectionAttempt(
                address,
                false,
                $"No answer within {AttemptTimeout.TotalSeconds:0} seconds.",
                null);
    }

    private static ConnectionAttempt TryOnce(string address)
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
        catch (UnauthorizedAccessException ex) when (CanReadAttributes(path))
        {
            // The folder itself can be opened, so the share and the account are fine; only the
            // listing is refused. A known profile can still be read directly.
            return new ConnectionAttempt(
                address,
                false,
                "The Users folder exists but its contents cannot be listed. Known profile files "
                + "can still be read; use By user with the exact profile folder name.",
                ErrorCode(ex));
        }
        catch (Exception ex)
        {
            return new ConnectionAttempt(address, false, Readable(ex.Message), ErrorCode(ex));
        }
    }

    private static bool CanReadAttributes(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the path that the directory APIs append to an error. The address is already part
    /// of the report, and the appended path makes the sentence read badly.
    /// </summary>
    internal static string Readable(string message)
    {
        var marker = message.LastIndexOf(" : '", StringComparison.Ordinal);
        return marker > 0 ? message[..marker].TrimEnd() : message;
    }

    /// <summary>
    /// The Windows error code behind the exception, for example 5 for access denied. Only Win32
    /// errors have such a code; a framework error would produce a meaningless number.
    /// </summary>
    internal static int? ErrorCode(Exception exception)
    {
        var hresult = (uint)exception.HResult;
        if ((hresult & 0xFFFF0000) != 0x80070000) return null;

        var code = (int)(hresult & 0xFFFF);
        return code == 0 ? null : code;
    }
}
