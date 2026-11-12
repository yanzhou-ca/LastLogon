using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LastLogon.Core;

public enum LookupMode { ByComputer, ByUser }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly ProfileScanner _scanner;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cts;
    private LookupMode _mode;
    private string _computersText = "";
    private string _username = "";
    private bool _isScanning;
    private string _statusText = "Ready";

    public MainViewModel(ProfileScanner scanner, Action<Action>? dispatch = null)
    {
        _scanner = scanner;
        _dispatch = dispatch ?? (a => a());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProfileResult> Results { get; } = new();
    public int StaleDays { get; set; } = 90;

    public LookupMode Mode { get => _mode; set => Set(ref _mode, value); }
    public string ComputersText { get => _computersText; set => Set(ref _computersText, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool IsStale(ProfileResult r) => r.DaysAgo is int d && d > StaleDays;

    public void Cancel() => _cts?.Cancel();

    public async Task ScanAsync()
    {
        IReadOnlyList<string> computers;
        try
        {
            computers = InputParser.ParseComputers(ComputersText);
        }
        catch (FormatException)
        {
            Results.Clear();
            StatusText = "Computer name: Enter a short computer name; paths and domains are not allowed.";
            return;
        }

        var user = InputParser.NormalizeUsername(Username);

        if (computers.Count == 0) { StatusText = "Computer name: Enter at least one computer name."; return; }
        if (Mode == LookupMode.ByUser && user.Length == 0) { StatusText = "Username: Enter a username."; return; }

        Results.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true;
        StatusText = "Scanning…";
        try
        {
            if (Mode == LookupMode.ByComputer)
            {
                await foreach (var r in _scanner.ScanComputersAsync(computers, ct))
                    _dispatch(() => Insert(r));
            }
            else
            {
                await foreach (var r in _scanner.FindUserAsync(user, computers, ct))
                    _dispatch(() => Insert(r));
            }
            StatusText = ct.IsCancellationRequested ? $"Cancelled. {Results.Count} result(s)." : $"Done. {Results.Count} result(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Cancelled. {Results.Count} result(s).";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task LoadFromAdAsync(string prefix, string? ou)
    {
        StatusText = "Searching AD…";
        try
        {
            var names = await AdComputerSearch.SearchAsync(prefix, ou, CancellationToken.None);
            ComputersText = string.Join(Environment.NewLine, names);
            StatusText = $"{names.Count} computer(s) loaded from AD.";
        }
        catch (Exception ex)
        {
            StatusText = "AD search failed: " + ex.Message;
        }
    }

    // Keep newest first as results stream in; failures (no date) sort last.
    private void Insert(ProfileResult r)
    {
        var key = r.LastActivity ?? DateTime.MinValue;
        var i = 0;
        while (i < Results.Count && (Results[i].LastActivity ?? DateTime.MinValue) >= key) i++;
        Results.Insert(i, r);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _dispatch(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
    }
}
