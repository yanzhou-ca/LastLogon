using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LastLogon.Core;
using Microsoft.Win32;

namespace LastLogon;

public partial class ScanWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LogonHistoryViewModel _historyVm;
    private bool _hasCompletedScan;
    private int _connectionTestGeneration;
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";

    public ScanWindow()
    {
        Action<Action> dispatch = action =>
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        };

        _vm = new MainViewModel(
            new ProfileScanner(new SmbProfileFileSystem()),
            dispatch);
        _historyVm = new LogonHistoryViewModel(
            new LogonEventScanner(new WindowsLogonEventSource()),
            dispatch);

        InitializeComponent();

        HistoryResultsHost.DataContext = _historyVm;
        HistoryStartBox.Text = _historyVm.StartLocal.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        HistoryEndBox.Text = _historyVm.EndLocal.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        ResultsList.ItemsSource = _vm.Results;
        StatusLabel.Text = _vm.StatusText;
        _vm.Results.CollectionChanged += (_, _) => UpdateEmptyResultsState();
        UpdateEmptyResultsState();
        UpdateOpenFolderButton();
        UpdateComputerTargetHint();
        Mode_Changed(this, new RoutedEventArgs());

        if (!IsElevated())
        {
            // The manifest asks for administrator rights, so this only happens when the
            // application is started in a way that drops them.
            StatusLabel.Text =
                "This tool reads administrative shares on other computers and needs to run "
                + "elevated. Start it with Run as administrator.";
        }

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.IsScanning):
                case nameof(MainViewModel.IsSearchingAd):
                    UpdateScanControls();
                    UpdateEmptyResultsState();
                    break;
                case nameof(MainViewModel.StatusText):
                    if (LogonHistoryRadio.IsChecked != true)
                    {
                        StatusLabel.Text = _vm.StatusText;
                        _hasCompletedScan = _vm.StatusText.StartsWith("Done.", StringComparison.Ordinal)
                            || _vm.StatusText.StartsWith("Cancelled.", StringComparison.Ordinal);
                        UpdateEmptyResultsState();
                    }
                    break;
                case nameof(MainViewModel.ComputersText):
                    if (ComputersBox.Text != _vm.ComputersText) ComputersBox.Text = _vm.ComputersText;
                    break;
            }
        };

        _historyVm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(LogonHistoryViewModel.IsScanning):
                    UpdateScanControls();
                    UpdateHistoryEmptyState();
                    break;
                case nameof(LogonHistoryViewModel.StatusText):
                    if (LogonHistoryRadio.IsChecked == true) StatusLabel.Text = _historyVm.StatusText;
                    UpdateHistoryEmptyState();
                    break;
                case nameof(LogonHistoryViewModel.HasVisibleEvents):
                    UpdateHistoryEmptyState();
                    break;
            }
        };
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (HistoryPanel is null || ListButtons is null || UsernamePanel is null) return;

        var history = LogonHistoryRadio.IsChecked == true;
        var byUser = !history && ByUserRadio.IsChecked == true;
        if (!history) _vm.Mode = byUser ? LookupMode.ByUser : LookupMode.ByComputer;
        Title = history ? "Logon History" : "Last Profile Activity";

        ResultsHost.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryResultsHost.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        UsernamePanel.Visibility = ListButtons.Visibility = byUser ? Visibility.Visible : Visibility.Collapsed;
        StaleSettingsPanel.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        OpenFolderButton.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        ComputersLabel.Text = history || byUser
            ? "Computers (short or full names, one per line, comma or space separated)"
            : "Computer name (short or full name)";
        if (UsernameErrorLabel is not null) UsernameErrorLabel.Visibility = Visibility.Collapsed;
        if (ComputersErrorLabel is not null) ComputersErrorLabel.Visibility = Visibility.Collapsed;
        if (history)
        {
            ApplyHistoryFilters();
            StatusLabel.Text = _historyVm.StatusText;
            UpdateHistoryEmptyState();
        }
        else
        {
            StatusLabel.Text = _vm.StatusText;
            UpdateEmptyResultsState();
        }

        UpdateScanControls();
        UpdateComputerTargetHint();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await StartScanAsync();

    /// <summary>
    /// Pressing Enter in one of the input fields starts the scan. The event is tunnelled, so the
    /// text box never gets to insert a line break for it.
    /// </summary>
    private async void LookupInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await StartScanAsync();
    }

    private async Task StartScanAsync()
    {
        if (LogonHistoryRadio.IsChecked == true)
        {
            _historyVm.ComputersText = ComputersBox.Text;
            if (!TryParseLocalDateTime(HistoryStartBox.Text, out var startLocal)
                || !TryParseLocalDateTime(HistoryEndBox.Text, out var endLocal))
            {
                HistoryTimeErrorLabel.Text = $"Enter local times as {LocalDateTimeFormat}.";
                HistoryTimeErrorLabel.Visibility = Visibility.Visible;
                StatusLabel.Text = HistoryTimeErrorLabel.Text;
                return;
            }

            HistoryTimeErrorLabel.Visibility = Visibility.Collapsed;
            _historyVm.StartLocal = startLocal;
            _historyVm.EndLocal = endLocal;
            ApplyHistoryFilters();
            await _historyVm.ScanAsync();
            StatusLabel.Text = _historyVm.StatusText;
            UpdateHistoryEmptyState();
            return;
        }

        _vm.ComputersText = ComputersBox.Text;
        _vm.Username = UsernameBox.Text;
        await _vm.ScanAsync();
        StatusLabel.Text = _vm.StatusText;
        UpdateValidationFeedback();
        UpdateEmptyResultsState();
    }

    private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e)
        => ApplyHistoryFilters();

    private void ApplyHistoryFilters()
    {
        if (AccountFilterCombo is null || TypeFilterCombo is null) return;
        if (AccountFilterCombo.SelectedIndex is >= 0 and <= 2)
            _historyVm.AccountFilter = (LogonAccountFilter)AccountFilterCombo.SelectedIndex;
        if (TypeFilterCombo.SelectedIndex is >= 0 and <= 4)
            _historyVm.TypeFilter = (LogonTypeFilter)TypeFilterCombo.SelectedIndex;
    }

    private void ValidationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender == ComputersBox)
        {
            ComputersErrorLabel.Visibility = Visibility.Collapsed;
            ClearConnectionTest();
            UpdateComputerTargetHint();
        }

        if (sender == UsernameBox)
        {
            UsernameErrorLabel.Visibility = Visibility.Collapsed;
            UpdateComputerTargetHint();
        }
        if (sender == HistoryStartBox || sender == HistoryEndBox)
            HistoryTimeErrorLabel.Visibility = Visibility.Collapsed;
        if (_vm.StatusText.StartsWith("Computer name: ", StringComparison.Ordinal)
            || _vm.StatusText.StartsWith("Username: ", StringComparison.Ordinal))
            StatusLabel.Text = "Ready";
    }

    /// <summary>
    /// Checks whether the computer can be reached and reports which name reaches it. It is the
    /// difference between a name that is wrong and a share that cannot be read.
    /// </summary>
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> computers;
        try
        {
            computers = InputParser.ParseComputers(ComputersBox.Text);
        }
        catch (FormatException)
        {
            ConnectionTestLabel.Text = "Enter a computer name first.";
            return;
        }

        if (computers.Count != 1)
        {
            ConnectionTestLabel.Text = "Enter a single computer name to test it.";
            return;
        }

        var computer = computers[0];
        var generation = ++_connectionTestGeneration;
        ConnectionTestLabel.Text = "Testing " + computer + "…";
        TestButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => ConnectionTest.Run(computer, ComputerTarget.AddressingMode));
            if (generation == _connectionTestGeneration)
                ConnectionTestLabel.Text = Describe(result);
        }
        catch (Exception ex)
        {
            if (generation == _connectionTestGeneration)
                ConnectionTestLabel.Text = "Test failed: " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Drops a test result that no longer describes the input, so that a result for the
    /// previous computer is not mistaken for the current one.
    /// </summary>
    private void ClearConnectionTest()
    {
        _connectionTestGeneration++;
        if (ConnectionTestLabel is not null) ConnectionTestLabel.Text = string.Empty;
    }

    private static string Describe(ConnectionTestResult result)
    {
        var text = result.Summary;

        if (!string.IsNullOrEmpty(result.ResolvedAddress))
            text += $" Resolves to {result.ResolvedAddress}.";

        if (result.Attempts.Count > 1)
        {
            var details = result.Attempts.Select(attempt =>
            {
                var code = attempt.Win32Error is int error ? $" (Windows error {error})" : string.Empty;
                return attempt.Succeeded
                    ? $"[{attempt.AddressedAs}: reachable]"
                    : $"[{attempt.AddressedAs}: failed{code}]";
            });
            text += " " + string.Join(" ", details);
        }

        return text;
    }

    private void AddressingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressingCombo is null || AddressingCombo.SelectedIndex is not (0 or 1)) return;

        ComputerTarget.AddressingMode = AddressingCombo.SelectedIndex == 1
            ? ComputerTarget.Addressing.ShortName
            : ComputerTarget.Addressing.DomainQualified;

        ClearConnectionTest();
        UpdateComputerTargetHint();
    }

    /// <summary>
    /// Shows which host the scan will actually talk to, so that a name that resolves to the
    /// wrong machine is visible before the scan runs, together with the account and rights the
    /// scan will use.
    /// </summary>
    private void UpdateComputerTargetHint()
    {
        if (ComputerTargetLabel is null) return;

        IReadOnlyList<string> computers;
        try
        {
            computers = InputParser.ParseComputers(ComputersBox.Text);
        }
        catch (FormatException)
        {
            ComputerTargetLabel.Text = string.Empty;
            return;
        }

        var allShort = computers.All(InputParser.IsShortComputerName);
        var domain = ComputerTarget.DnsDomain;
        var qualified = allShort
                        && domain.Length > 0
                        && ComputerTarget.AddressingMode == ComputerTarget.Addressing.DomainQualified;

        // In By user mode the scan reads one known file instead of listing C:\Users, so the
        // exact file is shown. That is the path to compare with a manual PowerShell check.
        var byUser = LogonHistoryRadio.IsChecked != true && ByUserRadio.IsChecked == true;
        var user = InputParser.NormalizeUsername(UsernameBox.Text);
        var knownProfile = byUser && user.Length > 0 && InputParser.IsSafeProfileFolderName(user);

        ComputerTargetLabel.Text = computers.Count switch
        {
            1 when knownProfile =>
                $"Reads {ComputerTarget.UncPath(computers[0], "Users", user, "NTUSER.DAT")} directly, without listing C:\\Users",
            1 => $"Reads {ComputerTarget.UncPath(computers[0], "Users")}",
            0 => string.Empty,
            _ when knownProfile => $"Reads {user}\\NTUSER.DAT on {computers.Count} computers",
            _ when qualified => $"Reads {computers.Count} computers in {domain}",
            _ when allShort => $"Reads {computers.Count} computers by short name",
            _ => $"Reads {computers.Count} computers as entered",
        };

        if (RunContextLabel is null) return;
        RunContextLabel.Text = RunContext.IsElevated
            ? $"Running as {RunContext.Account}, elevated."
            : $"Running as {RunContext.Account} without administrator rights - the administrative "
              + "share (C$) of another computer will be refused.";
    }

    /// <summary>True when the process holds the full administrator token.</summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void UpdateValidationFeedback()
    {
        UsernameErrorLabel.Visibility = ComputersErrorLabel.Visibility = Visibility.Collapsed;

        const string computerPrefix = "Computer name: ";
        const string usernamePrefix = "Username: ";
        if (_vm.StatusText.StartsWith(computerPrefix, StringComparison.Ordinal))
        {
            ComputersErrorLabel.Text = _vm.StatusText[computerPrefix.Length..];
            ComputersErrorLabel.Visibility = Visibility.Visible;
            ComputersBox.Focus();
            Keyboard.Focus(ComputersBox);
        }
        else if (_vm.StatusText.StartsWith(usernamePrefix, StringComparison.Ordinal))
        {
            UsernameErrorLabel.Text = _vm.StatusText[usernamePrefix.Length..];
            UsernameErrorLabel.Visibility = Visibility.Visible;
            UsernameBox.Focus();
            Keyboard.Focus(UsernameBox);
        }
    }

    private void UpdateEmptyResultsState()
    {
        if (ResultsList is null || EmptyResultsPanel is null) return;
        EmptyResultsPanel.Visibility = _vm.Results.Count == 0 && !_vm.IsScanning
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyResultsLabel.Text = _vm.StatusText.StartsWith("Cancelled.", StringComparison.Ordinal)
            ? "Scan cancelled before any results were found."
            : _hasCompletedScan
                ? "No profiles found for this scan."
                : "Run a scan to see profile activity.";
    }

    private void UpdateHistoryEmptyState()
    {
        if (HistoryEmptyPanel is null || _historyVm is null) return;
        var noVisibleEvents = !_historyVm.HasVisibleEvents;
        HistoryEmptyPanel.Visibility = noVisibleEvents ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyLabel.Text = _historyVm.IsScanning
            ? "Querying Security logs…"
            : _historyVm.Events.Count > 0
                ? "No events match the current filters."
                : _historyVm.ComputerStatuses.Count > 0
                    && _historyVm.ComputerStatuses.All(item => item.Status == LogonQueryStatus.Success)
                        ? "No logon events were found in this time range."
                        : _historyVm.ComputerStatuses.Count > 0
                            ? "No events returned. Review the computer status below."
                            : "Run a history scan to see Security-log events.";
    }

    private void UpdateScanControls()
    {
        if (ScanButton is null || CancelButton is null || Progress is null) return;
        var isScanning = _vm.IsScanning || _vm.IsSearchingAd || _historyVm.IsScanning;
        ScanButton.IsEnabled = !isScanning;
        CancelButton.IsEnabled = isScanning;
        Progress.Visibility = isScanning ? Visibility.Visible : Visibility.Collapsed;
        ByComputerRadio.IsEnabled = ByUserRadio.IsEnabled = LogonHistoryRadio.IsEnabled = !isScanning;
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateOpenFolderButton();

    private void UpdateOpenFolderButton()
        => OpenFolderButton.IsEnabled = ResultsList.SelectedItem is ProfileResult r
            && !string.IsNullOrEmpty(r.Username);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (LogonHistoryRadio.IsChecked == true) _historyVm.Cancel();
        else _vm.Cancel();
    }

    private void StaleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null || !int.TryParse(StaleBox.Text, out var days) || days < 1) return;
        _vm.StaleDays = days;
        foreach (var item in _vm.Results)
        {
            if (ResultsList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) Highlight(c, item);
        }
    }

    private void Item_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem c && c.DataContext is ProfileResult r) Highlight(c, r);
    }

    private void Highlight(ListViewItem container, ProfileResult r)
    {
        container.Background = _vm.IsStale(r)
            ? new SolidColorBrush(Color.FromArgb(60, 255, 160, 0))
            : Brushes.Transparent;
    }

    private List<ProfileResult> SelectedOrAll()
    {
        var sel = ResultsList.SelectedItems.OfType<ProfileResult>().ToList();
        return sel.Count > 0 ? sel : _vm.Results.ToList();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LogonHistoryRadio.IsChecked == true)
            {
                var selectedEvents = HistoryEventsGrid.SelectedItems.OfType<LogonEventRecord>().ToList();
                var rows = selectedEvents.Count > 0 ? selectedEvents : _historyVm.FilteredEvents.ToList();
                var eventText = ResultExporter.ToLogonEventTabSeparated(rows);
                if (eventText.Length > 0) Clipboard.SetText(eventText);
                return;
            }

            var text = ResultExporter.ToTabSeparated(SelectedOrAll());
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not copy to the clipboard: " + ex.Message;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LogonHistoryRadio.IsChecked == true)
            {
                var historyDialog = new SaveFileDialog
                {
                    FileName = "LogonHistory",
                    DefaultExt = ".csv",
                    Filter = "CSV (*.csv)|*.csv"
                };
                if (historyDialog.ShowDialog(this) != true) return;
                File.WriteAllText(
                    historyDialog.FileName,
                    ResultExporter.ToLogonEventCsv(_historyVm.FilteredEvents),
                    new System.Text.UTF8Encoding(true));
                return;
            }

            var dlg = new SaveFileDialog { FileName = "LastProfileActivity", DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllText(dlg.FileName, ResultExporter.ToCsv(_vm.Results), new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not write the export file: " + ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text or CSV (*.txt;*.csv)|*.txt;*.csv|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ComputersBox.Text = File.ReadAllText(dlg.FileName);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not read the file: " + ex.Message;
        }
    }

    private async void FromAd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AdDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await _vm.LoadFromAdAsync(dlg.Prefix, dlg.Ou);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ProfileResult r || string.IsNullOrEmpty(r.Username)) return;

        // The folder name comes from the scanned computer, and the computer name from the
        // operator. Neither may be able to escape the path or the command line.
        if (!InputParser.IsValidComputerName(r.Computer) || !InputParser.IsSafeProfileFolderName(r.Username))
        {
            StatusLabel.Text = "The selected profile folder cannot be opened because its name is not valid.";
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(ComputerTarget.UncPath(r.Computer, "Users", r.Username));
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not open the profile folder: " + ex.Message;
        }
    }

    private static bool TryParseLocalDateTime(string text, out DateTimeOffset value)
    {
        if (!DateTime.TryParseExact(
                text,
                LocalDateTimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            value = default;
            return false;
        }

        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localDateTime))
        {
            value = default;
            return false;
        }

        value = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
        return true;
    }
}

public sealed class LocalTimeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTimeOffset time
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
