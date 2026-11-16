namespace LastLogon.Core;

/// <summary>A profile folder as the matcher sees it.</summary>
/// <param name="FileTime">Local time of the profile file (or of the folder, when <paramref name="FileSource"/> says so).</param>
/// <param name="Candidates">The raw SIDs on the access list of the folder, null when the list could not be read.</param>
public sealed record ProfileFolder(string Name, DateTime? FileTime, ActivitySource FileSource, IReadOnlyList<string>? Candidates);

/// <summary>The account whose login records belong to a profile folder, or the reason there is none.</summary>
public sealed record FolderMatch(string? Sid, string? Reason)
{
    public bool IsMatch => Sid is not null;

    public static FolderMatch Of(string sid) => new(sid, null);

    public static FolderMatch None(string reason) => new(null, reason);
}

/// <summary>
/// The match of the one folder that By user looks at. When several accounts are on its list and the name could
/// not be resolved, <see cref="Ambiguous"/> holds them and the records decide (<see cref="ProfileFolderMatcher.ChooseByRecords"/>).
/// </summary>
public sealed record UserMatch(FolderMatch Match, IReadOnlyList<string> Ambiguous)
{
    public bool NeedsRecords => Ambiguous.Count > 0;
}

/// <summary>
/// Ties login records to profile folders. A record names a SID and a row is a folder, so the access list of the
/// folder is used: Windows gives the user of a profile an entry of their own on it. A record is never attributed
/// to a folder on a guess: where the rules do not single out one account, the folder has no match and says why.
/// </summary>
public static class ProfileFolderMatcher
{
    /// <summary>How close the time of a profile file has to be to a logout record for the file to count as written by that logout.</summary>
    public static readonly TimeSpan LogoutFileTimeTolerance = TimeSpan.FromMinutes(2);

    public const string AccessListUnreadable =
        "The access list of this profile folder could not be read, so the login records could not be matched to it.";

    public const string NoAccount =
        "No user account is on the access list of this profile folder, so the login records could not be matched to it.";

    public const string OnlyOwnersOfOtherFolders =
        "Only accounts that have a profile folder of their own are on the access list of this profile folder, so the login records could not be matched to it.";

    public const string Ambiguous =
        "More than one account on the access list of this profile folder has login records, so they could not be matched to it.";

    public const string AnotherProfileOfTheAccount =
        "This folder is another profile of an account that has more than one folder on this computer, and the login records could not be assigned to one of them.";

    public const string OtherAccountWithTheSameName =
        "This folder belongs to another account with the same name, so the login records of the account that was looked up do not apply to it.";

    /// <summary>
    /// Matches all the folders of one computer together, keyed by folder name (case-insensitive).
    /// <paramref name="profileUse"/> is asked only to tell the folders of a rebuilt profile apart.
    /// </summary>
    public static IReadOnlyDictionary<string, FolderMatch> AssignForComputer(
        IReadOnlyList<ProfileFolder> folders, LoginHistory history, Func<string, ProfileUse> profileUse)
    {
        var result = new Dictionary<string, FolderMatch>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, ProfileFolder>(StringComparer.OrdinalIgnoreCase);

        // 1. The candidates of a folder: the account SIDs on its list.
        var pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            byName[folder.Name] = folder;
            if (folder.Candidates is null)
            {
                result[folder.Name] = FolderMatch.None(AccessListUnreadable);
                continue;
            }

            var candidates = AccountSids(folder.Candidates);
            if (candidates.Count == 0)
            {
                result[folder.Name] = FolderMatch.None(NoAccount);
                continue;
            }

            pending[folder.Name] = candidates;
        }

        // 2. An account that is the only candidate of a folder owns it. Where it only stands beside others on the
        // list of another folder, it was given access there: strike it. A struck folder can become a folder with a
        // single candidate, so go on until nothing changes. One pass strikes every owner at once.
        bool changed;
        do
        {
            changed = false;
            var owners = pending.Values.Where(list => list.Count == 1).Select(list => list[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in pending.Keys.ToList())
            {
                var candidates = pending[name];
                if (candidates.Count < 2)
                {
                    continue;
                }

                var kept = candidates.Where(sid => !owners.Contains(sid)).ToList();
                if (kept.Count == candidates.Count)
                {
                    continue;
                }

                changed = true;
                if (kept.Count == 0)
                {
                    pending.Remove(name);
                    result[name] = FolderMatch.None(OnlyOwnersOfOtherFolders);
                }
                else
                {
                    pending[name] = kept;
                }
            }
        }
        while (changed);

        // 3. One candidate left: the folder is that account's. Several: the records decide.
        foreach (var (name, candidates) in pending)
        {
            result[name] = candidates.Count == 1 ? FolderMatch.Of(candidates[0]) : ChooseByRecords(candidates, history);
        }

        // 4. An account on several folders is a rebuilt profile (jdoe beside jdoe.old). Without records nothing has to
        // be chosen. With them, the records go to the folder Windows is shown to be using.
        var shared = result
            .Where(pair => pair.Value.IsMatch)
            .GroupBy(pair => pair.Value.Sid!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => (Sid: group.Key, Names: group.Select(pair => pair.Key).ToList()))
            .ToList();
        foreach (var (sid, names) in shared)
        {
            var records = history.Records.Where(record => SidText.Same(record.Sid, sid)).ToList();
            if (records.Count == 0)
            {
                continue;
            }

            var chosen = LoginRecords.NewestOpenLogin(records) is not null
                ? OnlyOne(names.Where(name => profileUse(name) == ProfileUse.InUse))
                : OnlyOne(names.Where(name => WrittenAtTheLogout(byName[name], records)));
            foreach (var name in names.Where(name => !string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                result[name] = FolderMatch.None(AnotherProfileOfTheAccount);
            }
        }

        return result;
    }

    /// <summary>
    /// The match of the one folder that By user looks at. A name that resolved to a SID is the match when that SID is
    /// on the list and rules the folder out when it is not (another account with the same name). Without a SID, one
    /// account on the list is the match and several are left to <see cref="ChooseByRecords"/>.
    /// </summary>
    public static UserMatch MatchForUser(IReadOnlyList<string>? candidates, string? resolvedSid)
    {
        if (candidates is null)
        {
            return Settled(FolderMatch.None(AccessListUnreadable));
        }

        var accounts = AccountSids(candidates);
        if (accounts.Count == 0)
        {
            return Settled(FolderMatch.None(NoAccount));
        }

        if (resolvedSid is not null && SidText.TryNormalize(resolvedSid, out var resolved))
        {
            return accounts.Contains(resolved, StringComparer.OrdinalIgnoreCase)
                ? Settled(FolderMatch.Of(resolved))
                : Settled(FolderMatch.None(OtherAccountWithTheSameName));
        }

        return accounts.Count == 1
            ? Settled(FolderMatch.Of(accounts[0]))
            : new UserMatch(FolderMatch.None(Ambiguous), accounts);
    }

    /// <summary>
    /// Several accounts are candidates: the one that has records on the computer is the match. None has: any of them
    /// gives the same answer, no records, so the first is taken. More than one has: no match.
    /// </summary>
    public static FolderMatch ChooseByRecords(IReadOnlyList<string> candidates, LoginHistory history)
    {
        if (candidates.Count == 0)
        {
            return FolderMatch.None(NoAccount);
        }

        var withRecords = candidates
            .Where(sid => history.Records.Any(record => SidText.Same(record.Sid, sid)))
            .ToList();
        return withRecords.Count switch
        {
            0 => FolderMatch.Of(candidates[0]),
            1 => FolderMatch.Of(withRecords[0]),
            _ => FolderMatch.None(Ambiguous)
        };
    }

    private static UserMatch Settled(FolderMatch match) => new(match, Array.Empty<string>());

    private static List<string> AccountSids(IEnumerable<string> list)
    {
        var sids = new List<string>();
        foreach (var entry in list)
        {
            if (SidText.TryNormalize(entry, out var sid) && SidText.IsAccountSid(sid) && !sids.Contains(sid))
            {
                sids.Add(sid);
            }
        }

        return sids;
    }

    // The profile file of a folder was written at the newest logout of the account. The time of a folder that has no
    // NTUSER.DAT says nothing about a logout and never counts.
    private static bool WrittenAtTheLogout(ProfileFolder folder, IReadOnlyList<LoginRecord> records)
    {
        if (folder.FileSource != ActivitySource.NtUserDat || folder.FileTime is not { } fileTime
            || LoginRecords.NewestLogout(records) is not { } logout)
        {
            return false;
        }

        return (new DateTimeOffset(fileTime) - logout).Duration() <= LogoutFileTimeTolerance;
    }

    private static string? OnlyOne(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 1 ? list[0] : null;
    }
}
