using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LastLogon.Core;
using Microsoft.Win32;

namespace LastLogon;

public partial class ScanWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LogonHistoryViewModel _historyVm;
    private INotifyCollectionChanged? _observedResults;
    private const string LocalDateTimeFormat = "yyyy-MM-dd HH:mm";

    private const string StaleBoxHint =
        "Rows whose user was last on the computer more than this many days ago are marked stale. "
        + "A user who is logged in is never marked stale.";

    // The least room the lists need before they stop being useful: the header and five rows of a
    // profile table, or the events grid with the two panes under it in Logon history. The window
    // is never allowed to be smaller than what these and the rest of the form add up to.
    private const double ProfileResultsMinimum = 160;
    private const double HistoryResultsMinimum = 224;

    // A list of computer names is a few kilobytes. A file far larger than that is the wrong file,
    // and loading it would freeze the window.
    private const long MaxListFileBytes = 1_000_000;
    private const double LeastWindowHeight = 480;

    public ScanWindow()
    {
        Action<Action> dispatch = action =>
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action);
        };

        // A computer that answers "access denied" is looked into with a connection test, using
        // the addressing the operator selected, so that the row says which address each name
        // reaches. The login times come from the same event-log connection that Logon history uses,
        // and By user finds the account of the typed name through the domain of this computer.
        _vm = new MainViewModel(
            new ProfileScanner(
                new SmbProfileFileSystem(),
                connectionProbe: computer => ConnectionTest.Run(computer, ComputerTarget.AddressingMode),
                loginHistory: new LoginHistoryReader(new WindowsLoginLogSource()),
                resolveUserSid: WindowsUserSidResolver.ResolveAsync),
            dispatch);
        _historyVm = new LogonHistoryViewModel(
            new LogonEventScanner(new WindowsLogonEventSource()),
            dispatch);

        InitializeComponent();

        LimitToWorkArea();
        HistoryResultsHost.DataContext = _historyVm;
        HistoryStartBox.Text = _historyVm.StartLocal.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        HistoryEndBox.Text = _historyVm.EndLocal.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
        BindResults();
        StatusLabel.Text = _vm.StatusText;
        UpdateEmptyResultsState();
        UpdateOpenFolderButton();
        UpdateComputerTargetHint();

        // Ctrl+C in the table copies what the Copy button copies, not the grid's own text.
        ResultsList.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            (_, _) => CopyRows(),
            (_, e) => e.CanExecute = HasRows()));
        DataObject.AddPastingHandler(StaleBox, StaleBox_Pasting);

        Mode_Changed(this, new RoutedEventArgs());

        if (!RunContext.IsElevated)
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
                case nameof(MainViewModel.Results):
                    BindResults();
                    break;
                case nameof(MainViewModel.StatusText):
                    if (LogonHistoryRadio.IsChecked != true)
                    {
                        StatusLabel.Text = _vm.StatusText;
                        UpdateEmptyResultsState();
                    }
                    break;
                case nameof(MainViewModel.ScanCompleted):
                case nameof(MainViewModel.ScanTotal):
                    UpdateProgress();
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
                    UpdateResultActions();
                    break;
                case nameof(LogonHistoryViewModel.ScanCompleted):
                case nameof(LogonHistoryViewModel.ScanTotal):
                    UpdateProgress();
                    break;
            }
        };

        Loaded += (_, _) => FocusFirstInput();
        LayoutUpdated += (_, _) => UpdateMinimumHeight();
    }

    /// <summary>
    /// A window taller or wider than the screen would hide the status line or the buttons. On a screen narrower
    /// than the table the table scrolls sideways, and the window does not run off the screen.
    /// </summary>
    private void LimitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (Height > area.Height) Height = area.Height;
        if (Width > area.Width) Width = area.Width;
        if (MinWidth > area.Width) MinWidth = area.Width;
    }

    /// <summary>
    /// Puts the cursor where the operator types first, so that a name can be typed the moment the
    /// window appears. The window itself would otherwise hold the focus.
    /// </summary>
    private void FocusFirstInput()
    {
        var first = ByUserRadio.IsChecked == true ? UsernameBox : ComputersBox;
        FocusManager.SetFocusedElement(this, first);
        first.Focus();
    }

    /// <summary>
    /// Keeps the window from being made smaller than its content needs. Everything above and below
    /// the lists has a size of its own, and the lists get the rest; below this height the lower
    /// part of the window would be cut off with no way to reach it.
    /// </summary>
    private void UpdateMinimumHeight()
    {
        if (!IsLoaded || RootGrid.ActualHeight <= 0) return;

        var history = LogonHistoryRadio.IsChecked == true;
        var results = history ? HistoryResultsMinimum : ProfileResultsMinimum;
        if (!history && ResultDetailsPane.Visibility == Visibility.Visible)
            results += ResultDetailsPane.ActualHeight + ResultDetailsPane.Margin.Top;

        var rows = RootGrid.RowDefinitions;
        if (Math.Abs(rows[3].MinHeight - results) > 0.5) rows[3].MinHeight = results;

        var fixedRows = 0.0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (i != 3) fixedRows += rows[i].ActualHeight;
        }

        var margins = RootGrid.Margin.Top + RootGrid.Margin.Bottom;
        var chrome = ActualHeight - RootGrid.ActualHeight - margins;
        var wanted = Math.Max(LeastWindowHeight, Math.Ceiling(chrome + margins + fixedRows + results));

        // A screen that is shorter than that cannot hold it however the window is sized.
        var target = Math.Min(wanted, SystemParameters.WorkArea.Height);
        if (Math.Abs(MinHeight - target) > 0.5) MinHeight = target;
    }

    private void BindResults()
    {
        if (_observedResults is not null) _observedResults.CollectionChanged -= Results_CollectionChanged;

        ResultsList.ItemsSource = _vm.Results;
        _observedResults = _vm.Results;
        _observedResults.CollectionChanged += Results_CollectionChanged;

        // Every lookup keeps its own rows, so a sort chosen in one is not carried into the other.
        Sorting.Reset(ResultsList);
        UpdateResultActions();
        UpdateDetailsPane();
    }

    private void Results_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyResultsState();
        UpdateResultActions();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (HistoryPanel is null || ListButtons is null || UsernamePanel is null) return;

        var history = LogonHistoryRadio.IsChecked == true;
        var byUser = !history && ByUserRadio.IsChecked == true;
        if (!history) _vm.Mode = byUser ? LookupMode.ByUser : LookupMode.ByComputer;

        ResultsHost.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryResultsHost.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        UsernamePanel.Visibility = byUser ? Visibility.Visible : Visibility.Collapsed;

        // A list of computers is typed in By user and in Logon history. By computer takes one name.
        ListButtons.Visibility = byUser || history ? Visibility.Visible : Visibility.Collapsed;
        ProfileNote.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        StaleSettingsPanel.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        OpenFolderButton.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        ComputersLabel.Text = history || byUser
            ? "Computers (short or full names, one per line, comma or space separated)"
            : "Computer name (short or full name)";

        _vm.ClearValidation();
        _historyVm.ClearValidation();
        HideValidation();
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
        UpdateResultActions();
        UpdateDetailsPane();
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
        // Enter in an input field gets here while the Scan button is disabled, so a scan or a
        // directory search that is already running has to be refused here as well.
        if (_vm.IsScanning || _vm.IsSearchingAd || _historyVm.IsScanning) return;

        HideValidation();

        if (LogonHistoryRadio.IsChecked == true)
        {
            _historyVm.ComputersText = ComputersBox.Text;
            if (!TryParseLocalDateTime(HistoryStartBox.Text, out var startLocal)
                || !TryParseLocalDateTime(HistoryEndBox.Text, out var endLocal))
            {
                var example = DateTime.Now.ToString(LocalDateTimeFormat, CultureInfo.InvariantCulture);
                ShowValidation(new ValidationIssue(
                    ValidationField.TimeRange,
                    $"Enter local times like {example}: year-month-day, then hours and minutes on a 24-hour clock."));
                return;
            }

            _historyVm.StartLocal = startLocal;
            _historyVm.EndLocal = endLocal;
            ApplyHistoryFilters();
            await _historyVm.ScanAsync();
            StatusLabel.Text = _historyVm.StatusText;
            ShowValidation(_historyVm.Validation);
            UpdateHistoryEmptyState();
            return;
        }

        _vm.ComputersText = ComputersBox.Text;
        _vm.Username = UsernameBox.Text;
        await _vm.ScanAsync();
        StatusLabel.Text = _vm.StatusText;
        ShowValidation(_vm.Validation);
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
        if (UsernameErrorLabel is null || ComputersErrorLabel is null || HistoryTimeErrorLabel is null) return;

        // Editing a field takes its message away: it described the text that was there before.
        if (sender == ComputersBox)
        {
            HideValidation(ValidationField.Computers);
            UpdateComputerTargetHint();
        }

        if (sender == UsernameBox)
        {
            HideValidation(ValidationField.Username);
            UpdateComputerTargetHint();
        }

        if (sender == HistoryStartBox || sender == HistoryEndBox)
            HideValidation(ValidationField.TimeRange);

        _vm?.ClearValidation();
        _historyVm?.ClearValidation();
    }

    /// <summary>
    /// Shows an input problem beside the field it is about and moves the cursor there. The line
    /// under a field is always reserved, so showing or hiding the message moves nothing.
    /// </summary>
    private void ShowValidation(ValidationIssue? issue)
    {
        HideValidation();
        if (issue is null) return;

        switch (issue.Field)
        {
            case ValidationField.Username:
                UsernameErrorLabel.Text = issue.Message;
                UsernameErrorLabel.Visibility = Visibility.Visible;
                FocusInput(UsernameBox);
                break;
            case ValidationField.Computers:
                ComputersErrorLabel.Text = issue.Message;
                ComputersErrorLabel.Visibility = Visibility.Visible;
                ComputerTargetLabel.Visibility = Visibility.Hidden;
                FocusInput(ComputersBox);
                break;
            case ValidationField.TimeRange:
                HistoryTimeErrorLabel.Text = issue.Message;
                HistoryTimeErrorLabel.Visibility = Visibility.Visible;
                HistoryCaption.Visibility = Visibility.Hidden;
                FocusInput(HistoryStartBox);
                break;
        }
    }

    private void HideValidation(ValidationField? field = null)
    {
        if (UsernameErrorLabel is null || ComputersErrorLabel is null || HistoryTimeErrorLabel is null) return;

        if (field is null or ValidationField.Username) UsernameErrorLabel.Visibility = Visibility.Hidden;

        if (field is null or ValidationField.Computers)
        {
            ComputersErrorLabel.Visibility = Visibility.Hidden;
            ComputerTargetLabel.Visibility = Visibility.Visible;
        }

        if (field is null or ValidationField.TimeRange)
        {
            HistoryTimeErrorLabel.Visibility = Visibility.Hidden;
            HistoryCaption.Visibility = Visibility.Visible;
        }
    }

    private static void FocusInput(TextBox box)
    {
        box.Focus();
        Keyboard.Focus(box);
    }

    private void AddressingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AddressingCombo is null || AddressingCombo.SelectedIndex is not (0 or 1)) return;

        ComputerTarget.AddressingMode = AddressingCombo.SelectedIndex == 1
            ? ComputerTarget.Addressing.ShortName
            : ComputerTarget.Addressing.DomainQualified;

        UpdateComputerTargetHint();
    }

    /// <summary>
    /// Shows which host the scan will actually talk to, so that a name that resolves to the
    /// wrong machine is visible before the scan runs, together with the account and rights the
    /// scan will use. With nothing typed yet, the line says what to type instead.
    /// </summary>
    private void UpdateComputerTargetHint()
    {
        if (ComputerTargetLabel is null) return;

        var history = LogonHistoryRadio.IsChecked == true;
        var byUser = !history && ByUserRadio.IsChecked == true;

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
        var user = InputParser.NormalizeUsername(UsernameBox.Text);
        var knownProfile = byUser && user.Length > 0 && InputParser.IsSafeProfileFolderName(user);

        // Naming the mode here makes it visible when the path still contains a domain that the
        // operator did not want. A name that was entered fully qualified is used as it is.
        var singleMode = string.Empty;
        if (computers.Count == 1)
        {
            if (!InputParser.IsShortComputerName(computers[0]))
                singleMode = "entered as a fully qualified name";
            else if (ComputerTarget.AddressingMode == ComputerTarget.Addressing.ShortName)
                singleMode = "short name";
            else if (ComputerTarget.IsValidDnsDomain(domain))
                singleMode = "domain-qualified name";
            else
                singleMode = "domain-qualified name, but no DNS domain was found";
        }

        ComputerTargetLabel.Text = computers.Count switch
        {
            1 when knownProfile =>
                $"Reads {ComputerTarget.UncPath(computers[0], "Users", user, "NTUSER.DAT")} directly, without listing C:\\Users ({singleMode})",
            1 => $"Reads {ComputerTarget.UncPath(computers[0], "Users")} ({singleMode})",
            0 => history || byUser
                ? "Enter computer names separated by spaces, commas or new lines."
                : "Enter a computer name, then press Enter or Scan.",
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

    private void UpdateEmptyResultsState()
    {
        if (ResultsList is null || EmptyResultsPanel is null) return;

        EmptyResultsPanel.Visibility = _vm.Results.Count == 0 && !_vm.IsScanning
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Before a scan the panel says what the table will hold, since a blank table does not.
        var status = _vm.StatusText;
        EmptyResultsLabel.Text = status.StartsWith("Cancelled.", StringComparison.Ordinal)
            ? "Scan cancelled before any results were found."
            : status.StartsWith("Done.", StringComparison.Ordinal)
                ? "No profiles found for this scan."
                : _vm.Mode == LookupMode.ByUser
                    ? "Press Scan to see which of the computers have a profile for this user, and whether the user is logged in."
                    : "Press Scan to list the user profiles on the computer, with who is logged in now.";
    }

    private void UpdateHistoryEmptyState()
    {
        if (HistoryEmptyPanel is null || _historyVm is null) return;
        var noVisibleEvents = !_historyVm.HasVisibleEvents;
        HistoryEmptyPanel.Visibility = noVisibleEvents ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyLabel.Text = _historyVm.IsScanning
            ? "Scanning the Security logs…"
            : _historyVm.Events.Count > 0
                ? "No events match the current filters."
                : _historyVm.ComputerStatuses.Count > 0
                    && _historyVm.ComputerStatuses.All(item => item.Status == LogonQueryStatus.Success)
                        ? "No logon events were found in this time range."
                        : _historyVm.ComputerStatuses.Count > 0
                            ? "No events returned. Review the computer status below."
                            : "Press Scan to read the logon events in the Security logs.";
    }

    private void UpdateScanControls()
    {
        if (ScanButton is null || CancelButton is null || Progress is null) return;
        var isScanning = _vm.IsScanning || _vm.IsSearchingAd || _historyVm.IsScanning;
        ScanButton.IsEnabled = !isScanning;
        CancelButton.IsEnabled = isScanning;
        Progress.Visibility = isScanning ? Visibility.Visible : Visibility.Hidden;
        ByComputerRadio.IsEnabled = ByUserRadio.IsEnabled = LogonHistoryRadio.IsEnabled = !isScanning;
        UpdateProgress();
    }

    /// <summary>How many of the asked computers have answered, or a moving bar while a directory is searched.</summary>
    private void UpdateProgress()
    {
        if (Progress is null) return;

        // A directory search runs from By user and from Logon history alike.
        if (_vm.IsSearchingAd)
        {
            Progress.IsIndeterminate = true;
            return;
        }

        var history = LogonHistoryRadio.IsChecked == true;
        var total = history ? _historyVm.ScanTotal : _vm.ScanTotal;
        var completed = history ? _historyVm.ScanCompleted : _vm.ScanCompleted;
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, total);
        Progress.Value = Math.Min(completed, Progress.Maximum);
    }

    /// <summary>Copy and Export are available only when there is something to copy or export.</summary>
    private void UpdateResultActions()
    {
        if (CopyButton is null || ExportButton is null) return;
        var any = HasRows();
        CopyButton.IsEnabled = any;
        ExportButton.IsEnabled = any;
    }

    private bool HasRows()
        => LogonHistoryRadio.IsChecked == true ? _historyVm.FilteredEvents.Count > 0 : _vm.Results.Count > 0;

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOpenFolderButton();
        UpdateDetailsPane();
    }

    private void UpdateOpenFolderButton()
        => OpenFolderButton.IsEnabled = ResultsList.SelectedItem is ProfileResult { CanOpenFolder: true };

    /// <summary>
    /// A row's message can be hundreds of characters, far more than a cell shows. Selecting one row
    /// shows all of it, wrapped, where it can be read, selected and copied.
    /// </summary>
    private void UpdateDetailsPane()
    {
        if (ResultDetailsPane is null) return;

        if (LogonHistoryRadio.IsChecked != true
            && ResultsList.SelectedItems.Count == 1
            && ResultsList.SelectedItem is ProfileResult { DetailText.Length: > 0 } row)
        {
            var who = row.Username.Length > 0 ? $" ({row.Username})" : "";
            ResultDetailsBox.Text = $"{row.Computer}{who}: {row.ResultText}{Environment.NewLine}{row.DetailText}";
            ResultDetailsPane.Visibility = Visibility.Visible;
        }
        else
        {
            ResultDetailsPane.Visibility = Visibility.Collapsed;
            ResultDetailsBox.Clear();
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A double click on a header or on the scroll bar is not a double click on a row.
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(ResultsList, source) is not ListViewItem)
            return;

        OpenFolder_Click(sender, e);
    }

    private void ResultsMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuCopyRows.IsEnabled = HasRows();
        MenuCopyComputer.IsEnabled = ResultsList.SelectedItem is ProfileResult;
        MenuOpenFolder.IsEnabled = ResultsList.SelectedItem is ProfileResult { CanOpenFolder: true };
    }

    private void MenuCopyRows_Click(object sender, RoutedEventArgs e) => CopyRows();

    private void MenuCopyComputer_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is ProfileResult row)
            CopyText(row.Computer, $"Copied the computer name {row.Computer} to the clipboard.");
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // A directory search can be running under any lookup, and while it runs nothing else is.
        if (LogonHistoryRadio.IsChecked == true && !_vm.IsSearchingAd) _historyVm.Cancel();
        else _vm.Cancel();
    }

    // The "stale after" box only ever holds a number the table is using. Typing is limited to
    // digits, a value that is not a usable number is marked and not applied, and leaving the box
    // puts the number in use back.
    private void StaleBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(character => character is < '0' or > '9');

    private void StaleBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text
            || text.Any(character => character is < '0' or > '9'))
            e.CancelCommand();
    }

    private void StaleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;

        if (InputParser.TryParseStaleDays(StaleBox.Text, out var days))
        {
            MarkStaleBox(valid: true);
            _vm.StaleDays = days;
            foreach (var item in _vm.Results)
            {
                if (ResultsList.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem container)
                    Highlight(container, item);
            }
        }
        else
        {
            MarkStaleBox(valid: false);
        }
    }

    private void StaleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (InputParser.TryParseStaleDays(StaleBox.Text, out _)) return;

        StaleBox.Text = _vm.StaleDays.ToString(CultureInfo.InvariantCulture);
        MarkStaleBox(valid: true);
    }

    private void MarkStaleBox(bool valid)
    {
        if (valid)
        {
            StaleBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
            StaleBox.ToolTip = StaleBoxHint;
            System.Windows.Automation.AutomationProperties.SetHelpText(StaleBox, StaleBoxHint);
            return;
        }

        var message = $"Enter a whole number of days from 1 to {InputParser.MaxStaleDays}. "
                      + $"The table still uses {_vm.StaleDays}.";
        StaleBox.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "ErrorBrush");
        StaleBox.ToolTip = message;
        System.Windows.Automation.AutomationProperties.SetHelpText(StaleBox, message);
    }

    private void Item_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListViewItem container && container.DataContext is ProfileResult row) Highlight(container, row);
    }

    private void Highlight(ListViewItem container, ProfileResult row)
        => Stale.SetIsStale(container, _vm.IsStale(row));

    // The rows as the operator sees them, in the order the table is sorted in.
    private IReadOnlyList<ProfileResult> ProfileRowsInView()
        => CollectionViewSource.GetDefaultView(_vm.Results).Cast<ProfileResult>().ToList();

    private IReadOnlyList<LogonEventRecord> EventsInView()
        => HistoryEventsGrid.ItemsSource is { } source
            ? CollectionViewSource.GetDefaultView(source).Cast<LogonEventRecord>().ToList()
            : _historyVm.FilteredEvents.ToList();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRows();

    /// <summary>Copies the selected rows, or every row when none is selected, and says which.</summary>
    private void CopyRows()
    {
        if (LogonHistoryRadio.IsChecked == true)
        {
            var selectedEvents = HistoryEventsGrid.SelectedItems.OfType<LogonEventRecord>().ToHashSet();
            var inView = EventsInView();
            var events = selectedEvents.Count > 0 ? inView.Where(selectedEvents.Contains).ToList() : inView;
            if (events.Count == 0) return;

            CopyText(
                ResultExporter.ToLogonEventTabSeparated(events),
                CopiedMessage(events.Count, selectedEvents.Count > 0, "event"));
            return;
        }

        var selected = ResultsList.SelectedItems.OfType<ProfileResult>().ToHashSet();
        var visible = ProfileRowsInView();
        var rows = selected.Count > 0 ? visible.Where(selected.Contains).ToList() : visible;
        if (rows.Count == 0) return;

        CopyText(ResultExporter.ToTabSeparated(rows), CopiedMessage(rows.Count, selected.Count > 0, "row"));
    }

    private static string CopiedMessage(int count, bool selected, string noun)
        => selected
            ? $"Copied {Plural.Count(count, "selected " + noun)} to the clipboard."
            : count == 1
                ? $"Copied 1 {noun} to the clipboard."
                : $"Copied all {Plural.Count(count, noun)} to the clipboard.";

    private void CopyText(string text, string confirmation)
    {
        try
        {
            Clipboard.SetText(text);
            StatusLabel.Text = confirmation;
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

                var events = EventsInView();
                File.WriteAllText(
                    historyDialog.FileName,
                    ResultExporter.ToLogonEventCsv(events),
                    new System.Text.UTF8Encoding(true));
                StatusLabel.Text = $"Exported {Plural.Count(events.Count, "event")} to {historyDialog.FileName}.";
                return;
            }

            var dlg = new SaveFileDialog { FileName = "LastProfileActivity", DefaultExt = ".csv", Filter = "CSV (*.csv)|*.csv" };
            if (dlg.ShowDialog(this) != true) return;

            var rows = ProfileRowsInView();
            File.WriteAllText(dlg.FileName, ResultExporter.ToCsv(rows), new System.Text.UTF8Encoding(true));
            StatusLabel.Text = $"Exported {Plural.Count(rows.Count, "row")} to {dlg.FileName}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not write the export file: " + ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Text or CSV (*.txt;*.csv)|*.txt;*.csv|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;

        LoadComputersFromFile(dialog.FileName);
    }

    private void LoadComputersFromFile(string path)
    {
        var name = Path.GetFileName(path);
        string text;
        try
        {
            if (new FileInfo(path).Length > MaxListFileBytes)
            {
                StatusLabel.Text = $"{name} is too large to be a list of computers (the limit is 1 MB).";
                return;
            }

            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not read the file: " + ex.Message;
            return;
        }

        // A file with no names leaves the list as it was, the way an empty answer from the
        // directory does. A file with a name that cannot be used is loaded all the same, so that the
        // name can be seen beside the box and corrected.
        if (CountComputers(text) == 0)
        {
            StatusLabel.Text = $"{name} has no computer names.";
            return;
        }

        ReplaceComputers(text);
        StatusLabel.Text = CheckLoadedList() is { } loaded
            ? $"{Plural.Count(loaded, "computer")} loaded from {name}."
            : $"{name} was loaded, but it has a name that is not valid.";
    }

    /// <summary>How many computers a text names, or null when it holds a name that cannot be used.</summary>
    private static int? CountComputers(string text)
    {
        try
        {
            return InputParser.ParseComputers(text).Count;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async void FromAd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AdDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;

        await LoadComputersFromAdAsync(dialog.Prefix, dialog.Ou);
    }

    private async Task LoadComputersFromAdAsync(string prefix, string? ou)
    {
        StatusLabel.Text = "Loading computers from AD…";
        var result = await _vm.LoadFromAdAsync(prefix, ou);

        // A search that found nothing, failed or was cancelled leaves the list as it was.
        if (result.Names.Count > 0)
        {
            ReplaceComputers(string.Join(Environment.NewLine, result.Names));
            CheckLoadedList();
        }

        StatusLabel.Text = result.Message;
    }

    /// <summary>
    /// Puts a loaded list in the computers box as an edit that the box can undo, so that Ctrl+Z
    /// brings back what the operator had typed. Assigning to Text would clear the undo history.
    /// </summary>
    private void ReplaceComputers(string text)
    {
        ComputersBox.SelectAll();
        ComputersBox.SelectedText = text;
        ComputersBox.CaretIndex = 0;
        ComputersBox.ScrollToHome();
    }

    /// <summary>
    /// Checks a list that was just loaded the way a scan would, so that a name that cannot be used
    /// is shown beside the box now and not on the next Scan. Returns how many computers the list
    /// names, or null when it has a name that cannot be used.
    /// </summary>
    private int? CheckLoadedList()
    {
        try
        {
            return InputParser.ParseComputers(ComputersBox.Text).Count;
        }
        catch (FormatException ex)
        {
            ShowValidation(new ValidationIssue(ValidationField.Computers, ex.Message));
            return null;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsList.SelectedItem is not ProfileResult { CanOpenFolder: true } r) return;

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
