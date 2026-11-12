using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LastLogon.Core;

public class ProfileScanner
{
    private static readonly HashSet<string> SystemProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users", "desktop.ini"
    };

    private readonly IProfileFileSystem _fs;
    private readonly int _maxParallel;
    private readonly TimeSpan _timeout;

    public ProfileScanner(IProfileFileSystem fs, int maxParallel = 8, TimeSpan? perPcTimeout = null)
    {
        _fs = fs;
        _maxParallel = maxParallel;
        _timeout = perPcTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<IReadOnlyList<ProfileResult>> ScanComputerAsync(string computer, CancellationToken ct)
        => (await ScanComputerWithOutcomeAsync(computer, ct)).Results;

    public IAsyncEnumerable<ProfileResult> ScanComputersAsync(
        IEnumerable<string> computers, CancellationToken ct)
        => ScanOnComputersAsync<ProfileResult>(computers, ScanComputerWithOutcomeAsync, ct);

    public IAsyncEnumerable<ProfileResult> FindUserAsync(
        string username, IEnumerable<string> computers, CancellationToken ct)
        => ScanOnComputersAsync<ProfileResult>(computers, (computer, token) => CheckUserAsync(computer, username, token), ct);

    /// <summary>
    /// The outcome of scanning one computer, together with any work that is still running in the
    /// background because the remote call could not be interrupted.
    /// </summary>
    private sealed record ScanOutcome<T>(IReadOnlyList<T> Results, Task? PendingWork)
    {
        public static ScanOutcome<T> Empty { get; } = new(Array.Empty<T>(), null);
    }

    private async Task<ScanOutcome<ProfileResult>> ScanComputerWithOutcomeAsync(string computer, CancellationToken ct)
    {
        Task? pending = null;
        try
        {
            var results = await RunWithTimeout(() =>
            {
                var list = new List<ProfileResult>();
                UnauthorizedAccessException? firstDenied = null;
                foreach (var folder in _fs.ListUserFolders(computer))
                {
                    if (SystemProfiles.Contains(folder)) continue;

                    try
                    {
                        list.Add(ReadProfile(computer, folder));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // A folder whose ACL refuses this account says nothing about the other
                        // profiles, so it gets a row of its own and the scan goes on. Any other
                        // failure still ends the computer, because a lost connection would fail
                        // every profile after it in the same way.
                        firstDenied ??= ex;
                        list.Add(DeniedProfile(computer, folder, ex));
                    }
                }

                // When no profile could be read at all, the problem is the account or the share
                // and not one folder, so the computer is reported once, with the guidance.
                if (firstDenied is not null && list.All(r => r.Status == ScanStatus.AccessDenied))
                    throw firstDenied;

                return list;
            }, ct, task => pending = task);

            if (results.Count == 0)
            {
                // A reachable computer without user profiles still gets a row, so that it is not
                // mistaken for a computer that was never scanned.
                return new ScanOutcome<ProfileResult>(
                    new[]
                    {
                        new ProfileResult(computer, "", null, null, ScanStatus.NoProfile, "No user profiles found.")
                    },
                    null);
            }

            return new ScanOutcome<ProfileResult>(
                results.OrderByDescending(r => r.LastActivity ?? DateTime.MinValue).ToList(),
                null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ScanOutcome<ProfileResult>(new[] { Failure(computer, "", ex) }, pending);
        }
    }

    private async IAsyncEnumerable<T> ScanOnComputersAsync<T>(
        IEnumerable<string> computers,
        Func<string, CancellationToken, Task<ScanOutcome<T>>> scanComputer,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<T>();
        using var gate = new SemaphoreSlim(_maxParallel);

        var producers = computers.Select(async computer =>
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

            var releaseGate = true;
            try
            {
                var outcome = await scanComputer(computer, ct);
                foreach (var result in outcome.Results)
                    await channel.Writer.WriteAsync(result, CancellationToken.None);

                if (outcome.PendingWork is not null)
                {
                    // The remote call timed out but cannot be interrupted and is still holding
                    // an SMB connection open. The slot stays taken until it ends so that the
                    // number of live connections stays bounded.
                    releaseGate = false;
                    _ = outcome.PendingWork.ContinueWith(
                        _ => ReleaseGate(gate),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { if (releaseGate) gate.Release(); }
        }).ToList();

        var producersTask = Task.WhenAll(producers);
        _ = producersTask.ContinueWith(
            task => channel.Writer.TryComplete(task.Exception),
            TaskScheduler.Default);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                if (ct.IsCancellationRequested) yield break;
                yield return item;
            }
        }
        finally
        {
            await producersTask;
        }
    }

    private static void ReleaseGate(SemaphoreSlim gate)
    {
        try
        {
            gate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The scan finished while the abandoned call was still running.
        }
    }

    private async Task<ScanOutcome<ProfileResult>> CheckUserAsync(string computer, string username, CancellationToken ct)
    {
        Task? pending = null;
        try
        {
            var result = await RunWithTimeout(() =>
            {
                // A known profile folder can be read without listing C:\Users. Some estates
                // deny the listing but allow reading a known profile, and the by-user lookup
                // already knows the folder name it is looking for.
                Exception? directError = null;
                try
                {
                    var direct = TryReadKnownProfile(computer, username);
                    if (direct is not null) return direct;
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Keep the more specific file error for the case where the listing is
                    // refused as well; a suffixed folder may still be found by the listing.
                    directError = ex;
                }

                try
                {
                    var folders = _fs.ListUserFolders(computer);
                    var match = folders.FirstOrDefault(f => f.Equals(username, StringComparison.OrdinalIgnoreCase))
                        ?? folders.FirstOrDefault(f => f.StartsWith(username + ".", StringComparison.OrdinalIgnoreCase));
                    return match is null ? null : ReadProfile(computer, match);
                }
                catch when (directError is not null)
                {
                    throw directError;
                }
            }, ct, task => pending = task);

            return result is null
                ? ScanOutcome<ProfileResult>.Empty
                : new ScanOutcome<ProfileResult>(new[] { result }, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new ScanOutcome<ProfileResult>(new[] { Failure(computer, username, ex) }, pending);
        }
    }

    /// <summary>
    /// Reads the profile folder the operator named, without listing C:\Users. Null when the
    /// folder has no NTUSER.DAT, so that a folder with a domain suffix can still be found by
    /// the listing.
    /// </summary>
    private ProfileResult? TryReadKnownProfile(string computer, string username)
    {
        if (!InputParser.IsSafeProfileFolderName(username)) return null;

        DateTime? time;
        try
        {
            time = _fs.GetNtUserDatTime(computer, username);
        }
        catch (IOException)
        {
            // A missing folder is found by the listing, which also knows about domain suffixes.
            return null;
        }

        return time is null
            ? null
            : new ProfileResult(computer, username, time, ActivitySource.NtUserDat, ScanStatus.Ok, null);
    }

    private ProfileResult ReadProfile(string computer, string folder)
    {
        var t = _fs.GetNtUserDatTime(computer, folder);
        return t is not null
            ? new ProfileResult(computer, folder, t, ActivitySource.NtUserDat, ScanStatus.Ok, null)
            : new ProfileResult(computer, folder, _fs.GetFolderTime(computer, folder), ActivitySource.Folder, ScanStatus.Ok, "NTUSER.DAT not found; using folder time");
    }

    private async Task<T> RunWithTimeout<T>(Func<T> work, CancellationToken ct, Action<Task>? onTimeout = null)
    {
        var task = Task.Run(work, CancellationToken.None);
        var delay = Task.Delay(_timeout, ct);
        var done = await Task.WhenAny(task, delay);
        if (done == task) return await task;
        ct.ThrowIfCancellationRequested();

        // The remote call cannot be interrupted, so it keeps running in the background. It is
        // handed to the caller so that the concurrency slot is not released while the call is
        // still holding a connection open.
        onTimeout?.Invoke(task);
        _ = task.ContinueWith(
            t => { _ = t.Exception; },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        throw new TimeoutException("Timed out");
    }

    /// <summary>
    /// The row for one profile folder that could not be read while the others could. It keeps the
    /// reason from the system, which names the file, and says that the account and the share are
    /// fine, so that the operator does not go looking for missing administrator rights.
    /// </summary>
    private static ProfileResult DeniedProfile(string computer, string folder, UnauthorizedAccessException exception)
    {
        var reason = exception.Message.Trim();
        var text = reason.Length == 0 ? "Access denied" : "Access denied: " + reason;
        if (!text.EndsWith('.')) text += ".";

        return new ProfileResult(
            computer,
            folder,
            null,
            null,
            ScanStatus.AccessDenied,
            text + " The other profiles on this computer could be read.");
    }

    private static ProfileResult Failure(string computer, string user, Exception ex) => ex switch
    {
        UnauthorizedAccessException access => new(
            computer, user, null, null, ScanStatus.AccessDenied, Describe(access, computer)),
        TimeoutException => new(computer, user, null, null, ScanStatus.TimedOut, "Timed out"),
        DirectoryNotFoundException => new(computer, user, null, null, ScanStatus.Offline, "Offline or C$ unreachable"),
        IOException io => new(computer, user, null, null, ScanStatus.Offline, io.Message),
        _ => new(computer, user, null, null, ScanStatus.Error, ex.Message),
    };

    /// <summary>
    /// Keeps the reason a share could not be read and says what is needed to read it, because
    /// "Access denied" on its own does not say whether the tool is filtered, the account is not
    /// an administrator on the target or the share has been turned off.
    /// </summary>
    private static string Describe(UnauthorizedAccessException exception, string computer)
    {
        var reason = exception.Message.Trim();
        var reasonText = reason.Length == 0 ? "Access denied" : "Access denied: " + reason;

        var addressed = DescribeAddressing(
            computer, ComputerTarget.DnsDomain, ComputerTarget.AddressingMode);

        var guidance = RunContext.IsElevated
            ? $"The account {RunContext.Account} has to be an administrator on {computer}, "
              + "and the C$ share has to be enabled there. If the folder can be read but not "
              + "listed, use By user with the exact profile folder name."
            : "The tool is not running elevated, so the administrative share (C$) of another "
              + "computer is refused. Start it with Run as administrator.";

        return reasonText + " " + addressed + " " + guidance;
    }

    /// <summary>
    /// Says which name the failed connection used, because a name that reaches one machine does
    /// not have to reach the machine that owns the profile.
    /// </summary>
    internal static string DescribeAddressing(
        string computer, string? dnsDomain, ComputerTarget.Addressing addressing)
    {
        if (!InputParser.IsShortComputerName(computer))
        {
            return $"The computer was entered as the fully qualified name {computer}. If the "
                   + "short name is the one that works, enter only the first label and choose "
                   + "Short name.";
        }

        if (addressing == ComputerTarget.Addressing.ShortName)
            return $"The computer was addressed by its short name ({computer}).";

        var qualified = ComputerTarget.Qualify(
            computer, dnsDomain, ComputerTarget.Addressing.DomainQualified);
        if (qualified.Equals(computer, StringComparison.OrdinalIgnoreCase))
        {
            return $"The computer was addressed by its short name ({computer}) because no DNS "
                   + "domain could be determined.";
        }

        return $"The computer was addressed as {qualified} (domain-qualified). If the short "
               + "name is the one that works, choose Short name.";
    }
}
