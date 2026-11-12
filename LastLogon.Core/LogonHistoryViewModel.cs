using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LastLogon.Core;

public sealed class LogonHistoryViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> SpecialAccountDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "BUILTIN",
        "FONT DRIVER HOST",
        "IIS APPPOOL",
        "NT AUTHORITY",
        "NT SERVICE",
        "NT VIRTUAL MACHINE",
        "WINDOW MANAGER"
    };

    /// <summary>The longest time range a single scan may cover.</summary>
    public static readonly TimeSpan DefaultMaxRange = TimeSpan.FromDays(90);

    /// <summary>The largest number of events a single scan keeps.</summary>
    public const int DefaultMaxEvents = 50_000;

    private readonly ObservableCollection<LogonEventRecord> _filtered = new();
    private readonly ILogonEventScanner _scanner;
    private readonly Action<Action> _dispatch;
    private readonly TimeSpan _maxRange;
    private readonly int _maxEvents;
    private CancellationTokenSource? _cancellation;
    private string _computersText = "";
    private DateTimeOffset _startLocal;
    private DateTimeOffset _endLocal;
    private LogonEventRecord? _selectedEvent;
    private LogonAccountFilter _accountFilter;
    private LogonTypeFilter _typeFilter;
    private bool _isScanning;
    private string _statusText = "Ready";
    private bool _isTruncated;

    public LogonHistoryViewModel(
        ILogonEventScanner scanner,
        Action<Action>? dispatch = null,
        TimeSpan? maxRange = null,
        int maxEvents = DefaultMaxEvents)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        _scanner = scanner;
        _dispatch = dispatch ?? (action => action());
        _maxRange = maxRange ?? DefaultMaxRange;
        _maxEvents = maxEvents;
        _endLocal = DateTimeOffset.Now;
        _startLocal = _endLocal.AddDays(-3);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LogonEventRecord> Events { get; } = new();
    public ObservableCollection<LogonComputerCompleted> ComputerStatuses { get; } = new();

    /// <summary>
    /// The events that pass the current filters, newest first.
    /// <para>
    /// The collection instance never changes, so the grid keeps a single item source and each
    /// new event is added to it instead of the whole list being rebuilt and re-sorted.
    /// </para>
    /// </summary>
    public IReadOnlyList<LogonEventRecord> FilteredEvents => _filtered;

    /// <summary>True when at least one event passes the current filters.</summary>
    public bool HasVisibleEvents => _filtered.Count > 0;

    /// <summary>True when the scan stopped early because it reached the event limit.</summary>
    public bool IsTruncated => _isTruncated;

    public string ComputersText
    {
        get => _computersText;
        set => Set(ref _computersText, value);
    }

    public DateTimeOffset StartLocal
    {
        get => _startLocal;
        set => Set(ref _startLocal, value);
    }

    public DateTimeOffset EndLocal
    {
        get => _endLocal;
        set => Set(ref _endLocal, value);
    }

    public LogonEventRecord? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (Set(ref _selectedEvent, value))
            {
                OnPropertyChanged(nameof(SelectedDetails));
            }
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> SelectedDetails
        => _selectedEvent?.Data.ToArray() ?? Array.Empty<KeyValuePair<string, string>>();

    public LogonAccountFilter AccountFilter
    {
        get => _accountFilter;
        set
        {
            if (Set(ref _accountFilter, value))
            {
                ApplyFilters();
            }
        }
    }

    public LogonTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (Set(ref _typeFilter, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set => Set(ref _isScanning, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void Cancel() => _cancellation?.Cancel();

    public async Task ScanAsync()
    {
        IReadOnlyList<string> computers;
        try
        {
            computers = InputParser.ParseComputers(ComputersText);
        }
        catch (FormatException)
        {
            ClearResults();
            StatusText = "Computer name: Enter a short computer name; paths and domains are not allowed.";
            return;
        }

        if (computers.Count == 0)
        {
            StatusText = "Computer name: Enter at least one computer name.";
            return;
        }

        if (StartLocal >= EndLocal)
        {
            StatusText = "Start time must be earlier than end time.";
            return;
        }

        if (EndLocal - StartLocal > _maxRange)
        {
            StatusText = $"The time range must not exceed {_maxRange.TotalDays:F0} days.";
            return;
        }

        ClearResults();
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var cancellationToken = cancellation.Token;
        IsScanning = true;
        StatusText = "Scanning…";

        var fromUtc = StartLocal.ToUniversalTime();
        var toUtc = EndLocal.ToUniversalTime();

        try
        {
            var received = 0;
            await foreach (var update in _scanner.ScanAsync(computers, fromUtc, toUtc, cancellationToken))
            {
                switch (update)
                {
                    case LogonEventFound found:
                        if (received >= _maxEvents)
                        {
                            // The remote log holds more events than this tool keeps. Stop reading
                            // and say so, rather than growing without bound.
                            _isTruncated = true;
                            OnPropertyChanged(nameof(IsTruncated));
                            cancellation.Cancel();
                            break;
                        }
                        received++;
                        _dispatch(() => Insert(found.Event));
                        break;
                    case LogonComputerCompleted completed:
                        if (completed.Truncated) _isTruncated = true;
                        _dispatch(() => ComputerStatuses.Add(completed));
                        break;
                }
            }

            var successful = ComputerStatuses.Count(item => item.Status == LogonQueryStatus.Success);
            var failed = ComputerStatuses.Count - successful;
            StatusText = _isTruncated
                ? $"Stopped at the {_maxEvents} event limit. {Events.Count} event(s) collected; results are incomplete."
                : cancellationToken.IsCancellationRequested
                    ? $"Cancelled. {Events.Count} event(s); {failed} incomplete computer(s)."
                    : $"Done. {Events.Count} event(s); {successful} succeeded, {failed} failed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = $"Cancelled. {Events.Count} event(s); {ComputerStatuses.Count} computer(s) completed.";
        }
        catch (Exception ex)
        {
            StatusText = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void ClearResults()
    {
        SelectedEvent = null;
        _isTruncated = false;
        _dispatch(() =>
        {
            Events.Clear();
            _filtered.Clear();
            ComputerStatuses.Clear();
            OnPropertyChanged(nameof(IsTruncated));
            OnPropertyChanged(nameof(HasVisibleEvents));
        });
    }

    /// <summary>Rebuilds the filtered collection after the account or logon type filter changed.</summary>
    private void ApplyFilters()
    {
        _dispatch(() =>
        {
            _filtered.Clear();
            foreach (var record in Events)
            {
                if (IsVisible(record)) _filtered.Add(record);
            }

            OnPropertyChanged(nameof(HasVisibleEvents));
        });
    }

    private void Insert(LogonEventRecord eventRecord)
    {
        Events.Insert(FindInsertionIndex(Events, eventRecord.TimeUtc), eventRecord);

        if (IsVisible(eventRecord))
        {
            _filtered.Insert(FindInsertionIndex(_filtered, eventRecord.TimeUtc), eventRecord);
            if (_filtered.Count == 1) OnPropertyChanged(nameof(HasVisibleEvents));
        }
    }

    /// <summary>
    /// Finds the position of an event in a collection that is ordered newest first. Events with
    /// the same timestamp keep the order in which they arrived.
    /// </summary>
    private static int FindInsertionIndex(IReadOnlyList<LogonEventRecord> items, DateTimeOffset timeUtc)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (items[middle].TimeUtc >= timeUtc) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private bool IsVisible(LogonEventRecord eventRecord)
        => IsAccountVisible(eventRecord) && IsTypeVisible(eventRecord);

    private bool IsAccountVisible(LogonEventRecord eventRecord)
        => AccountFilter switch
        {
            LogonAccountFilter.All => true,
            LogonAccountFilter.Local => !string.IsNullOrWhiteSpace(eventRecord.AccountDomain)
                && eventRecord.AccountDomain.Equals(eventRecord.Computer, StringComparison.OrdinalIgnoreCase),
            LogonAccountFilter.Domain => !string.IsNullOrWhiteSpace(eventRecord.AccountDomain)
                && !eventRecord.AccountDomain.Equals(eventRecord.Computer, StringComparison.OrdinalIgnoreCase)
                && !SpecialAccountDomains.Contains(eventRecord.AccountDomain),
            _ => false
        };

    private bool IsTypeVisible(LogonEventRecord eventRecord)
    {
        if (TypeFilter == LogonTypeFilter.All)
        {
            return true;
        }

        var category = eventRecord.LogonType switch
        {
            2 or 7 or 10 or 11 or 12 or 13 => LogonTypeFilter.Interactive,
            3 or 8 or 9 => LogonTypeFilter.Network,
            4 or 5 => LogonTypeFilter.Service,
            _ => LogonTypeFilter.Other
        };

        return category == TypeFilter;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => _dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
}
