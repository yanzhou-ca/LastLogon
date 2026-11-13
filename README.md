# LastLogon

A Windows tool for support staff. It answers two questions about remote computers:

- **Who used this computer, and when?**
- **Which computers did this user use?**

![By user mode, with made-up data](docs/images/sample-by-user.png)

The screenshot uses made-up computer names, a made-up domain and a made-up user.

## Lookup modes

- **By computer:** every user profile on a computer, with the time of its last profile activity, newest first.
- **By user:** the computers from a list that have a profile for one user. Every computer checked gets a row: a profile found, no profile, or the reason it could not be checked. The status line gives the totals, for example `Done. 6 computers checked: 3 with a profile, 1 without, 2 could not be checked.`
- **Logon history:** logon, logoff, lock, unlock, failed-logon and special-logon events from the remote Security log. You can filter by account and logon type, select an event to see all its fields, and export to CSV.

Access-denied rows also show the address each computer name resolves to. That separates a refused account from a name that reaches a different computer.

In **By user** and **Logon history** you type or paste the computer names, or fill the box with **Load from file…** (a text or CSV file) or **Load from AD…** (a name prefix and an optional OU). Loading replaces what is in the box, and Ctrl+Z in the box brings it back.

Working with the results:

- Select a row to read its full message under the table. Rows that are long are cut off in the table itself, and a tooltip shows the whole of a long computer or user name.
- Click a column heading to sort, and click it again to reverse the order.
- Double-click a profile row, or right-click it, to open the profile folder or copy the computer name.
- **Copy** and Ctrl+C copy the selected rows, or every row when none is selected. **Export CSV** writes every row. Both follow the order the table is sorted in.
- Esc cancels a scan that is running.

## Read the results with care

- **Last profile activity** is the time the user's `NTUSER.DAT` file was last written, which happens at logoff. For someone who is still logged on it shows the previous logoff, not the current time. Use **Logon history** for current activity.
- **A missing record is not proof** that a user never used a computer. Profiles get cleaned up, folders can carry a domain suffix, and event logs are overwritten.

## Requirements

- Windows 10 or 11, 64-bit.
- The **.NET 10 Desktop Runtime** for the framework-dependent download. A computer that has only the .NET 8 runtime cannot start it. Microsoft supports .NET 10 until 14 November 2028. The self-contained build in [docs/publishing.md](docs/publishing.md) avoids needing the runtime.
- Run the tool as an administrator. The account you use needs administrator rights on each target computer.
- For **Logon history**, the target computers need the *Remote Event Log Management* firewall rules enabled, and logon auditing turned on.

## Download

Get the latest build from the [Releases](https://github.com/yanzhou-ca/LastLogon/releases) page. Check the file against the SHA-256 value published with the release.

The executable is not code-signed, so Windows SmartScreen may warn the first time you start it.

## Build from source

You need the .NET 10 SDK on Windows.

```powershell
dotnet restore LastLogon.slnx --use-lock-file
dotnet build LastLogon.slnx -c Release --no-restore
dotnet test LastLogon.Tests/LastLogon.Tests.csproj
dotnet publish LastLogon.csproj -c Release -p:Platform=x64 -r win-x64 --no-restore
```

The published file is written to `publish\win-x64\LastLogon.exe`. [docs/publishing.md](docs/publishing.md) describes the other publish profiles, including a self-contained build that needs no runtime.

## Project layout

- `LastLogon.Core/`: the scanning and filtering logic, with no user interface.
- `ScanWindow.xaml` and `ScanWindow.xaml.cs`: the WPF window. `AdDialog.xaml` is the Active Directory search, and `Styles.xaml` holds the look the two share.
- `LastLogon.Tests/`: the unit tests.
- `docs/`: the publishing guide and the sample screenshot.

## Behaviour and limits

- The tool only reads. It changes nothing on the computers it checks.
- It uses the Windows identity you run it under. It stores no credentials.
- Each computer gets a time limit, and a scan returns at most 50,000 events per computer. A partial result is marked as such.

## Status

Copyright (C) 2026 Yan.

LastLogon is free software, licensed under the [GNU General Public License, version 3 or later](LICENSE). You may use, study, change and share it under those terms. The program comes with no warranty.
