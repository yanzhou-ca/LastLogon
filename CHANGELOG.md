# Changelog

All notable changes to LastLogon are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

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
