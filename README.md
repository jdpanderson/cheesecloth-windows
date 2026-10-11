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
| 7 | The service could not be stopped. No files have changed. |
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
- **Old events lose their text** after an uninstall, because the Event Log
  source is removed.
- **No MSI.** Group Policy and Intune MSI deployment are not possible. Run the
  silent install from a script, or use Intune's Win32 app type.

## Building and releasing

CI (`.github/workflows/ci.yml`) builds `cheesecloth.exe` at the pinned
commit in the `binary` job. The `installer` job then builds and checks the
installers: `build.ps1` builds one, and `check.ps1` installs, upgrades,
reinstalls and uninstalls it on the runner.

To build one by hand, with Inno Setup on PATH and a cheesecloth checkout
beside this one, at the commit that built the exe:

```powershell
.\build.ps1 -Arch x64 -Exe ..\cheesecloth\target\release\cheesecloth.exe -Source ..\cheesecloth
```

### The pin file

`cheesecloth.json` names the cheesecloth commit that the installer packages:

```json
{ "commit": "<full commit SHA-1>" }
```

CI checks out cheesecloth at that commit, builds the binary from source, and
takes the README, LICENSE, notices and docs from the same checkout.

When cheesecloth releases attach a binary for each target, the pin will name
a release and the SHA-256 of each binary in place of the commit, and CI will
download and check them:

```json
{ "release": "v0.4.0", "sha256": { "x64": "<sha256>", "arm64": "<sha256>" } }
```

CI does not read this form yet. Before a package release, the pin must name a
release. For work that spans both repositories, change cheesecloth first and
merge it, then pin that commit here.

Before a release, run the installer from the last CI run of `main` once on a
Windows machine with its wizard: check the pages, the options page, and that
the service runs. A pre-release installer has the version of the final
release, so installing the final release over it is a normal same-version
reinstall.

### Tools

- **Wintun.** `WintunVersion` and `WintunSha256` at the top of `build.ps1`.
  Take the zip from <https://www.wintun.net/>, check its SHA-256 there, and
  check that its `LICENSE.txt` still allows shipping `wintun.dll` with
  cheesecloth.
- **Inno Setup.** `INNO_SETUP_VERSION` and `INNO_SETUP_SHA256` in the
  `installer` job of `ci.yml`. The SHA-256 is the digest of the
  `innosetup-<version>-x64.exe` asset on the GitHub release of
  `jrsoftware/issrc`. If the script needs a newer version, raise the
  `#if Ver < EncodeVer(...)` line at the top of `cheesecloth.iss` too.

### Signing

The `Sign` step of the `installer` job signs `cheesecloth.exe`, the
uninstaller and the installer. It needs two repository secrets:

- `WINDOWS_SIGNING_CERT`: the code signing certificate and its private key,
  as a PFX file in base64.
- `WINDOWS_SIGNING_PASSWORD`: the PFX file's password.

`sign.ps1` imports the certificate into the runner's certificate store,
rebuilds the installer with Inno Setup's Sign Tool
(`signtool sign /sha1 <thumbprint> /fd SHA256 /tr <timestamp server>`), checks
that both signatures are valid, and removes the certificate. The step runs
after the installer checks, so no secret is present while cargo or our binary
runs.

A certificate on a hardware key or in a cloud signing service needs only a
different signing command in `sign.ps1`.

Without the secrets the step is skipped, and the installers are unsigned.
SmartScreen then warns when a user runs one.
