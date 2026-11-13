# Changelog

All notable changes to LastLogon are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed

- The label of the **Scan** button nearly vanished while the pointer was over it or it was pressed (white on pale blue, 1.3:1). The button now shades the system highlight colour, so the text stays readable in every state.
- **Logon history** was cut off at the default window size: the selected event and the state of each computer were pushed below the bottom of the window. The window can no longer be made smaller than its content needs, and both panes show their rows at the default size.
- By user at the smallest window size left room for about one row. Every lookup now keeps at least five rows visible.
- A missing or invalid computer name, a missing username and a bad time range are shown beside their field, in Logon history as well, and the cursor moves there. The message no longer repeats in the status line, no longer stays behind after the input is fixed, and nothing on the window moves when it appears.
- The "stale after" box could show a number that the table was not using. It accepts digits only, marks a value it cannot use, and puts the number in use back when it loses focus.
- Text was cut off with no way to read it. The last column of each table now takes the width that is left, long messages wrap in the two panes of Logon history, and the full message of a selected profile row is shown under the table. Logon history fits its eight columns without scrolling sideways.
- A computer or user name too long for its column was cut off with nothing to show it. It now ends in an ellipsis, and a tooltip shows the whole name. A long tooltip wraps instead of running off the screen.
- Rows of one lookup no longer appear under another. By computer and By user keep their own rows and their own status line.
- The progress bar pushed the "Stale after" box aside when a scan started. It now sits beside the status line, which says how far the scan has got.

### Changed

- **LastLogon now needs the .NET 10 Desktop Runtime**, where it needed .NET 8 before. A computer that has only the .NET 8 runtime can no longer start it, so install .NET 10 there first. The self-contained build needs no runtime. Microsoft ended support for .NET 8 on 10 November 2026. Building from source needs the .NET 10 SDK.
- **Scan** and **Cancel** moved to the top right, beside the choice of lookup, so that they stay in the same place in every lookup. Esc cancels a running scan.
- The cursor starts in the first input. In the Tab order the inputs come first and the dropdown that sets how a computer is addressed comes after them. The Active Directory dialog opens with its first field ready.
- The table says "NTUSER.DAT", "OK", "Access denied" and "No profile" instead of the names of internal values. A row whose time came only from the folder says "Approximate" instead of "OK".
- A stale row is also marked "Yes" in its own **Stale** column, so it does not depend on seeing a colour. In a High Contrast theme the notes, errors, borders, buttons and table highlights use the system colours.
- Counts read "1 computer" and "2 computers" instead of "computer(s)", and the status line of a running scan reads "Scanning… 3 of 12 computers". The status line is no longer small grey text.
- Failure messages in Logon history say what is missing: the permission to read the Security log, or the *Remote Event Log Management* firewall rules.
- A computer name that is not valid is named in the message.
- Dropdowns match the text boxes beside them: white, the same border colours and the same height. The dropdown that sets how a computer is addressed no longer sits against the edge of the computer box. It moved to the line under the box that says which account the scan runs as, with an "Address as" caption.
- The window opens taller.
- Buttons, text boxes, dropdowns, tables and groups share one flat look. Every control is 28 units high, every button is the same width, and they all use one outline colour. The grey gradient buttons, the black grid lines of Logon history and the stock blue rows are gone. A row under the pointer or selected is light blue in both tables, and grey while the table does not have the keyboard.
- Column headings start where the text of their column starts instead of being centred over it, and the numbers (Days ago, Event ID) are right-aligned. The sort arrow is a small grey triangle after the heading.
- Spacing follows a 4-unit grid, with the same margin on every side of the window. The window title is always "LastLogon" instead of changing with the lookup. The choices of lookup say "computer" instead of "PC" and keep their explanation in grey. "Stale after (days)" reads "Mark stale after (days)".
- Before a scan, the empty table says what it will hold for the lookup you are in, and so do the two panes under the events of Logon history.
- **Import .txt…** is now **Load from file…** and **From AD…** is **Load from AD…**. Both are in Logon history as well as By user. They replace the list in the box, and Ctrl+Z in the box brings the old list back. A file with no names, or an empty answer from the directory, leaves the list alone. A name that cannot be used is shown beside the box as soon as the list is loaded, and the status line says how many computers came in and from where. A directory search can be cancelled from any lookup.
- The Active Directory dialog has the same look and **Load** as its main button. It checks the form of the OU name before it closes, so a mistake is shown beside the box instead of in the status line of the window behind it.

### Added

- Click a column heading of the profile table to sort it.
- Ctrl+C in the profile table, a right-click menu (copy rows, copy the computer name, open the profile folder) and a double-click that opens the profile folder.
- Copy and Export CSV say what they did and follow the sort order of the table. They are off when there is nothing to copy.
- A note under the inputs of By computer and By user that says what "last profile activity" is.
- A hint under the computer box that says what to type while the box is empty.
- An icon for the program, in the title bars, the taskbar and the file.

## [1.0.1] - 2026-11-13

### Removed

- The **Test connection** button and its status line. The connection check still runs automatically when an access-denied row needs to show which address each name resolves to.

### Added

- `LICENSE`: GNU General Public License version 3 or later. The 1.0.0 download did not include it.
- `README.md`, with a sample screenshot, the lookup modes, requirements, download and build instructions.

## [1.0.0] - 2026-11-12

First release.

### Added

- **By computer**: every user profile on a PC with its last profile activity, newest first.
- **By user**: the PCs from a list that have a profile for one user. Every computer checked gets a row, and the status line gives the totals.
- **Logon history**: logon, logoff, lock, unlock, failed-logon and special-logon events from the remote Security log, with account and logon-type filters, event details and CSV export.
- Access-denied rows show the address each computer name resolves to, so a refused account can be told apart from a name that reaches a different computer.
- Copy, CSV export and Open folder for profile rows, and stale-profile highlighting.
- An AD-based computer list and import from a text file.

### Fixed

- Pressing Enter during a running scan no longer starts a second scan, and Cancel reaches the scan that is running.
- One unreadable profile folder no longer hides the other profiles on the same computer.
