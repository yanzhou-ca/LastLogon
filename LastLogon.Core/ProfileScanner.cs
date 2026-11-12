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
    {
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
            }, ct);
            return results.OrderByDescending(r => r.LastActivity ?? DateTime.MinValue).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new[] { Failure(computer, "", ex) };
        }
    }

    public IAsyncEnumerable<ProfileResult> ScanComputersAsync(
        IEnumerable<string> computers, CancellationToken ct)
        => ScanOnComputersAsync<ProfileResult>(computers, ScanComputerAsync, ct);

    public IAsyncEnumerable<ProfileResult> FindUserAsync(
        string username, IEnumerable<string> computers, CancellationToken ct)
        => ScanOnComputersAsync<ProfileResult>(computers, async (computer, token) =>
        {
            var result = await CheckUserAsync(computer, username, token);
            return result is null ? Array.Empty<ProfileResult>() : new[] { result };
        }, ct);

    private async IAsyncEnumerable<T> ScanOnComputersAsync<T>(
        IEnumerable<string> computers,
        Func<string, CancellationToken, Task<IReadOnlyList<T>>> scanComputer,
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
            try
            {
                var results = await scanComputer(computer, ct);
                foreach (var result in results)
                    await channel.Writer.WriteAsync(result, CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { gate.Release(); }
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

    private async Task<ProfileResult?> CheckUserAsync(string computer, string username, CancellationToken ct)
    {
        try
        {
            return await RunWithTimeout(() =>
            {
                var folders = _fs.ListUserFolders(computer);
                var match = folders.FirstOrDefault(f => f.Equals(username, StringComparison.OrdinalIgnoreCase))
                    ?? folders.FirstOrDefault(f => f.StartsWith(username + ".", StringComparison.OrdinalIgnoreCase));
                return match is null ? null : ReadProfile(computer, match);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Failure(computer, username, ex); }
    }

    private ProfileResult ReadProfile(string computer, string folder)
    {
        var t = _fs.GetNtUserDatTime(computer, folder);
        return t is not null
            ? new ProfileResult(computer, folder, t, ActivitySource.NtUserDat, ScanStatus.Ok, null)
            : new ProfileResult(computer, folder, _fs.GetFolderTime(computer, folder), ActivitySource.Folder, ScanStatus.Ok, "NTUSER.DAT not found; using folder time");
    }

    private async Task<T> RunWithTimeout<T>(Func<T> work, CancellationToken ct)
    {
        var task = Task.Run(work, CancellationToken.None);
        var delay = Task.Delay(_timeout, ct);
        var done = await Task.WhenAny(task, delay);
        if (done == task) return await task;
        ct.ThrowIfCancellationRequested();
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new TimeoutException("Timed out");
    }

    private static ProfileResult Failure(string computer, string user, Exception ex) => ex switch
    {
        UnauthorizedAccessException => new(computer, user, null, null, ScanStatus.AccessDenied, "Access denied"),
        TimeoutException or DirectoryNotFoundException => new(computer, user, null, null, ScanStatus.Offline, "Offline or C$ unreachable"),
        IOException => new(computer, user, null, null, ScanStatus.Offline, ex.Message),
        _ => new(computer, user, null, null, ScanStatus.Error, ex.Message),
    };
}
