using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LastLogon.Core;

public enum LookupMode { ByComputer, ByUser }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ProfileScanner _scanner;
    private readonly Action<Action> _dispatch;
    private readonly Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>> _searchAd;

    // By computer and By user answer different questions, so each keeps its own rows and its own
    // status line. Switching between them shows the rows of the lookup that is on screen and never
    // those of the other one.
    private readonly Dictionary<LookupMode, ObservableCollection<ProfileResult>> _results = new()
    {
        [LookupMode.ByComputer] = new(),
        [LookupMode.ByUser] = new(),
    };

    private readonly Dictionary<LookupMode, string> _status = new()
    {
        [LookupMode.ByComputer] = "Ready",
        [LookupMode.ByUser] = "Ready",
    };

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _adCts;
    private int _scanGeneration;
    private LookupMode _mode;
    private string _computersText = "";
    private string _username = "";
    private bool _isScanning;
    private bool _isSearchingAd;
    private ValidationIssue? _validation;
    private int _scanTotal;
    private int _scanCompleted;

    /// <param name="searchAd">Looks computer names up in Active Directory. The tests replace it.</param>
    public MainViewModel(
        ProfileScanner scanner,
        Action<Action>? dispatch = null,
        Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>>? searchAd = null)
    {
        _scanner = scanner;
        _dispatch = dispatch ?? (a => a());
        _searchAd = searchAd ?? AdComputerSearch.SearchAsync;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The rows of the lookup that is on screen. The collection changes with <see cref="Mode"/>.</summary>
    public ObservableCollection<ProfileResult> Results => _results[_mode];

    public int StaleDays { get; set; } = 90;

    public LookupMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Notify(nameof(Mode));
            Notify(nameof(Results));
            Notify(nameof(StatusText));
        }
    }

    public string ComputersText { get => _computersText; set => Set(ref _computersText, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    /// <summary>True while a directory search is running, so that it can be cancelled too.</summary>
    public bool IsSearchingAd { get => _isSearchingAd; private set => Set(ref _isSearchingAd, value); }

    /// <summary>The status line of the lookup that is on screen.</summary>
    public string StatusText => _status[_mode];

    /// <summary>
    /// The input problem that stopped the last scan from starting, or null. It belongs next to
    /// the field it names; it is deliberately not part of <see cref="StatusText"/>.
    /// </summary>
    public ValidationIssue? Validation { get => _validation; private set => Set(ref _validation, value); }

    /// <summary>How many computers the running (or last) scan was asked to look at.</summary>
    public int ScanTotal { get => _scanTotal; private set => Set(ref _scanTotal, value); }

    /// <summary>How many of those computers have answered, with a profile or with a reason.</summary>
    public int ScanCompleted { get => _scanCompleted; private set => Set(ref _scanCompleted, value); }

    public bool IsStale(ProfileResult r) => r.DaysAgo is int d && d > StaleDays;

    public void ClearValidation() => Validation = null;

    public void Cancel()
    {
        _cts?.Cancel();
        _adCts?.Cancel();
    }

    public async Task ScanAsync()
    {
        var mode = Mode;
        var results = _results[mode];

        IReadOnlyList<string> computers;
        try
        {
            computers = InputParser.ParseComputers(ComputersText);
        }
        catch (FormatException ex)
        {
            results.Clear();
            Validation = new ValidationIssue(ValidationField.Computers, ex.Message);
            return;
        }

        var user = InputParser.NormalizeUsername(Username);

        if (computers.Count == 0)
        {
            Validation = new ValidationIssue(ValidationField.Computers, "Enter at least one computer name.");
            return;
        }

        if (mode == LookupMode.ByUser && user.Length == 0)
        {
            Validation = new ValidationIssue(ValidationField.Username, "Enter a username.");
            return;
        }

        Validation = null;

        // A scan that is still running is replaced. It is cancelled before the list is cleared,
        // so that it cannot add results to the list of the new scan, and it stops reporting
        // anything once it has been replaced: the state belongs to the scan that is current.
        _cts?.Cancel();
        var generation = ++_scanGeneration;
        var total = computers.Count;
        results.Clear();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        ScanTotal = total;
        ScanCompleted = 0;
        IsScanning = true;
        SetStatus(mode, Progress(0, total));

        // A computer is finished when its first row arrives: the scanner writes every row of a
        // computer at once, after it has finished with it.
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(ProfileResult result) => _dispatch(() =>
        {
            if (generation != _scanGeneration) return;

            Insert(results, result);
            if (finished.Add(result.Computer))
            {
                ScanCompleted = finished.Count;
                SetStatus(mode, Progress(finished.Count, total));
            }
        });

        try
        {
            if (mode == LookupMode.ByComputer)
            {
                await foreach (var r in _scanner.ScanComputersAsync(computers, ct))
                    Add(r);
            }
            else
            {
                await foreach (var r in _scanner.FindUserAsync(user, computers, ct))
                    Add(r);
            }
            if (generation == _scanGeneration)
                SetStatus(mode, Summarize(mode, results, total, cancelled: ct.IsCancellationRequested));
        }
        catch (OperationCanceledException)
        {
            if (generation == _scanGeneration)
                SetStatus(mode, Summarize(mode, results, total, cancelled: true));
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration)
                SetStatus(mode, "Scan failed: " + ex.Message);
        }
        finally
        {
            if (generation == _scanGeneration)
            {
                IsScanning = false;
                _cts = null;
            }

            // The scan has finished with its token, so its own source can go whether or not it
            // is still the current one.
            cts.Dispose();
        }
    }

    private static string Progress(int completed, int total)
        => $"Scanning… {completed} of {Plural.Count(total, "computer")}";

    /// <summary>
    /// The status line at the end of a scan. By user says how many computers were checked and what
    /// was found on them, because a result list alone cannot say whether a computer was looked at.
    /// The line keeps starting with "Done." or "Cancelled.", which the window relies on.
    /// </summary>
    private static string Summarize(
        LookupMode mode, ObservableCollection<ProfileResult> results, int total, bool cancelled)
    {
        var outcome = cancelled ? "Cancelled" : "Done";
        if (mode == LookupMode.ByComputer) return $"{outcome}. {Plural.Count(results.Count, "result")}.";

        var withProfile = results.Count(r => r.Status == ScanStatus.Ok);
        var without = results.Count(r => r.Status == ScanStatus.NoProfile);
        var notChecked = results.Count - withProfile - without;
        var checkedText = cancelled
            ? $"{results.Count} of {Plural.Count(total, "computer")}"
            : Plural.Count(results.Count, "computer");

        return $"{outcome}. {checkedText} checked: {withProfile} with a profile, "
               + $"{without} without, {notChecked} could not be checked.";
    }

    /// <summary>
    /// Asks Active Directory for computer names. The names and a sentence about the outcome are
    /// handed back; the caller puts them where they belong. A load is not a scan, so it leaves the
    /// status line of the lookup alone, and it works the same from Logon history, which has no
    /// profile lookup to write into.
    /// </summary>
    public async Task<AdLoadResult> LoadFromAdAsync(string prefix, string? ou)
    {
        _adCts?.Cancel();
        var cts = new CancellationTokenSource();
        _adCts = cts;
        IsSearchingAd = true;
        try
        {
            var names = await _searchAd(prefix, ou, cts.Token);
            if (names.Count > 0)
                return new AdLoadResult(names, $"{Plural.Count(names.Count, "computer")} loaded from AD.");

            return new AdLoadResult(
                names,
                string.IsNullOrEmpty(prefix)
                    ? "No computers found in AD."
                    : $"No computer in AD has a name starting with {prefix}.");
        }
        catch (OperationCanceledException)
        {
            return new AdLoadResult(Array.Empty<string>(), "AD search cancelled.");
        }
        catch (Exception ex)
        {
            return new AdLoadResult(Array.Empty<string>(), "AD search failed: " + ex.Message);
        }
        finally
        {
            IsSearchingAd = false;
            if (ReferenceEquals(_adCts, cts))
            {
                _adCts = null;
                cts.Dispose();
            }
        }
    }

    // Keep newest first as results stream in; failures (no date) sort last.
    private static void Insert(ObservableCollection<ProfileResult> list, ProfileResult r)
    {
        var key = r.LastActivity ?? DateTime.MinValue;
        var i = 0;
        while (i < list.Count && (list[i].LastActivity ?? DateTime.MinValue) >= key) i++;
        list.Insert(i, r);
    }

    private void SetStatus(LookupMode mode, string text)
    {
        if (_status[mode] == text) return;

        _status[mode] = text;
        if (mode == _mode) Notify(nameof(StatusText));
    }

    private void Notify(string name)
        => _dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name!);
    }
}
