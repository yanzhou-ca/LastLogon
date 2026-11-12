using System.Text;
using System.Globalization;
using System.Text.Json;

namespace LastLogon.Core;

public static class ResultExporter
{
    private static readonly string[] Header =
        { "Computer", "Username", "LastProfileActivity", "DaysAgo", "Source", "Status", "Message" };
    private static readonly string[] LogonEventHeader =
    {
        "Computer", "LocalTime", "TimeUtc", "EventId", "EventName", "AccountName",
        "AccountDomain", "AccountSid", "LogonType", "LogonTypeName", "SourceAddress", "Details"
    };

    public static string ToCsv(IEnumerable<ProfileResult> rows) => Build(rows, ",", Csv);

    public static string ToTabSeparated(IEnumerable<ProfileResult> rows) => Build(rows, "\t", TabSeparated);

    public static string ToLogonEventCsv(IEnumerable<LogonEventRecord> rows)
        => BuildLogonEvents(rows, ",", Csv);

    public static string ToLogonEventTabSeparated(IEnumerable<LogonEventRecord> rows)
        => BuildLogonEvents(rows, "\t", TabSeparated);

    private static string Build(IEnumerable<ProfileResult> rows, string sep, Func<string, string> esc)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(sep, Header)).Append("\r\n");
        foreach (var r in rows)
        {
            var cells = new[]
            {
                r.Computer, r.Username,
                r.LastActivity?.ToString("yyyy-MM-dd HH:mm") ?? "",
                r.DaysAgo?.ToString() ?? "",
                r.Source?.ToString() ?? "",
                r.Status.ToString(), r.Message ?? ""
            };
            sb.Append(string.Join(sep, cells.Select(esc))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string BuildLogonEvents(
        IEnumerable<LogonEventRecord> rows,
        string separator,
        Func<string, string> escape)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(separator, LogonEventHeader.Select(escape))).Append("\r\n");
        foreach (var row in rows)
        {
            var timeUtc = row.TimeUtc.ToUniversalTime();
            var cells = new[]
            {
                row.Computer,
                timeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                timeUtc.ToString("O", CultureInfo.InvariantCulture),
                row.EventId.ToString(CultureInfo.InvariantCulture),
                row.EventName,
                row.AccountName ?? "",
                row.AccountDomain ?? "",
                row.AccountSid ?? "",
                row.LogonType?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.LogonTypeName ?? "",
                row.SourceAddress ?? "",
                JsonSerializer.Serialize(row.Data)
            };
            builder.Append(string.Join(separator, cells.Select(escape))).Append("\r\n");
        }

        return builder.ToString();
    }

    private static string Csv(string s)
    {
        if (HasFormulaPrefix(s)) s = "'" + s;
        return s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>
    /// Escapes a value for tab-separated output.
    /// <para>
    /// Tab-separated text is pasted straight into a spreadsheet, which has no quoting rules: a
    /// cell that starts with a formula trigger is evaluated, and a value containing a line
    /// break starts a new row. Both are neutralised here so that the clipboard output cannot
    /// execute anything in the receiving spreadsheet or forge rows.
    /// </para>
    /// </summary>
    private static string TabSeparated(string s)
    {
        s = s.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
        if (HasFormulaPrefix(s)) s = "'" + s;
        return s;
    }

    private static bool HasFormulaPrefix(string value)
    {
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)) continue;
            return character is '=' or '+' or '-' or '@';
        }
        return false;
    }
}
