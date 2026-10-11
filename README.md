# Windows installer

An Inno Setup installer for
[cheesecloth](https://github.com/jdpanderson/cheesecloth) on Windows. It
installs `cheesecloth.exe` with `wintun.dll` into
`C:\Program Files\cheesecloth`, adds that folder to the system PATH, registers
the Event Log source, and sets up and starts the service. See
[docs/WINDOWS.md](https://github.com/jdpanderson/cheesecloth/blob/main/docs/WINDOWS.md)
in cheesecloth for the service itself, its logs, the CLI and the Windows weak
points.

## Install

Pick the installer for your machine from the release, or from the CI run's
**Artifacts**:

- `cheesecloth-<version>-windows-x64-setup.exe` for x64 (Windows 10 or 11);
- `cheesecloth-<version>-windows-arm64-setup.exe` for Arm64 (Windows 11 only:
  the installer itself is an x64 program, and only Windows 11 runs x64
  programs on Arm64).

The wrong one stops at once with a message. Run the installer and follow the
wizard. It installs into `C:\Program Files\cheesecloth`, then starts the
service. Shells opened before the install do not see the new PATH.

For a silent install, run it from an elevated shell:

```powershell
.\cheesecloth-0.3.0-windows-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=setup.log
```

`/CHEESECLOTH_OPTS="..."` sets the service's options; see
[Changing options](#changing-options). The installer's exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Installed, and the service runs. |
| 7 | The service could not be stopped, or `cheesecloth.exe` is in use (see [Weak points](#weak-points)). No files have changed. |
| 10 | The files are installed, but the service could not be set up or did not start. The log and the Event Log say why. |

Other codes are [Inno Setup's](https://jrsoftware.org/ishelp/topic_setupexitcodes.htm).
Any install or uninstall that fails or is cancelled can leave the service
stopped and disabled until the next install that succeeds.

A newer installer upgrades in place, and running the same version again
reinstalls it. Downgrades are not supported: an older daemon may not read the
state files of a newer one.

The installer also sets the service's restart on failure (after 5 seconds)
and gives the daemon 30 seconds to stop at system shutdown.

## Changing options

The service runs `cheesecloth daemon` with the options from the installer. To
change them, run the installer again with new options, or change them on its
options page:

```powershell
.\cheesecloth-0.3.0-windows-x64-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /CHEESECLOTH_OPTS="--no-port-mapping --log debug"
```

An upgrade without `/CHEESECLOTH_OPTS` keeps the options. `/CHEESECLOTH_OPTS=`
with nothing after it clears them. Test new options in a console first, with
the service stopped. Edits with `sc config` or in the registry are lost on the
next upgrade.

## Uninstall

Use Settings > Apps, or run from an elevated shell:

```powershell
& "C:\Program Files\cheesecloth\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

It stops and removes the service, the PATH entry and the Event Log source. It
keeps the state directory, `C:\ProgramData\cheesecloth`, with the node's keys
and cluster state; delete it to forget the node. Leave the cluster first with
`cheesecloth leave` if the node should not come back.

## Weak points

- **No rollback of the service setup.** If the service cannot be set up or
  does not start, Setup keeps the installed files and exits with code 10.
  Fix the cause and run the installer again.
- **Spaces in one option.** The installer's command-line parsing removes
  double quotes, so `/CHEESECLOTH_OPTS` cannot hold a value with a space
  inside one option. Use the options page, or a form without spaces, such as
  `--name=office-pc`.
- **A service being removed.** While a program such as Services or Event
  Viewer holds the service open, an uninstall marks it for deletion, and
  Windows removes it only when that program closes it. An install in that
  time fails with error 1072.
- **Event Viewer blocks an upgrade.** Windows reads the text of cheesecloth's
  events from `cheesecloth.exe`. While a program shows those events, such as
  Event Viewer or `Get-WinEvent` in an open PowerShell, it holds the file,
  and Setup cannot replace it. Setup waits up to 5 seconds for the file to
  be free, then asks you to close the program and click Retry. A silent
  install with `/SUPPRESSMSGBOXES` stops instead, with exit code 7, before it
  changes any file. The service stays stopped and disabled until an install
  succeeds.
- **Old events lose their text** after an uninstall, because the Event Log
  source is removed.
- **No MSI.** Group Policy and Intune MSI deployment are not possible. Run the
  silent install from a script, or use Intune's Win32 app type.

## Building and releasing

In CI (`.github/workflows/ci.yml`), the `binary` job gets the pinned
`cheesecloth.exe` (see [the pin file](#the-pin-file)). The `installer` job
then builds and checks the installers: `build.ps1` builds one, and
`check.ps1` installs, upgrades, reinstalls and uninstalls it on the runner.

To build one by hand, with Inno Setup on PATH and a cheesecloth checkout
beside this one, at the commit or tag of the exe:

```powershell
.\build.ps1 -Arch x64 -Exe ..\cheesecloth\target\release\cheesecloth.exe -Source ..\cheesecloth
```

### The pin file

`cheesecloth.pin` names what the installer packages. It is TOML, in the
format that "Contracts" in cheesecloth's
[docs/REPOSITORIES.md](https://github.com/jdpanderson/cheesecloth/blob/main/docs/REPOSITORIES.md#contracts)
defines. It has two forms.

A release of cheesecloth, with the SHA-256 of each Windows binary. Copy them
from the release's `SHA256SUMS`:

```toml
release = "0.4.0"
[sha256]
x86_64-pc-windows-msvc = "<sha-256>"
aarch64-pc-windows-msvc = "<sha-256>"
```

CI downloads `cheesecloth-<release>-<target>.exe` from the release and fails
if its SHA-256 differs from the pin. The release does not hold the README,
LICENSE, notices and docs that the installer ships, so CI takes them from
the tag `v<release>` of cheesecloth. It builds no Rust.

A commit of cheesecloth, for work that spans both repositories:

```toml
commit = "<40-character commit id>"
```

CI checks out cheesecloth at that commit, builds the binary from source, and
takes the README, LICENSE, notices and docs from the same checkout. Change
cheesecloth first and merge it, then pin that commit here. Before a package
release, the pin must name a release again.

### Releasing

A package release names the cheesecloth release it packages plus a package
revision, such as `0.4.0-1` (the revision starts at 1 for each cheesecloth
release). It is published on this repository's releases.

1. Pin the cheesecloth release in `cheesecloth.pin`, and merge it to `main`.
2. Run the installer from the last CI run of `main` once on a Windows
   machine with its wizard: check the pages, the options page, the Retry box
   while Event Viewer shows cheesecloth's events, and that the service runs.
3. Add a heading for the package version to CHANGELOG.md, and merge it.
4. Tag `main` and push the tag:
   `git tag v0.4.0-1 && git push origin v0.4.0-1`.

For the tag, CI checks that it is `v<pinned release>-<revision>`, runs all
the checks, and the `release` job attaches
`cheesecloth-<package version>-windows-<arch>-setup.exe` for each
architecture and a `SHA256SUMS` file. A pinned release with a suffix, such as
`0.4.0pre0`, makes a pre-release. While signing is off, the release is
unsigned, and its notes say so. While signing is on, the release waits for
the signed installers and their checks; if signing fails, nothing is
published.

The installer's own version is the binary's version, such as `0.4.0`, since
Windows versions hold only numbers. So installing `0.4.0-1` over a
`0.4.0pre0` installer is a same-version reinstall.

### Tools

- **Wintun.** `WintunVersion` and `WintunSha256` at the top of `build.ps1`.
  Take the zip from <https://www.wintun.net/>, check its SHA-256 there, and
  check that its `LICENSE.txt` still allows shipping `wintun.dll` with
  cheesecloth.
- **Inno Setup.** `INNO_SETUP_VERSION` and `INNO_SETUP_SHA256` in
  `.github/actions/inno-setup/action.yml`. The SHA-256 is the digest of the
  `innosetup-<version>-x64.exe` asset on the GitHub release of
  `jrsoftware/issrc`. If the script needs a newer version, raise the
  `#if Ver < EncodeVer(...)` line at the top of `cheesecloth.iss` too.

### Signing

The `sign` job signs `cheesecloth.exe`, the uninstaller and the installer.
It runs on a fresh runner after the `installer` job passes, because that job
ran our binary as a service, so its runner is not trusted with the key. The
`sign` job never runs cargo or our binary. Then the `check-signed` job runs
`check.ps1` on the signed installer, on another fresh runner without the
secrets, so the installer that ships is the one that was checked. Its
artifact is `cheesecloth-windows-<arch>-signed`.

To set it up:

1. Create the environment `signing` (Settings > Environments), and limit its
   deployment branches and tags to `main` and `v*`.
2. Add two secrets to that environment:
   - `WINDOWS_SIGNING_CERT`: the code signing certificate and its private
     key, as a PFX file in base64.
   - `WINDOWS_SIGNING_PASSWORD`: the PFX file's password.
3. Add the repository variable `WINDOWS_SIGNING` with the value `true`.

Only jobs that name the environment can read its secrets, and only on
`main` and release tags, so code from a pull request never runs with the
key.

`sign.ps1` imports the certificate into the runner's certificate store,
rebuilds the installer with Inno Setup's Sign Tool
(`signtool sign /sha1 <thumbprint> /fd SHA256 /tr <timestamp server>`), checks
that both signatures are valid, and removes the certificate. A rebuild is
needed because Inno signs the uninstaller while it compiles.

A certificate on a hardware key or in a cloud signing service needs only a
different signing command in `sign.ps1`.

Without the variable, the `sign` and `check-signed` jobs are skipped, and the
installers and releases are unsigned. SmartScreen then warns when a user
runs one.
