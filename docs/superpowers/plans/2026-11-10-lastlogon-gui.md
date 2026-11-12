# LastLogon GUI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> Scope note (2026-11-11): This plan remains the implementation history for profile-activity modes, and its task sequence is unchanged. The original proposal targeted WinUI 3; the active application was subsequently aligned to WPF. The separate Security-log Logon History mode is specified in the [logon-history design](../specs/2026-11-11-logon-history-design.md) and [logon-history plan](2026-11-11-logon-history.md).

**Goal:** A WinUI 3 tool that shows recent profile activity by computer, and by user across a list of computers, using `NTUSER.DAT` timestamps over `\\PC\C$`.

**Architecture:** A testable class library (`LastLogon.Core`) holds the scanner, an injectable file-system abstraction, the computer-list parser, CSV export, and an optional AD computer search. The existing WinUI project (`LastLogon`) holds a view model and the window. Scans run async, in parallel (cap 8), with per-PC timeouts, and stream results into the grid.

**Tech Stack:** .NET 8, WinUI 3 (Windows App SDK 2.6.5, already referenced), xUnit for tests, `System.DirectoryServices` for the AD search.

**Spec:** `docs/superpowers/specs/2026-11-10-lastlogon-gui-design.md`

## Global Constraints

- Target `net8.0` for Core and Tests; the existing app stays `net8.0-windows10.0.19041.0`.
- The UI label is "Last profile activity", never "Last logon".
- Use the current Windows identity. No credential prompts. Access denied is a status.
- Skip the profiles Public, Default, Default User, All Users, and `desktop.ini`.
- Stale threshold default is 90 days, adjustable.
- Parallelism cap is 8, with a per-PC timeout and a Cancel button. The UI must never block.
- Read-only: no remote changes.
- No source control: this folder is not a git repository. Checkpoint steps are "build and test pass".

## Review Focus

- Hostname input with whitespace, blank lines, duplicates, `\\PC` prefixes, or mixed case: it is trimmed, de-duplicated case-insensitively, and the prefix is stripped.
- Username typed as `DOMAIN\jdoe` or `jdoe@corp.local`: it is reduced to `jdoe`.
- `NTUSER.DAT` missing but the folder exists (profile never loaded): fall back to the folder time and mark the source as `Folder`.
- Offline or unreachable PC: reported as Offline within the timeout, and the scan continues.
- Cancel mid-scan: stops quickly and keeps the partial results.

---

### Task 1: Core library and test project scaffolding

**Files:**
- Create: `LastLogon.Core\LastLogon.Core.csproj` (net8.0, Nullable enable)
- Create: `LastLogon.Tests\LastLogon.Tests.csproj` (xUnit, references Core)
- Modify: `LastLogon.slnx` (add both projects)
- Modify: `LastLogon.csproj` (add a ProjectReference to Core)

**Interfaces:**
- Produces: namespace `LastLogon.Core`, used by every later task.

- [ ] **Step 1:** Create the projects with `dotnet new classlib` and `dotnet new xunit`, then add them to the slnx with `dotnet sln LastLogon.slnx add`.
- [ ] **Step 2:** Run `dotnet test LastLogon.Tests`. Expected: builds and runs 0 tests with no errors.
- [ ] **Step 3:** Build the app with `dotnet build LastLogon.csproj -p:Platform=x64`. Expected: success.

### Task 2: Input normalization

**Files:**
- Create: `LastLogon.Core\InputParser.cs`
- Test: `LastLogon.Tests\InputParserTests.cs`

**Interfaces:**
- Produces:
  - `static class InputParser`
  - `static IReadOnlyList<string> ParseComputers(string text)`
  - `static string NormalizeUsername(string input)`

- [ ] **Step 1: Write failing tests** (names and assertions):
  - `ParseComputers_TrimsDedupesIgnoresBlank`: `" pc01\r\n\r\nPC01\n\\\\pc02 ,pc03"` returns `["pc01","pc02","pc03"]`. Separators are newline, comma, semicolon, and whitespace. The first-seen casing is kept.
  - `NormalizeUsername_StripsDomainAndUpn`: `"CORP\\jdoe"`, `"jdoe@corp.local"`, and `" jdoe "` all return `"jdoe"`.
  - `NormalizeUsername_Empty_ReturnsEmpty`.
- [ ] **Step 2:** Run `dotnet test --filter InputParserTests`. Expected: FAIL.
- [ ] **Step 3:** Implement the two methods as signed above.
- [ ] **Step 4:** Run the same command. Expected: PASS.

### Task 3: Profile scanner

**Files:**
- Create: `LastLogon.Core\Models.cs`, `LastLogon.Core\IProfileFileSystem.cs`, `LastLogon.Core\ProfileScanner.cs`
- Test: `LastLogon.Tests\ProfileScannerTests.cs` (uses a fake `IProfileFileSystem`)

**Interfaces:**
- Produces:
  - `enum ScanStatus { Ok, Offline, AccessDenied, NoProfile, Error }`
  - `enum ActivitySource { NtUserDat, Folder }`
  - `record ProfileResult(string Computer, string Username, DateTime? LastActivity, ActivitySource? Source, ScanStatus Status, string? Message)`, with the computed property `int? DaysAgo`.
  - `interface IProfileFileSystem` with:
	- `IReadOnlyList<string> ListUserFolders(string computer)`. It throws `DirectoryNotFoundException`, `UnauthorizedAccessException`, or `IOException`.
	- `DateTime? GetNtUserDatTime(string computer, string user)`. It returns null if the file is missing.
	- `DateTime GetFolderTime(string computer, string user)`.
  - `class SmbProfileFileSystem : IProfileFileSystem`, which uses `\\computer\C$\Users`.
  - `class ProfileScanner(IProfileFileSystem fs, int maxParallel = 8, TimeSpan? perPcTimeout = null)` with:
	- `Task<IReadOnlyList<ProfileResult>> ScanComputerAsync(string computer, CancellationToken ct)`. Results are sorted newest first, system profiles are skipped, and failures return a single result with a status and message.
	- `IAsyncEnumerable<ProfileResult> FindUserAsync(string username, IEnumerable<string> computers, CancellationToken ct)`. It streams results as each PC finishes, runs at most `maxParallel` at a time, and yields only PCs where the profile exists or whose status is not `NoProfile`.

- [ ] **Step 1: Write failing tests**:
  - `ScanComputer_ListsProfilesNewestFirst_SkipsSystemFolders`.
  - `ScanComputer_MissingNtUserDat_UsesFolderTime` (Source is `Folder`).
  - `ScanComputer_DirectoryNotFound_ReturnsOffline`, and `UnauthorizedAccess_ReturnsAccessDenied`.
  - `ScanComputer_Timeout_ReturnsOffline`. The fake blocks for longer than the 100 ms timeout.
  - `FindUser_ReturnsOnlyComputersWithProfile_IncludingErrors` (offline PCs appear with a status, profile-less PCs are omitted).
  - `FindUser_RespectsMaxParallel`. The fake tracks concurrency, and the peak is at most 2 when `maxParallel` is 2.
  - `FindUser_Cancelled_StopsAndKeepsYieldedResults`.
- [ ] **Step 2:** Run `dotnet test --filter ProfileScannerTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Wrap the synchronous file-system calls in `Task.Run` and race them against the timeout. Use a `SemaphoreSlim` for the cap and a `Channel<ProfileResult>` for streaming. Match the username case-insensitively. Also match folders of the form `user.DOMAIN` or `user.000`, since Windows can append suffixes.
- [ ] **Step 4:** Run the same command. Expected: PASS.

### Task 4: CSV export and clipboard text

**Files:**
- Create: `LastLogon.Core\ResultExporter.cs`
- Test: `LastLogon.Tests\ResultExporterTests.cs`

**Interfaces:**
- Consumes: `ProfileResult`.
- Produces:
  - `static string ToCsv(IEnumerable<ProfileResult> rows)`
  - `static string ToTabSeparated(IEnumerable<ProfileResult> rows)`
  - Columns: Computer, Username, LastProfileActivity (ISO `yyyy-MM-dd HH:mm`), DaysAgo, Source, Status, Message.

- [ ] **Step 1: Write failing tests**: `ToCsv_QuotesCommasAndQuotes`, `ToCsv_NullDate_EmptyCell`, and `ToTabSeparated_HasHeaderRow`.
- [ ] **Step 2:** Run `dotnet test --filter ResultExporterTests`. Expected: FAIL.
- [ ] **Step 3:** Implement both methods as signed above.
- [ ] **Step 4:** Run the same command. Expected: PASS.

### Task 5: AD computer search (optional source)

**Files:**
- Create: `LastLogon.Core\AdComputerSearch.cs`
- Test: `LastLogon.Tests\AdComputerSearchTests.cs`

**Interfaces:**
- Produces:
  - `static string BuildFilter(string namePrefix)`. It returns an LDAP filter such as `(&(objectCategory=computer)(name=PREFIX*))`.
  - `static Task<IReadOnlyList<string>> SearchAsync(string namePrefix, string? ouDistinguishedName, CancellationToken ct)`, which uses `System.DirectoryServices.DirectorySearcher` under the current identity.

- [ ] **Step 1: Write failing tests**:
  - `BuildFilter_EscapesLdapSpecialChars`: `"pc*(x)"` is escaped per RFC 4515 (`\2a`, `\28`, `\29`).
  - `BuildFilter_EmptyPrefix_MatchesAllComputers`.
- [ ] **Step 2:** Run `dotnet test --filter AdComputerSearchTests`. Expected: FAIL.
- [ ] **Step 3:** Add the `System.DirectoryServices` package to Core and implement. `SearchAsync` is exercised manually in Task 7; it is not unit-tested.
- [ ] **Step 4:** Run the same command. Expected: PASS.

### Task 6: View model

**Files:**
- Create: `LastLogon.Core\MainViewModel.cs` (no WinUI types, so it is testable; the window only binds to it)
- Test: `LastLogon.Tests\MainViewModelTests.cs`

**Interfaces:**
- Consumes: `ProfileScanner`, `InputParser`, `ResultExporter`, `AdComputerSearch`.
- Produces `class MainViewModel` (`INotifyPropertyChanged`) with:
  - `enum LookupMode { ByComputer, ByUser }`, `LookupMode Mode`
  - `string ComputersText`, `string Username`
  - `int StaleDays = 90`
  - `ObservableCollection<ProfileResult> Results`
  - `bool IsScanning`, `string StatusText`
  - `Task ScanAsync()`, `void Cancel()`, `Task LoadFromAdAsync(string prefix, string? ou)`
  - `bool IsStale(ProfileResult r)`

- [ ] **Step 1: Write failing tests** (using a fake file system):
  - `Scan_ByComputer_FillsResultsNewestFirst`.
  - `Scan_ByUser_StreamsResultsAsTheyArrive`.
  - `Scan_ByUser_BlankUsername_SetsStatusAndDoesNotScan`.
  - `Cancel_StopsScanAndKeepsPartialResults`.
  - `IsStale_UsesStaleDays`: 91 days old is stale with the default and 89 days is not.
  - `Scan_ClearsPreviousResults`.
- [ ] **Step 2:** Run `dotnet test --filter MainViewModelTests`. Expected: FAIL.
- [ ] **Step 3:** Implement. Marshal results to the UI thread through an injected `Action<Action>` dispatcher, which tests replace with a direct call.
- [ ] **Step 4:** Run the same command. Expected: PASS.

### Task 7: WinUI window

**Files:**
- Modify: `MainWindow.xaml`, `MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel`.

Layout:
- Top: a mode toggle (By computer / By user). In By computer mode it shows one computer box. In By user mode it shows a username box plus a multi-line computer list with an "Import .txt" button and a "From AD…" button (name prefix and an optional OU).
- Middle: Scan and Cancel buttons, a stale-days NumberBox, and a ProgressRing.
- Main: a results grid with columns Computer, Username, Last profile activity, Days ago, Source, Status. Stale rows are highlighted.
- Footer: StatusText.
- Row actions: Copy rows, Export CSV (file picker), and Open profile folder (`explorer \\pc\C$\Users\<user>`).

- [ ] **Step 1:** Build the XAML as above and bind it to the view model, using `DispatcherQueue.TryEnqueue` as the dispatcher.
- [ ] **Step 2:** Run `dotnet build LastLogon.csproj -p:Platform=x64`. Expected: success.
- [ ] **Step 3: Manual verification**:
  - Run on a domain PC with admin rights. By computer on `localhost`, then on a real PC: profiles are listed newest first.
  - By user with 3 PCs, including one offline and one nonexistent: the offline PC shows Offline within the timeout and the UI stays responsive.
  - Cancel mid-scan, Export CSV, Copy rows, and Open folder all work.
  - A non-admin access-denied case shows AccessDenied with no prompt.

### Task 8: Final verification

- [ ] **Step 1:** Run `dotnet test LastLogon.slnx`. Expected: all tests pass.
- [ ] **Step 2:** Re-read the spec's Goals, Anti-goals, and Modes against the running app and fix any gap.
