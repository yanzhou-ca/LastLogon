using System.Security.Principal;

namespace LastLogon.Core;

/// <summary>
/// Finds the SID of the account that the operator typed in By user, so that the login records of that account, and not
/// of another account with the same name, are used. The lookup asks the domain of this computer, never this computer's
/// own accounts.
/// </summary>
public static class WindowsUserSidResolver
{
    private const int MaxNameLength = 256;

    private static readonly TimeSpan LookupLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The SID of the account as text, or null when the name cannot be turned into an account name, does not exist or
    /// the lookup fails or takes longer than five seconds. Throws only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public static async Task<string?> ResolveAsync(string typedName, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = Task.Run(() => Translate(typedName), CancellationToken.None);
            _ = lookup.ContinueWith(
                task => { _ = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return await lookup.WaitAsync(LookupLimit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The name to look up. A domain account name (CORP\jdoe) and a user principal name (jdoe@corp.local) are used as typed.
    /// A plain name is completed with the DNS domain, when there is one: looked up as it is, it could match a local
    /// account of the technician's PC. Anything else is null.
    /// </summary>
    internal static string? AccountNameFor(string typedName, string? dnsDomain)
    {
        var name = typedName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return null;
        }

        var backslash = name.IndexOf('\\');
        if (backslash >= 0)
        {
            return name.IndexOf('\\', backslash + 1) < 0 && backslash > 0 && backslash < name.Length - 1 ? name : null;
        }

        var at = name.IndexOf('@');
        if (at >= 0)
        {
            return name.IndexOf('@', at + 1) < 0 && at > 0 && at < name.Length - 1 ? name : null;
        }

        return ComputerTarget.IsValidDnsDomain(dnsDomain) ? name + "@" + dnsDomain : null;
    }

    private static string? Translate(string typedName)
    {
        // The DNS domain is only looked up for a name that needs it: it can take a few seconds.
        var plain = typedName is not null && !typedName.Contains('\\') && !typedName.Contains('@');
        var name = AccountNameFor(typedName ?? string.Empty, plain ? ComputerTarget.DnsDomain : null);
        if (name is null)
        {
            return null;
        }

        try
        {
            var sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            return SidText.TryNormalize(sid.Value, out var normalised) ? normalised : null;
        }
        catch (Exception)
        {
            // The account does not exist, or no domain controller answered.
            return null;
        }
    }
}
