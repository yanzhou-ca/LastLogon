# Logon History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only, per-event Windows Security-log history view beside the existing profile-activity scan.

**Architecture:** Keep `ProfileScanner` and `ProfileResult` unchanged. Add a typed event model/parser, an injectable native Windows Event Log source, and a bounded scanner that streams event and per-computer status updates. Bind a separate history view model to a third mode in the active WPF `ScanWindow`.

**Tech Stack:** .NET 8, WPF, `System.Diagnostics.Eventing.Reader`, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-11-logon-history-design.md`

## Global Constraints

- Query the remote computer's `Security` log with the current Windows identity through the native Windows Event Log API.
- The proposed default range is the last three days, matching the event-log example; users can change it.
- Interpret the selected start/end range in the querying workstation's time zone, convert it to one shared absolute interval for every target, and compare event timestamps as UTC.
- Begin with event IDs `4624`, `4625`, `4634`, `4647`, `4800`, `4801`, and `4672`.
- Process multiple computers with bounded concurrency (proposed cap: 4) and a per-computer timeout (proposed: 10 seconds).
- All identities and categories are included by default; local/domain identity is not conflated with logon type.
- No remote machine settings, registry values, profiles, or files are changed.
- No credentials are stored or prompted for; the current Windows identity is used.
- Keep profile activity and Security-log evidence separate. Missing events are not evidence that a user did not use a computer.
- This directory is not a Git repository; use builds and tests as checkpoints, not commits.

## Review Focus

- **A range crossing computers in different time zones** must apply to the same absolute interval; test workstation-local-to-UTC conversion once in Task 5.
- **Event XML with missing or event-specific fields** must still parse, retaining available details and leaving absent cells empty; test in Task 2.
- **A successful query with zero matches versus a failed query** must produce distinct statuses; test in Task 3.
- **Local, domain, and special authority accounts plus unknown logon types** must remain visible under default filters and be categorized safely; test in Task 5.
- **Cancellation during a multi-computer scan** must preserve events already emitted and report incomplete/error status without presenting it as a successful empty scan; test in Task 3.

---

### Task 1: Restore a buildable active WPF application

**Files:**
- Modify: `LastLogon.csproj`
- Modify: `ScanWindow.xaml.cs`

**Interfaces:**
- Consumes: the active WPF `App.xaml`, `ScanWindow.xaml`, `ScanWindow.xaml.cs`, and `AdDialog.xaml`.
- Produces: a buildable .NET 8 WPF application with the existing `ScanWindow` as its startup window; unused WinUI `MainWindow.xaml` and `LookupWindow.xaml` remain excluded.

- [ ] **Step 1: Confirm the baseline failure**

Run: `dotnet build LastLogon.csproj -p:Platform=x64 --no-restore`

Expected: reproduce `WMC0011` for `StartupUri` in `App.xaml`.

- [ ] **Step 2: Align project XAML compilation with the active WPF UI**

Set `UseWPF=true`, remove `UseWinUI=true`, the `Microsoft.WindowsAppSDK` package reference, and `WindowsAppSDKSelfContained`. Compile `App.xaml`, `ScanWindow.xaml`, and `AdDialog.xaml` as WPF XAML; keep the unused WinUI `MainWindow.xaml` and `LookupWindow.xaml` excluded from compilation. Add explicit `System`, `System.Collections.Generic`, and `System.Linq` imports required by the active code-behind. Retain the existing target framework, application manifest, unpackaged app setting, and .NET self-contained publish behavior.

- [ ] **Step 3: Verify the active application builds**

Run: `dotnet build LastLogon.csproj -p:Platform=x64 --no-restore`

Expected: PASS with no XAML compiler errors.

---

### Task 2: Parse Security event XML into typed records

**Files:**
- Create: `LastLogon.Core\LogonEventRecord.cs`
- Create: `LastLogon.Core\LogonEventParser.cs`
- Test: `LastLogon.Tests\LogonEventParserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LogonEventRecord(string Computer, DateTimeOffset TimeUtc, int EventId, string EventName, string? AccountName, string? AccountDomain, string? AccountSid, int? LogonType, string? LogonTypeName, string? SourceAddress, IReadOnlyDictionary<string, string> Data)`.
  - `public static LogonEventRecord LogonEventParser.Parse(string computer, string eventXml)`.
- Account/domain/SID fields prefer `TargetUser*` event data and fall back to `SubjectUser*`; `Data` retains all named EventData fields for the detail view and CSV.

- [ ] **Step 1: Write failing parser tests**

Add tests named:
- `Parse_4624_MapsTargetAccountLogonTypeAndAddress`: assert computer, UTC timestamp, event ID/name, target account/domain/SID, logon type `2`, label `Interactive`, and source address.
- `Parse_4672_FallsBackToSubjectAccount`: assert subject identity is used when target fields are absent.
- `Parse_MapsAllSupportedEventNames`: assert friendly names for `4624`, `4625`, `4634`, `4647`, `4800`, `4801`, and `4672`.
- `Parse_EventWithMissingOptionalFields_LeavesValuesEmpty`: assert absent address, account, and logon type are null while parsing succeeds.
- `Parse_UnknownLogonType_PreservesNumber`: assert the numeric type remains available and the label identifies it as unknown.
- `Parse_InvalidXml_ThrowsFormatException`.

- [ ] **Step 2: Run parser tests to verify they fail**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~LogonEventParserTests`

Expected: FAIL because the parser/model do not exist.

- [ ] **Step 3: Implement the record and parser**

In `LogonEventParser.Parse`, read `EventID`, `TimeCreated/@SystemTime`, and named EventData values from XML. Parse event time as UTC using invariant parsing. Map IDs `4624`, `4625`, `4634`, `4647`, `4800`, `4801`, `4672` and known logon types; do not parse localized `EventRecord.Message` text.

- [ ] **Step 4: Run parser tests**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~LogonEventParserTests`

Expected: PASS.

---

### Task 3: Query and scan remote Security logs

**Files:**
- Create: `LastLogon.Core\ILogonEventSource.cs`
- Create: `LastLogon.Core\WindowsLogonEventSource.cs`
- Create: `LastLogon.Core\LogonEventScanner.cs`
- Modify: `LastLogon.Core\LastLogon.Core.csproj`
- Test: `LastLogon.Tests\LogonEventScannerTests.cs`
- Test: `LastLogon.Tests\WindowsLogonEventSourceTests.cs`

**Interfaces:**
- Produces:
  - `public interface ILogonEventSource { IEnumerable<string> ReadEventXml(string computer, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken); }`
  - `public enum LogonQueryStatus { Success, AccessDenied, Offline, TimedOut, Error }`.
  - `public abstract record LogonScanUpdate`.
  - `public sealed record LogonEventFound(LogonEventRecord Event) : LogonScanUpdate`.
  - `public sealed record LogonComputerCompleted(string Computer, LogonQueryStatus Status, string? Message, int EventCount) : LogonScanUpdate`.
  - `public interface ILogonEventScanner { IAsyncEnumerable<LogonScanUpdate> ScanAsync(IEnumerable<string> computers, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken); }`.
  - `public sealed class LogonEventScanner(ILogonEventSource source, int maxParallel = 4, TimeSpan? perComputerTimeout = null) : ILogonEventScanner`.
- Add the `System.Diagnostics.EventLog` `8.0.0` package reference to Core; it provides `System.Diagnostics.Eventing.Reader` for the existing `net8.0` target.
- `WindowsLogonEventSource` uses `EventLogSession` and `EventLogReader` against the target `Security` log, with named event XML and the current identity. The XPath query limits event IDs and the inclusive UTC range.
- `WindowsLogonEventSource.BuildXPath(DateTimeOffset fromUtc, DateTimeOffset toUtc)` returns the UTC-bounded XPath for the fixed event IDs and is independently testable.
- Emit each parsed event as it is read, followed by exactly one per-computer completion update. A successful zero-event read has `Success` and count `0`; failures have explicit status/message.

- [ ] **Step 1: Add the Event Log dependency**

Add `System.Diagnostics.EventLog` version `8.0.0` to `LastLogon.Core.csproj`.

Run: `dotnet restore LastLogon.slnx`

Expected: PASS with the new package in the Core assets.

- [ ] **Step 2: Write failing scanner/source tests**

Add tests named:
- `WindowsSource_BuildsXPathForSupportedIdsAndUtcRange`: assert the XPath contains all seven IDs and encodes the supplied UTC bounds.
- `ScanAsync_ReportsSuccessfulEmptySeparatelyFromFailure`: assert an empty read emits `Success` with count `0`, while access denied emits `AccessDenied`.
- `ScanAsync_ReportsOfflineTimeoutAndOtherErrors`: assert directory/network failure, timeout, and unexpected exceptions map to distinct statuses.
- `ScanAsync_RespectsConcurrencyLimit`: with eight delayed computers and cap `2`, assert peak source concurrency is at most `2`.
- `ScanAsync_TimedOutReadRetainsCapacityUntilItExits`: assert a timed-out reader continues to occupy its slot until the underlying read stops.
- `ScanAsync_Cancelled_KeepsAlreadyEmittedEvents`: cancel after an event update and assert it remains observable and the scan does not report a successful complete result for unfinished computers.
- `ScanAsync_CancelledWhileQueued_ReportsEveryComputer`: cancel with a second target waiting for a slot and assert both targets receive incomplete completion statuses.

- [ ] **Step 3: Run scanner/source tests to verify they fail**

Run: `dotnet test LastLogon.Tests --no-restore --filter "FullyQualifiedName~LogonEventScannerTests|FullyQualifiedName~WindowsLogonEventSourceTests"`

Expected: FAIL because the event source/scanner do not exist.

- [ ] **Step 4: Implement the native event source and scanner**

Build the Security-log XPath from the seven fixed IDs and UTC bounds. Dispose event-log sessions/readers and register cancellation with `EventLogReader.CancelReading`. In the scanner, apply a `SemaphoreSlim` cap of `4`, retain each permit until its read task exits, use the `10` second default per-computer timeout, parse each XML record with `LogonEventParser`, preserve already-emitted updates on cancellation, emit one completion per target (including queued targets cancelled before starting), and map access-denied/offline/timeout/other exceptions explicitly.

- [ ] **Step 5: Run scanner/source tests**

Run: `dotnet test LastLogon.Tests --no-restore --filter "FullyQualifiedName~LogonEventScannerTests|FullyQualifiedName~WindowsLogonEventSourceTests"`

Expected: PASS.

---

### Task 4: Export and copy event rows

**Files:**
- Modify: `LastLogon.Core\ResultExporter.cs`
- Test: `LastLogon.Tests\ResultExporterTests.cs`

**Interfaces:**
- Produces:
  - `ResultExporter.ToLogonEventCsv(IEnumerable<LogonEventRecord> rows)`.
  - `ResultExporter.ToLogonEventTabSeparated(IEnumerable<LogonEventRecord> rows)`.
- Keep existing profile export methods unchanged. Event CSV has a fixed header for computer, local display time, UTC time, event ID/name, account/domain/SID, logon type/label, source address, and details JSON. Fields absent from an event are empty. Reuse existing CSV escaping and formula-prefix protection.

- [ ] **Step 1: Write failing export tests**

Add tests named:
- `ToLogonEventCsv_HasFixedHeaderAndEventFields`.
- `ToLogonEventCsv_QuotesCommasAndQuotesInFields`.
- `ToLogonEventCsv_ProtectsFormulaPrefixes`.
- `ToLogonEventTabSeparated_HasHeaderAndSanitizesTabs`.

- [ ] **Step 2: Run exporter tests to verify they fail**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~ResultExporterTests`

Expected: new event-export tests FAIL; existing profile-export tests remain passing.

- [ ] **Step 3: Implement event export methods**

Add the event-specific fixed columns and serialize the retained `Data` dictionary into the final details cell. Use the existing `Csv` escaping helper and tab sanitization for TSV.

- [ ] **Step 4: Run exporter tests**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~ResultExporterTests`

Expected: PASS.

---

### Task 5: Add the logon-history view model

**Files:**
- Create: `LastLogon.Core\LogonHistoryViewModel.cs`
- Create: `LastLogon.Core\LogonEventFilters.cs`
- Test: `LastLogon.Tests\LogonHistoryViewModelTests.cs`

**Interfaces:**
- Consumes: `ILogonEventScanner`, `InputParser.ParseComputers`, `LogonEventRecord`, `LogonScanUpdate`, and `ResultExporter`.
- Produces `LogonHistoryViewModel` with:
  - `string ComputersText`.
  - `DateTimeOffset StartLocal` and `EndLocal`.
  - `ObservableCollection<LogonEventRecord> Events`.
  - `ObservableCollection<LogonComputerCompleted> ComputerStatuses`.
  - `LogonEventRecord? SelectedEvent`.
  - `IReadOnlyList<KeyValuePair<string, string>> SelectedDetails`.
  - `LogonAccountFilter AccountFilter` (`All`, `Local`, `Domain`), default `All`.
  - `LogonTypeFilter TypeFilter` (`All`, `Interactive`, `Network`, `Service`, `Other`), default `All`.
  - `bool IsScanning`, `string StatusText`, `Task ScanAsync()`, `void Cancel()`.
- `ScanAsync` converts the entered local interval to UTC once before invoking the scanner. All-filter defaults retain all event records, including unknown/special accounts and types. Local/domain classification compares the event account domain to the target computer name; special authority domains and missing domains remain visible under `All`.

- [ ] **Step 1: Write failing view-model tests**

Add tests named:
- `ScanAsync_ConvertsOneSharedLocalRangeToUtc`: assert the fake scanner receives the same UTC bounds for the whole computer list.
- `ScanAsync_RejectsEmptyComputerListAndReversedRange`: assert validation status and no scanner call.
- `ScanAsync_StreamsEventsAndCompletionStatuses`: assert events and per-PC statuses appear as scanner updates arrive.
- `Cancel_KeepsAlreadyReceivedEvents`.
- `Filters_DefaultToAllAndRetainUnknownAccountsAndTypes`.
- `Filters_LocalDomainAndLogonTypeIndependently`: assert account-domain scope is distinct from logon-type categories.
- `Events_ArrivingOutOfOrder_AreInsertedNewestFirst`.
- `SelectedEvent_ExposesAvailableDetails`: assert the selected record's named data is available for the details section.

- [ ] **Step 2: Run view-model tests to verify they fail**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~LogonHistoryViewModelTests`

Expected: FAIL because the view model and filters do not exist.

- [ ] **Step 3: Implement the filters and view model**

Use the existing `INotifyPropertyChanged`/dispatcher pattern from `MainViewModel`. Validate a non-empty computer list and `StartLocal < EndLocal`, convert the interval to UTC once, insert each arriving event in newest-first order without clearing partial results on cancellation, and keep failures distinct from empty success.

- [ ] **Step 4: Run view-model tests**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~LogonHistoryViewModelTests`

Expected: PASS.

---

### Task 6: Add the Logon History mode to the active window

**Files:**
- Modify: `ScanWindow.xaml`
- Modify: `ScanWindow.xaml.cs`

**Interfaces:**
- Consumes: `LogonHistoryViewModel`.
- Produces: a third **Logon History** mode in `ScanWindow`, preserving existing profile modes and their controls.

- [ ] **Step 1: Add the history-mode controls and layout**

Add the third mode selector, local start/end date-time inputs defaulting to the last three days, account/logon-type filters, and a wide event table with the selected-event details section below it. Keep account identity/domain and logon type in separate columns. Use the WPF controls and styling already present in `ScanWindow.xaml`.

- [ ] **Step 2: Wire scan, cancel, details, copy, and CSV export**

Instantiate `WindowsLogonEventSource`, `LogonEventScanner`, and `LogonHistoryViewModel` in `ScanWindow`. Route history-mode scans/cancellation to the new view model, bind event rows and selected details, and use the new exporter methods. Leave existing profile scanning and export behavior unchanged.

- [ ] **Step 3: Build the active application**

Run: `dotnet build LastLogon.csproj -p:Platform=x64 --no-restore`

Expected: PASS with the new WPF XAML and code-behind compiled.

- [ ] **Step 4: Manually verify the event-history interaction**

On a Windows computer with Security-log access, query a local and a remote computer over a three-day range. Confirm the wide table, selected details below, account/domain and logon type distinction, local-time display, UTC-range consistency, cancellation, CSV export, access-denied/offline messages, and successful-empty status.

---

### Task 7: Update related documentation and run full verification

**Files:**
- Modify: `docs/superpowers/specs/2026-11-10-lastlogon-gui-design.md`
- Modify: `docs/superpowers/plans/2026-11-10-lastlogon-gui.md`

**Interfaces:**
- Documents: the unchanged profile-activity behavior and the new, separate Security-log history mode; link to `docs/superpowers/specs/2026-11-11-logon-history-design.md` as the detailed source of truth for event history.

- [ ] **Step 1: Update the original design and plan**

Link the prior design to the new history design, clarify that its profile-activity decisions remain in force, and preserve the original document's historical approach selection. Add a brief pointer in the old plan to the separate history scope without rewriting its completed task sequence.

- [ ] **Step 2: Run all tests**

Run: `dotnet test LastLogon.slnx --no-restore`

Expected: PASS for all existing and new tests.

- [ ] **Step 3: Run the application build**

Run: `dotnet build LastLogon.csproj -p:Platform=x64 --no-restore`

Expected: PASS.

---

### Task 8: Close final review findings and verify Release build

**Files:**
- Modify: `LastLogon.Core\LogonEventScanner.cs`
- Modify: `LastLogon.Core\WindowsLogonEventSource.cs`
- Modify: `LastLogon.Tests\LogonEventScannerTests.cs`
- Modify: `LastLogon.csproj`
- Modify: `docs\superpowers\specs\2026-11-11-logon-history-design.md`

**Interfaces:**
- Preserve bounded source concurrency across timeouts and emit an incomplete completion for every target when cancellation occurs before its query starts.
- Keep WPF Release builds compatible with the .NET SDK by disabling trimming; retain ReadyToRun for non-Debug configurations.

- [ ] **Step 1: Write failing scanner regression tests**

Add `ScanAsync_TimedOutReadRetainsCapacityUntilItExits` and `ScanAsync_CancelledWhileQueued_ReportsEveryComputer`.

- [ ] **Step 2: Run the scanner tests and verify both fail for the reviewed defects**

Run: `dotnet test LastLogon.Tests --no-restore --filter FullyQualifiedName~LogonEventScannerTests`

Expected: FAIL in both new cases before the implementation changes.

- [ ] **Step 3: Implement cancellation, completion, and Release configuration fixes**

Use `EventLogReader.CancelReading` when the query token is cancelled; retain the semaphore slot until the read task exits; emit an `Error` completion for a target cancelled before acquiring a slot; disable trimming for the WPF app.

- [ ] **Step 4: Run the full Release test suite**

Run: `dotnet test LastLogon.slnx -c Release --no-restore`

Expected: PASS.

- [ ] **Step 5: Build the x64 Release app**

Run: `dotnet build LastLogon.csproj -c Release -p:Platform=x64 --no-restore`

Expected: PASS with 0 warnings and 0 errors.
