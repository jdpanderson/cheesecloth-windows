# Changelog

All notable changes to the Windows installer are listed here. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). A release
names the cheesecloth version it packages plus a package revision, such as
`0.4.0-2`.

## [Unreleased]

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
