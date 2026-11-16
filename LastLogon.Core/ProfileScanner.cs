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

    private readonly Func<string, ConnectionTestResult>? _connectionProbe;
    private readonly ILoginHistorySource? _loginHistory;
    private readonly Func<string, CancellationToken, Task<string?>>? _resolveUserSid;
    private readonly TimeSpan _loginHistoryTimeout;

    /// <param name="loginHistory">
    /// Where the login records of a computer come from. Without it the rows are what they always were: the time of the
    /// profile file.
    /// </param>
    /// <param name="resolveUserSid">Turns the name typed in By user into the SID of the account, or null when it cannot.</param>
    /// <param name="loginHistoryTimeout">How long the records of one computer may take. Ten seconds, as in Logon history.</param>
    public ProfileScanner(
        IProfileFileSystem fs,
        int maxParallel = 8,
        TimeSpan? perPcTimeout = null,
        Func<string, ConnectionTestResult>? connectionProbe = null,
        ILoginHistorySource? loginHistory = null,
        Func<string, CancellationToken, Task<string?>>? resolveUserSid = null,
        TimeSpan? loginHistoryTimeout = null)
    {
        _connectionProbe = connectionProbe;
        _loginHistory = loginHistory;
        _resolveUserSid = resolveUserSid;
        _loginHistoryTimeout = loginHistoryTimeout ?? TimeSpan.FromSeconds(10);
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
    private sealed record ScanOutcome<T>(IReadOnlyList<T> Results, Task? PendingWork);

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

            var (rows, pendingLogin) = await AddLoginAsync(computer, results, ct);
            return new ScanOutcome<ProfileResult>(
                rows.OrderBy(r => r, Comparer<ProfileResult>.Create(ProfileResult.CompareForDisplay)).ToList(),
                pendingLogin);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var tried = await TryTheNamesAsync(ex, computer, ct);
            return new ScanOutcome<ProfileResult>(new[] { Failure(computer, "", ex, tried) }, pending);
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

                    // A computer that was reached and has no such profile still gets a row, so
                    // that "looked and found nothing" is not mistaken for "never looked".
                    return match is null ? NoProfile(computer, username) : ReadProfile(computer, match);
                }
                catch (Exception) when (directError is not null)
                {
                    throw directError;
                }
                catch (UnauthorizedAccessException)
                {
                    // The exact folder name was looked for and is not there, and the listing is
                    // refused. The computer answered, so this is not a rights problem to report
                    // as one: it is a search that could only be made halfway.
                    return NoProfileThatCouldNotBeSearched(computer, username);
                }
            }, ct, task => pending = task);

            return new ScanOutcome<ProfileResult>(new[] { result }, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var tried = await TryTheNamesAsync(ex, computer, ct);
            return new ScanOutcome<ProfileResult>(new[] { Failure(computer, username, ex, tried) }, pending);
        }
    }

    /// <summary>
    /// For a computer that answered "access denied", reports which address each way of naming it
    /// resolves to and whether it can be read through that name. The answer of a computer says
    /// nothing about the account when the name reaches a different computer than the one meant.
    /// <para>
    /// Only an access failure of the whole computer is looked into. An unreachable computer would
    /// only add waiting to a bulk scan. Null when there is nothing to add: no probe, another kind
    /// of failure, a probe that fails, or one that takes longer than three times the time a
    /// computer is given. The row then stays as it was.
    /// </para>
    /// </summary>
    private async Task<string?> TryTheNamesAsync(Exception failure, string computer, CancellationToken ct)
    {
        if (_connectionProbe is null || failure is not UnauthorizedAccessException) return null;

        try
        {
            var probe = Task.Run(() => _connectionProbe(computer), CancellationToken.None);
            _ = probe.ContinueWith(
                t => { _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            var result = await probe.WaitAsync(_timeout * 3, ct);
            var text = result.DescribeAttempts();
            return text.Length == 0 ? null : text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
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

    /// <summary>
    /// What the login records say about the rows of one computer. The records, the access lists and the in-use checks
    /// are separate steps, each with a time limit of its own, and a step that fails or hangs only makes the rows fall
    /// back to the time of the profile file. So nothing here can turn a computer whose profiles could be read into a
    /// failure. Work that cannot be interrupted is returned, so that the caller keeps the slot until it ends.
    /// </summary>
    private async Task<(List<ProfileResult> Rows, Task? Pending)> AddLoginAsync(
        string computer, List<ProfileResult> rows, CancellationToken ct)
    {
        var readable = rows.Where(row => row.Status == ScanStatus.Ok).ToList();
        if (_loginHistory is null || readable.Count == 0)
        {
            return (rows, null);
        }

        var abandoned = new List<Task>();
        try
        {
            var lists = await ReadAccessListsAsync(computer, readable.Select(row => row.Username).ToList(), ct, abandoned);
            var folders = readable
                .Select(row => new ProfileFolder(row.Username, row.LastActivity, row.Source ?? ActivitySource.Folder, lists[row.Username]))
                .ToList();
            var history = await ReadHistoryAsync(computer, null, ct, abandoned);
            var infos = await AssessAsync(computer, folders, history, ct, abandoned);

            var result = rows
                .Select(row => row.Status == ScanStatus.Ok && infos.TryGetValue(row.Username, out var info) ? ApplyLogin(row, info) : row)
                .ToList();
            return (result, PendingOf(abandoned));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Something unforeseen in the login steps must not cost the rows that were read.
            return (rows, PendingOf(abandoned));
        }
    }

    private static Task? PendingOf(List<Task> abandoned) => abandoned.Count == 0 ? null : Task.WhenAll(abandoned);

    /// <summary>
    /// The access list of each folder, null for a folder whose list cannot be read. When the step runs out of time every
    /// list is null and the call that cannot be interrupted is handed to <paramref name="abandoned"/>.
    /// </summary>
    private async Task<Dictionary<string, IReadOnlyList<string>?>> ReadAccessListsAsync(
        string computer, IReadOnlyList<string> folders, CancellationToken ct, List<Task> abandoned)
    {
        try
        {
            return await RunWithTimeout(() =>
            {
                var lists = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                {
                    try
                    {
                        lists[folder] = _fs.GetFolderAccessSids(computer, folder);
                    }
                    catch (Exception)
                    {
                        lists[folder] = null;
                    }
                }

                return lists;
            }, ct, abandoned.Add);
        }
        catch (TimeoutException)
        {
            return folders.ToDictionary(folder => folder, _ => (IReadOnlyList<string>?)null, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The login history of a computer, for one user when a SID is given. It never throws except for the cancellation of
    /// the scan: a read that fails or runs out of time is a history that says it could not be read. The limit is the
    /// one the reader works to plus a grace, for a source that cannot be interrupted.
    /// </summary>
    private async Task<LoginHistory> ReadHistoryAsync(string computer, string? sid, CancellationToken ct, List<Task> abandoned)
    {
        var grace = _loginHistoryTimeout < TimeSpan.FromSeconds(5) ? _loginHistoryTimeout : TimeSpan.FromSeconds(5);
        try
        {
            return await RunWithTimeout(
                () => _loginHistory!.Read(computer, sid, _loginHistoryTimeout, ct),
                ct,
                abandoned.Add,
                _loginHistoryTimeout + grace);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return LoginHistory.Unavailable("Reading the event logs timed out.");
        }
        catch (Exception ex)
        {
            return LoginHistory.Unavailable("The event logs could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// What the records say about each folder. The in-use check opens a file on the computer, so it has a limit of its
    /// own: when it runs out, the rows are assessed again with a check that cannot tell.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, LoginInfo>> AssessAsync(
        string computer, IReadOnlyList<ProfileFolder> folders, LoginHistory history, CancellationToken ct, List<Task> abandoned)
    {
        try
        {
            return await RunWithTimeout(
                () => LoginAssessor.AssessComputer(folders, history, folder => UseOf(computer, folder)),
                ct,
                abandoned.Add);
        }
        catch (TimeoutException)
        {
            return LoginAssessor.AssessComputer(folders, history, _ => ProfileUse.Unknown);
        }
    }

    private ProfileUse UseOf(string computer, string folder)
    {
        try
        {
            return _fs.GetProfileUse(computer, folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProfileUse.Unknown;
        }
    }

    /// <summary>
    /// The row with its login facts. The records are the source of the times unless the row rests on the profile file,
    /// and what the records say is added to the message the row already had.
    /// </summary>
    private static ProfileResult ApplyLogin(ProfileResult row, LoginInfo info)
    {
        var message = string.Join(" ", new[] { row.Message, info.Details }.Where(text => !string.IsNullOrEmpty(text)));
        return row with
        {
            Login = info,
            Source = info.RecordsUsed && !info.IsApproximate ? ActivitySource.LoginRecords : row.Source,
            Message = message.Length == 0 ? null : message
        };
    }

    private async Task<T> RunWithTimeout<T>(
        Func<T> work, CancellationToken ct, Action<Task>? onTimeout = null, TimeSpan? timeout = null)
    {
        var task = Task.Run(work, CancellationToken.None);
        var delay = Task.Delay(timeout ?? _timeout, ct);
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

    /// <summary>The row for a reached computer that has no profile for the user that was asked for.</summary>
    private static ProfileResult NoProfile(string computer, string username)
        => new(
            computer,
            username,
            null,
            null,
            ScanStatus.NoProfile,
            $"No profile folder for {username} was found on this computer.");

    /// <summary>
    /// The row for a reached computer where no folder with the exact name was found and the folder
    /// of all profiles cannot be listed, so a folder with a suffix could not be looked for.
    /// </summary>
    private static ProfileResult NoProfileThatCouldNotBeSearched(string computer, string username)
        => new(
            computer,
            username,
            null,
            null,
            ScanStatus.NoProfile,
            $"No NTUSER.DAT was found in a folder named exactly {username}. C:\\Users cannot be listed "
            + $"with this account, so a folder with a suffix (for example {username}.DOMAIN) could not "
            + "be looked for.");

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

    private static ProfileResult Failure(string computer, string user, Exception ex, string? tried = null) => ex switch
    {
        UnauthorizedAccessException access => new(
            computer, user, null, null, ScanStatus.AccessDenied, Describe(access, computer, tried)),
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
    private static string Describe(UnauthorizedAccessException exception, string computer, string? tried = null)
    {
        var reason = exception.Message.Trim();
        var reasonText = reason.Length == 0 ? "Access denied" : "Access denied: " + reason;

        // The names and addresses come straight after the reason, because the grid trims a long
        // row and they are what tells a refused account from a wrong computer.
        if (!string.IsNullOrEmpty(tried)) reasonText += " " + tried;

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
