# Logon History — Design

Status: draft, awaiting user approval.

## Intent

Add a Security event-log history view to LastLogon so support staff can inspect
individual logon-related events, not just the approximate last activity of each
profile. The existing profile-activity scan remains available and keeps its
current meaning.

The inspiration is the event list and selected-event details shown in
[PC_LogonEventLog](https://github.com/Munnster2001/PC_LogonEventLog), with
profile metadata ideas from
[profiletimes](https://github.com/ianjhart2718/profiletimes). This design uses
the examples as feature references; it does not copy their code.

## Goals

- Query logon-related events from the Windows Security log on one or more
  computers.
- Include local-machine and domain identities, and represent account domain
  separately from Windows logon type (interactive, network, RDP, service, etc.).
- Show event rows in a wide, sortable/scrollable table, with selected-event
  details below it.
- Allow the user to select a time range, cancel a query, and export event rows
  to CSV.
- Show per-computer access, timeout, and query errors explicitly.
- Keep profile activity and Security-log evidence separate in the UI and data
  model.

## Non-goals

- Replacing or changing the existing profile-activity lookup.
- Remote profile cleanup, registry repair, or orphan-folder deletion.
- Claiming a complete or authoritative audit of computer use.
- Centralized log collection, credential prompts, or changing target audit
  policy.
- Implementing the profile registry/load-unload-time source in this change.

## Anti-goals

- Calling an event-log timestamp “last profile activity” or a profile-file
  timestamp a definitive logon.
- Treating an empty result as proof that no logon occurred.
- Hiding local, domain, network, or service identities by default.
- Parsing localized event message text when named event-data fields are
  available.

## Current state and constraints

- `ScanWindow.xaml` is the active startup window (`App.xaml` points to it).
  Its XAML and code-behind use WPF controls and the `System.Windows` APIs.
- The project is currently configured with `UseWinUI=true`, and the baseline
  `dotnet build LastLogon.csproj -p:Platform=x64 --no-restore` fails with
  `WMC0011` because `StartupUri` in `App.xaml` is not valid for that WinUI
  application definition. A consistent, buildable app/UI configuration is a
  prerequisite. The proposed call is to retain the active `ScanWindow` UX and
  reconcile the project setup, rather than porting the feature to the unused
  `MainWindow.xaml`.
- Core scanning logic and testable models live in `LastLogon.Core`; tests use
  xUnit and fake service boundaries. The existing `ResultExporter` handles
  CSV escaping, including formula-prefix protection.
- Existing scans use the current Windows identity, bounded parallelism,
  cancellation, and per-computer timeouts. Event-log reads will follow those
  reliability and credential principles.
- Reading remote Security logs requires the caller to have permission and
  remote Event Log access to be available. Audit policy and log retention vary
  by computer.

## Approaches

1. **Native .NET Windows Event Log API (selected).** Add an event-log service
   using `System.Diagnostics.Eventing.Reader`, returning typed records to Core.
   This integrates with the existing .NET app and avoids a helper-process
   contract. It requires careful event-data parsing and explicit handling of
   remote access limitations.
2. **PowerShell helper using `Get-WinEvent`.** This is close to the reference
   script and could speed up a prototype, but requires launching and packaging
   a script, managing its process and errors, and serializing structured event
   data back to the application. It is not selected.
3. **Enrich profile rows with registry timestamps.** This is a different,
   read-only profile-health feature, not the event detail view requested here;
   it is deferred. Destructive cleanup operations from the reference are
   explicitly out of scope.

## Design

### Navigation and layout

- Add a distinct **Logon History** mode in the active `ScanWindow`; preserve
  the existing profile modes and results.
- Logon History accepts one or more short computer names, reusing current
  computer-list normalization and import/AD-list affordances where practical.
- Provide start and end date/time controls. The proposed default range is the
  last three days, matching the event-log example; users can change it.
- Present a wide event table with these primary columns:
  - Time
  - Computer
  - Account
  - Account domain
  - Logon type (number and friendly label)
  - Source address
  - Event (ID and friendly name)
- Place a selected-event details section below the table (the user's chosen
  layout). It displays available target, subject, workstation, logon process,
  authentication package, and other relevant named event-data fields.
- Offer optional account-scope and logon-type filters. All identities and
  categories are included by default; local/domain identity is not conflated
  with logon type.
- Retain cancellation and progress/status feedback. Show per-computer
  failures and queried time range even when no events are returned.
- Provide Copy and CSV export for event rows. CSV uses a fixed set of columns;
  fields absent from a particular event are empty. Reuse the established CSV
  escaping/formula-injection-safe behavior.

### Event retrieval and data

- Add a separate typed event model and event-log scanner/service; do not put
  event details into `ProfileResult` or change `ProfileScanner`.
- Query the remote computer's `Security` log with the current Windows
  identity through the native Windows Event Log API.
- Begin with the event IDs used by the reference implementation:
  `4624`, `4625`, `4634`, `4647`, `4800`, `4801`, and `4672`. Keep the ID set
  centralized so it can be adjusted without changing UI code.
- Parse named event XML fields into a normalized event row and a details
  collection. Do not depend on localized `EventRecord.Message` text for
  structured values.
- Interpret the selected start/end range in the querying workstation's time
  zone, convert it to one shared absolute interval for every target, and
  compare event timestamps as UTC. Display timestamps in the workstation's
  local time and include an ISO-8601 timestamp with offset in CSV.
- Sort events newest first. Process multiple computers with bounded
  concurrency (proposed cap: 4) and a per-computer timeout (proposed: 10
  seconds). Cancellation stops outstanding queries where supported and keeps
  already collected rows. A query keeps its concurrency slot until its reader
  has actually stopped, even when timeout or cancellation is reported first.
- Emit one completion status for every requested computer, including targets
  that were still queued when the user cancelled the scan.
- Report access denied, offline/unreachable, timeout, and other errors per
  computer. A successful query with zero matching records is distinct from a
  failed query.

### Reliability and interpretation

- Surface a visible note that results are limited by Security-log permissions,
  audit policy, retention, and the selected time range.
- Missing events are not evidence that a user did not use a computer.
- No remote machine settings, registry values, profiles, or files are changed.
- No credentials are stored or prompted for; the current Windows identity is
  used.

### Testing

- Test event XML parsing with representative records for successful/failed
  logons, logoff, lock/unlock, and special logon, including absent optional
  fields and local/domain accounts.
- Test time-range filtering, ordering, account/logon-type filters, and CSV
  escaping using an injected event source/fake; unit tests must not require
  access to a real Security log.
- Test cancellation, bounded concurrency, timeout, access-denied, offline,
  other error, and successful-empty outcomes.
- Verify the active application builds after reconciling the WPF/WinUI project
  mismatch, and manually verify remote access/error messaging on a suitable
  Windows domain machine.

## Decisions already made

- User selected **Logon History** as a separate event-level view.
- User selected the wide table with selected-event details below it.
- Both local and domain accounts matter, as do local/interactive and network
  logon types; these are represented as separate dimensions.
- User selected native .NET event-log access over a PowerShell helper.
- The selected time range is one shared absolute interval entered in the
  workstation's time zone, so results from computers in different time zones
  remain comparable.
- Existing profile activity remains unchanged; CSV export is also available
  for logon-event results.
- Cancel native event-log reads through `EventLogReader.CancelReading`, and
  retain the concurrency permit until the underlying read task exits. A
  computer cancelled before its query starts receives an incomplete status.

## Left to the builder

- Exact XAML controls for date/time selection and optional filters.
- Exact event-type labels and how variable-length details are presented.
- Whether a queried event count needs a UI virtualization or paging safeguard
  after measuring representative results.
- Final implementation details for reconciling the project configuration,
  provided `ScanWindow` remains the active UI and the app builds cleanly.
