# Changelog

All notable changes to the Windows installer are listed here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). A release
names the cheesecloth version it packages plus a package revision, such as
`0.4.0-2`.

## [Unreleased]

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
  checks the signed installer on another. It runs on `main` once signing is
  set up; without it the installers are unsigned.
- `cheesecloth.pin`, the cheesecloth commit that the installer packages. CI
  builds the binary from source at that commit.
