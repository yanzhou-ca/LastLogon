using System.Globalization;

namespace LastLogon.Core;

/// <summary>
/// The rules of "What a row says". From the login records of a computer, the folder the records of a user were matched
/// to and a check of whether Windows holds the profile open, it decides whether the user is logged in now and since
/// when, which times the row shows and what the Details say. Plain code: it reads and writes nothing.
/// </summary>
public static class LoginAssessor
{
    public const string UnreadableLogs = "The login records could not be read.";

    public const string NoRecordsAtAll =
        "The event logs of this computer hold no login or logout records, so they say nothing about this user.";

    public const string NoRecordsInTheLogThatWasRead =
        "The event log that could be read holds no login or logout records, so it says nothing about this user.";

    public const string Undatable =
        "The event logs hold no login record for this user and the date they begin could not be read, so a missing login cannot be dated.";

    public const string FromTheProfileFile = "The times come from the profile file, which Windows writes at logoff.";

    public const string FromRecords = "The times come from the login records in the event logs of this computer";

    public const string SessionEnded =
        "The last login has no logout record, but Windows no longer holds the profile open, so that session is over.";

    public const string NoLoginSince =
        "No login is recorded since then, so the last logout time is the time of the profile file.";

    public const string FileNotALogin =
        "The profile file was written after the records begin, but no login is recorded, so something other than a login wrote it.";

    public const string CutShort = "The read was cut short, so older records are missing.";

    /// <summary>
    /// The row of one profile folder. <paramref name="profileUse"/> says whether Windows holds the profile open; it is not
    /// called for a user whose records already say they are logged out, because the check opens the file of the profile.
    /// </summary>
    public static LoginInfo Assess(FolderMatch match, ProfileFolder folder, LoginHistory history, Func<ProfileUse> profileUse)
    {
        var records = match.IsMatch
            ? history.Records.Where(record => SidText.Same(record.Sid, match.Sid)).ToList()
            : new List<LoginRecord>();

        // The records can be used for a folder that has a match, from logs that were read and that hold records of
        // someone, when they say something about this user: a record of theirs, or the date they begin.
        var usable = history.AnyLogRead && history.AnyRecordOfAnyone;
        var used = usable && match.IsMatch && (records.Count > 0 || history.RecordsBegin is not null);

        var open = LoginRecords.NewestOpenLogin(records);
        var loggedOut = used && records.Count > 0 && open is null;
        var use = loggedOut ? ProfileUse.Unknown : profileUse();

        var loggedIn = LoggedInState.No;
        DateTimeOffset? since = null;
        if (used && open is { } openLogin)
        {
            // The records say a session is open and the profile says whether Windows still holds it.
            if (use != ProfileUse.NotInUse)
            {
                loggedIn = LoggedInState.Yes;
                since = openLogin;
            }
        }
        else if (!loggedOut)
        {
            // Nothing in the records about this user: only the profile can tell. Logged in since an unknown time.
            loggedIn = use switch
            {
                ProfileUse.InUse => LoggedInState.Yes,
                ProfileUse.NotInUse => LoggedInState.No,
                _ => LoggedInState.Unknown
            };
        }

        // The time of the profile file is shown as the logout when the records cannot be used, and for a user who has
        // no record when the file is older than the records, so that it cannot be the write of a later login.
        DateTime? logoutFile = null;
        if (!used)
        {
            logoutFile = folder.FileTime;
        }
        else if (records.Count == 0 && folder.FileTime is { } fileTime
                 && history.RecordsBegin is { } begin && new DateTimeOffset(fileTime) < begin)
        {
            logoutFile = fileTime;
        }

        return new LoginInfo(
            loggedIn,
            since,
            used ? LoginRecords.NewestLogin(records) : null,
            used ? LoginRecords.NewestLogout(records) : null,
            logoutFile,
            used ? history.RecordsBegin : null,
            used,
            used
                ? UsedDetails(folder, history, records, open, use, logoutFile)
                : UnusedDetails(match, history));
    }

    /// <summary>
    /// The rows of every profile folder of a computer, keyed by folder name (case-insensitive). The folders are matched to
    /// the records together, and <paramref name="profileUse"/> is asked at most once per folder.
    /// </summary>
    public static IReadOnlyDictionary<string, LoginInfo> AssessComputer(
        IReadOnlyList<ProfileFolder> folders, LoginHistory history, Func<string, ProfileUse> profileUse)
    {
        var answers = new Dictionary<string, ProfileUse>(StringComparer.OrdinalIgnoreCase);
        ProfileUse Asked(string name)
        {
            if (!answers.TryGetValue(name, out var use))
            {
                use = profileUse(name);
                answers[name] = use;
            }

            return use;
        }

        var matches = ProfileFolderMatcher.AssignForComputer(folders, history, Asked);
        var result = new Dictionary<string, LoginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            result[folder.Name] = Assess(matches[folder.Name], folder, history, () => Asked(folder.Name));
        }

        return result;
    }

    private static string UsedDetails(
        ProfileFolder folder,
        LoginHistory history,
        IReadOnlyList<LoginRecord> records,
        DateTimeOffset? open,
        ProfileUse use,
        DateTime? logoutFile)
    {
        var sentences = new List<string>
        {
            FromRecords + (history.RecordsBegin is { } begin ? ", which go back to " + LocalDate(begin) + "." : ".")
        };

        if (open is not null && use == ProfileUse.NotInUse)
        {
            sentences.Add(SessionEnded);
        }

        if (records.Count == 0 && folder.FileTime is not null)
        {
            sentences.Add(logoutFile is not null ? NoLoginSince : FileNotALogin);
        }

        sentences.AddRange(FailedLogSentences(history));

        if (history.Logs.Any(log => log.Outcome is LoginLogOutcome.CutShort or LoginLogOutcome.TimedOut))
        {
            sentences.Add(CutShort);
        }

        return string.Join(" ", sentences);
    }

    private static string UnusedDetails(FolderMatch match, LoginHistory history)
    {
        var sentences = new List<string>();
        if (!match.IsMatch)
        {
            sentences.Add(match.Reason ?? string.Empty);
        }
        else if (!history.AnyLogRead)
        {
            var failure = history.Logs.Select(log => log.Failure).FirstOrDefault(text => !string.IsNullOrEmpty(text));
            sentences.Add(failure is null ? UnreadableLogs : UnreadableLogs + " " + failure);
        }
        else
        {
            // "The logs hold no records" is only said of logs that were all read. When one could not be read, what is
            // known is about the other one, and the log that failed is named, because it is part of the reason.
            sentences.Add(history.AnyRecordOfAnyone
                ? Undatable
                : history.Logs.All(log => log.WasRead) ? NoRecordsAtAll : NoRecordsInTheLogThatWasRead);
            sentences.AddRange(FailedLogSentences(history));
        }

        sentences.Add(FromTheProfileFile);
        return string.Join(" ", sentences.Where(sentence => sentence.Length > 0));
    }

    private static IEnumerable<string> FailedLogSentences(LoginHistory history)
        => history.Logs
            .Where(result => !result.WasRead)
            .Select(result => $"The {LogTitle(result.Log)} log could not be used. {result.Failure}".TrimEnd());

    private static string LogTitle(LoginLog log)
        => log == LoginLog.System ? "System" : "User Profile Service";

    private static string LocalDate(DateTimeOffset time)
        => time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
