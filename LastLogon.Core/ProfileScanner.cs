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
                foreach (var folder in _fs.ListUserFolders(computer))
                {
                    if (SystemProfiles.Contains(folder)) continue;
                    list.Add(ReadProfile(computer, folder));
                }
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
                var folders = _fs.ListUserFolders(computer);
                var match = folders.FirstOrDefault(f => f.Equals(username, StringComparison.OrdinalIgnoreCase))
                    ?? folders.FirstOrDefault(f => f.StartsWith(username + ".", StringComparison.OrdinalIgnoreCase));
                return match is null ? null : ReadProfile(computer, match);
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

        var guidance = RunContext.IsElevated
            ? $"The account {RunContext.Account} has to be an administrator on {computer}, "
              + "and the C$ share has to be enabled there. If the computer is reached under a "
              + "different name, address it by its short name or by its full name."
            : "The tool is not running elevated, so the administrative share (C$) of another "
              + "computer is refused. Start it with Run as administrator.";

        return reasonText + " " + guidance;
    }
}
