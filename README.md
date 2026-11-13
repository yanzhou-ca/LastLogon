# LastLogon

A Windows tool for support staff. It answers two questions about remote computers:

- **Who used this PC, and when?**
- **Which PCs did this user use?**

![By user mode, with made-up data](docs/images/sample-by-user.png)

The screenshot uses made-up computer names, a made-up domain and a made-up user.

## Lookup modes

- **By computer:** every user profile on a PC, with the time of its last profile activity, newest first.
- **By user:** the PCs from a list that have a profile for one user. Every computer checked gets a row: a profile found, no profile, or the reason it could not be checked. The status line gives the totals, for example `Done. 6 computer(s) checked: 3 with a profile, 1 without, 2 could not be checked.`
- **Logon history:** logon, logoff, lock, unlock, failed-logon and special-logon events from the remote Security log. You can filter by account and logon type, select an event to see all its fields, and export to CSV.

Access-denied rows also show the address each computer name resolves to. That separates a refused account from a name that reaches a different computer.

## Read the results with care

- **Last profile activity** is the time the user's `NTUSER.DAT` file was last written, which happens at logoff. For someone who is still logged on it shows the previous logoff, not the current time. Use **Logon history** for current activity.
- **A missing record is not proof** that a user never used a PC. Profiles get cleaned up, folders can carry a domain suffix, and event logs are overwritten.

## Requirements

- Windows 10 or 11, 64-bit.
- The **.NET 8 Desktop Runtime** for the framework-dependent download. Microsoft ended support for .NET 8 on 10 November 2026. The self-contained build in [docs/publishing.md](docs/publishing.md) avoids needing the runtime.
- Run the tool as an administrator. The account you use needs administrator rights on each target computer.
- For **Logon history**, the target computers need the *Remote Event Log Management* firewall rules enabled, and logon auditing turned on.

## Download

Get the latest build from the [Releases](https://github.com/yanzhou-ca/LastLogon/releases) page. Check the file against the SHA-256 value published with the release.

The executable is not code-signed, so Windows SmartScreen may warn the first time you start it.

## Build from source

You need the .NET 8 SDK on Windows.

```powershell
dotnet restore LastLogon.slnx --use-lock-file
dotnet build LastLogon.slnx -c Release --no-restore
dotnet test LastLogon.Tests/LastLogon.Tests.csproj
dotnet publish LastLogon.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore
```

The published file is written to `publish\win-x64\LastLogon.exe`. [docs/publishing.md](docs/publishing.md) describes the other publish profiles, including a self-contained build that needs no runtime.

## Project layout

- `LastLogon.Core/`: the scanning and filtering logic, with no user interface.
- `ScanWindow.xaml` and `ScanWindow.xaml.cs`: the WPF window.
- `LastLogon.Tests/`: the unit tests.
- `docs/`: the design specs, the publishing guide, and the [QA review](docs/qa/2026-11-12-qa-audit.md) with its open findings.

## Behaviour and limits

- The tool only reads. It changes nothing on the computers it checks.
- It uses the Windows identity you run it under. It stores no credentials.
- Each computer gets a time limit, and a scan returns at most 50,000 events per computer. A partial result is marked as such.

## Status

No license has been chosen yet. Until one is added, the code is public but nobody has been granted permission to reuse it.
