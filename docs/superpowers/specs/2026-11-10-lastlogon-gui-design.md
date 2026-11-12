# LastLogon GUI – Profile Activity Design

Status: Approved for profile-activity scope. The original file-share approach remains in force for the By computer and By user modes. The separate Security-log history mode is specified in the [logon-history design](./2026-11-11-logon-history-design.md).

Implementation note: The original proposal targeted WinUI 3; the active application was subsequently aligned to WPF. This does not change the selected file-share approach or profile-activity decisions.

Historical note: At the time this draft was first written, the user was unavailable; the later approval and environment decisions are recorded below. Nothing had been built at initial draft time.

## Intent
Replace the one-liner that reads `\\PC\C$\Users\<user>\NTUSER.DAT` LastWriteTime with a WinUI 3 tool (existing project, .NET 8) that helps desktop support techs answer "who used this PC, and when?".

## Goals
- Look up one or more computers and show every profile on each, with last activity time.
- Make failures readable: offline, access denied, no `C$`, profile not found.
- Copy/export results (CSV).

## Non-goals
- Profile-activity timestamps are not audit or forensics evidence.
- No remote changes (no profile deletion in v1).
- No AD/Entra reporting.

## Anti-goals
- Presenting NTUSER.DAT time as a definitive "last logon". It is last profile activity (written at logoff and periodically during a session). The UI must say "Last profile activity".
- Freezing the UI while a PC times out.

## Constraints
- Needs admin rights on the target and `C$` reachable.
- Existing project: WinUI 3, Mica backdrop, empty MainWindow.

## Approaches
1. **File-share only (recommended for v1).** Enumerate `\\PC\C$\Users\*`, read the `NTUSER.DAT` time, and fall back to the folder time. It is simple, fast, needs no extra services, and is a direct port of the one-liner. Trade-off: it is an approximation.
2. **Share + registry ProfileList.** Adds the SID to account mapping, the true profile path, and LocalProfileLoad/UnloadTime. More accurate, but needs the Remote Registry service, which is often disabled.
3. **Event Log 4624/4634 queries.** Most accurate. Slow, needs log access and auditing enabled, and is heavy for a first version.

Pick: 1, with a structure that allows adding 2 later as another "source".

This selection remains the source for profile-activity results; event history does not replace or reinterpret `NTUSER.DAT` activity.

## Design
- Input: a computer name box (multi-line for several names) with a Scan button and a Cancel button.
- Output: a grid with Computer, Username, Last profile activity, "Days ago", Status/Error, and a colour hint for stale profiles (default more than 90 days; adjustable).
- Optional username filter. Skip system profiles (Public, Default, All Users).
- Scans run in parallel (cap about 8) and are async with per-PC timeouts, so the UI stays responsive.
- Actions: Copy rows, Export CSV, Open the profile folder in Explorer.
- Layers: `ProfileScanner` (pure service, testable) and `MainWindow` with a view model.

## Left to the builder
Exact stale threshold UI, timeout values, grid control choice, and the UI layout.

## Modes (from the user's answer, 2026-11-10 15:28)
Typical use: one user at a time. Two lookups, same scanner, same grid:
1. **By computer**: list all recently active usernames on a PC, most recent first.
2. **By user**: find which computers a user recently logged into, most recent first. This scans a computer list, checking `\\PC\C$\Users\<user>\NTUSER.DAT` on each, and lists only PCs where the profile exists. The scan streams results as each PC responds.

## Approval and environment (2026-11-10 15:30)
Design approved by the user. Environment assumption from the user: the tool runs on a domain-joined computer with an AD admin account already signed in. So:
- No credential prompt. Use the current Windows identity for share access.
- AD is available, so the "by user" computer list may also come from an AD computer search (OU or name prefix). This is in scope as an optional source alongside pasted/imported lists. My call: a pasted list is built first, AD search second.
- Access denied is reported as a status, never prompted for.

