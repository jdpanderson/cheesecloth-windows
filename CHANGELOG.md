# Changelog

All notable changes to the Windows installer are listed here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). A release
names the cheesecloth version it packages plus a package revision, such as
`0.4.0-2`.

## [Unreleased]

## [0.5.0-beta.4-2] - 2026-10-22

### Changed

- The tray app's Peers table looks like the GNOME app's. It shows roles and
  permissions as icons, the path state as a colored dot, and the actions as
  a flat button, each with a tooltip. It has a Present column, and light
  gray lines between rows only.

### Fixed

- The Peers table fills the window and resizes with it.

## [0.5.0-beta.4-1] - 2026-10-22

### Added

- The installer checks run the CLI as an administrator who is not elevated,
  against the service, as the tray app does.

### Fixed

- The tray app works for an administrator without elevation. 0.5.0-beta.3-1
  did not fully fix this: the CLI found the daemon's pipe, but still failed
  with "Access is denied" when it checked who serves it. The installer
  packages cheesecloth 0.5.0-beta.4, which checks the pipe's owner instead
  of the daemon's process.

## [0.5.0-beta.3-1] - 2026-10-21

### Fixed

- The tray app works for an administrator without elevation. It showed "Not
  responding" with "Access is denied", because the CLI could not find the
  daemon's pipe. The installer packages cheesecloth 0.5.0-beta.3, which finds
  the pipe without opening the state directory.
- The tray menu opened with a left click now closes when you click outside
  it, as the right-click menu does.

## [0.5.0-beta.2-2] - 2026-10-21

### Fixed

- The tray menu no longer fails when the daemon's status has no warnings
  list.

## [0.5.0-beta.2-1] - 2026-10-21

### Fixed

- The service starts on a Windows install without the Visual C++
  Redistributable. The installer packages cheesecloth 0.5.0-beta.2, whose
  `cheesecloth.exe` no longer needs `VCRUNTIME140.dll`. Before, Setup said
  the service "did not respond to the start or control request in a timely
  fashion", and the tray app's commands failed.
- The tray app no longer opens an empty "Cheesecloth tray" window when it
  starts.

### Changed

- Setup and the uninstaller close the tray app by setting an event that it
  waits for, instead of with Restart Manager or through a window. If the
  tray app is open in another user's session, they stop before they change
  anything and ask to close it there.
- An older tray app does not know this event. To upgrade from an earlier
  release, close the tray app first ("Close Tray Application" in its menu),
  or Setup asks you to.

## [0.5.0-beta.1-1] - 2026-10-15

### Added

- Member permissions in the tray. The Invite window has a checkbox for each
  permission, Admin, Voter and Relay, all on by default. The Status window
  and join approvals show the permissions, and the Peers window has a
  Permissions column: "All", a list such as "Voter, Relay", or "Member".

### Changed

- The installer packages cheesecloth 0.5.0-beta.1, which adds member
  permissions. Its local API and agreed state differ from 0.5.0-beta.0:
  rebuild existing networks.
- On a member without the admin permission, the tray has no Invite button,
  no Remove item for peers and no Approvals item in the menu.

### Security

- The installer installs only into `C:\Program Files\cheesecloth`, and
  refuses `/DIR=` with another folder (exit code 7). Another folder keeps the
  access of its parent, so under `C:\`, for example, other users could change
  `cheesecloth.exe`, which the service runs as LocalSystem.

## [0.5.0-beta.0-1] - 2026-10-13

The first test release of the tray app, with cheesecloth 0.5.0-beta.0.

### Changed

- The installer accepts pre-release versions. Its file version uses the
  numeric part, while the installer name and installed version keep the full
  release name.

## [0.4.0-2] - 2026-10-11

The installer for cheesecloth 0.4.0, signed with a self-signed certificate.

### Added

- The installers are signed with a self-signed certificate, `signing.cer`.
  The README says how to check an installer's signature against it.
  SmartScreen still warns.
- `verify.ps1` checks that a file is signed with `signing.cer`. CI runs it on
  the signed installers.

## [0.4.0-1] - 2026-10-11

The installer for cheesecloth 0.4.0. Unsigned.

### Changed

- The installer packages cheesecloth 0.4.0.
- A full release is published unsigned while signing is not set up, as a
  pre-release is. While signing is on, a failed signing still publishes
  nothing.

## [0.4.0pre0-1] - 2026-10-11

A test pre-release of the installer, for cheesecloth 0.4.0pre0. Unsigned.

### Added

- A Windows installer for x64 and arm64. It installs cheesecloth with
  `wintun.dll`, adds it to the system PATH, and sets up and starts the
  service. CI checks it by installing, upgrading and uninstalling it.
- When `cheesecloth.exe` is in use, for example by Event Viewer showing
  cheesecloth's events, Setup waits up to 5 seconds, then asks the user to
  close the program and retry. A silent install stops with exit code 7,
  before it changes any file.
- The installer takes the service's options from `/CHEESECLOTH_OPTS="..."`
  or its options page, and keeps them across upgrades.
- A CI job that signs the binary and the installer on a fresh runner, then
  checks the signed installer on another. It runs on `main` and release tags
  once signing is set up; without it the installers are unsigned.
- `cheesecloth.pin`, the cheesecloth release that the installer packages,
  with the SHA-256 of each binary. CI downloads the binaries and checks them.
  For work that spans repositories it may name a commit, and CI builds from
  source.
- Package releases: a tag `v<release>-<revision>` publishes the installers
  and a `SHA256SUMS` file on this repository's releases.
