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

    private readonly ILogonEventScanner _scanner;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cancellation;
    private string _computersText = "";
    private DateTimeOffset _startLocal;
    private DateTimeOffset _endLocal;
    private LogonEventRecord? _selectedEvent;
    private LogonAccountFilter _accountFilter;
    private LogonTypeFilter _typeFilter;
    private bool _isScanning;
    private string _statusText = "Ready";

    public LogonHistoryViewModel(ILogonEventScanner scanner, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
        _dispatch = dispatch ?? (action => action());
        _endLocal = DateTimeOffset.Now;
        _startLocal = _endLocal.AddDays(-3);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<LogonEventRecord> Events { get; } = new();
    public ObservableCollection<LogonComputerCompleted> ComputerStatuses { get; } = new();

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

    public IReadOnlyList<LogonEventRecord> FilteredEvents
        => Events.Where(IsVisible).ToArray();

    public LogonAccountFilter AccountFilter
    {
        get => _accountFilter;
        set
        {
            if (Set(ref _accountFilter, value))
            {
                OnPropertyChanged(nameof(FilteredEvents));
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
                OnPropertyChanged(nameof(FilteredEvents));
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
            await foreach (var update in _scanner.ScanAsync(computers, fromUtc, toUtc, cancellationToken))
            {
                switch (update)
                {
                    case LogonEventFound found:
                        _dispatch(() =>
                        {
                            Insert(found.Event);
                            OnPropertyChanged(nameof(FilteredEvents));
                        });
                        break;
                    case LogonComputerCompleted completed:
                        _dispatch(() => ComputerStatuses.Add(completed));
                        break;
                }
            }

            var successful = ComputerStatuses.Count(item => item.Status == LogonQueryStatus.Success);
            var failed = ComputerStatuses.Count - successful;
            StatusText = cancellationToken.IsCancellationRequested
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
        _dispatch(() =>
        {
            Events.Clear();
            ComputerStatuses.Clear();
            OnPropertyChanged(nameof(FilteredEvents));
        });
    }

    private void Insert(LogonEventRecord eventRecord)
    {
        var index = 0;
        while (index < Events.Count && Events[index].TimeUtc >= eventRecord.TimeUtc)
        {
            index++;
        }

        Events.Insert(index, eventRecord);
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
