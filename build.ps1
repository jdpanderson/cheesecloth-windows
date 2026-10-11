# Builds the Windows installer from a built cheesecloth.exe:
# <OutDir>\cheesecloth-<version>-windows-<arch>-setup.exe. Needs Inno Setup
# 7.1 or later, with ISCC.exe on PATH. The version comes from the binary.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    [Parameter(Mandatory)] [string] $Exe,
    # Only for tests that need another version, such as an upgrade.
    [string] $Version,
    [string] $OutDir = 'dist'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The Wintun release in the installer. See RELEASING.md to update it.
$WintunVersion = '0.14.1'
$WintunSha256 = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

if (-not $Version) {
    # "cheesecloth 0.3.0"
    $Version = (& $Exe --version) -replace '^cheesecloth ', ''
    if ($LASTEXITCODE -ne 0) { throw "$Exe --version failed" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "unexpected version '$Version'" }

# Stage the files.
$work = Join-Path $root 'target\windows'
$stage = Join-Path $work "$Arch\stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'docs') | Out-Null
Copy-Item $Exe (Join-Path $stage 'cheesecloth.exe')
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $stage
Copy-Item (Join-Path $root 'docs\*.md') (Join-Path $stage 'docs')

# Wintun, from its official release. A cached copy is used when its hash
# matches.
$zip = Join-Path $work "wintun-$WintunVersion.zip"
$cached = (Test-Path $zip) -and (Get-FileHash $zip -Algorithm SHA256).Hash -eq $WintunSha256
if (-not $cached) {
    Invoke-WebRequest "https://www.wintun.net/builds/wintun-$WintunVersion.zip" -OutFile $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $WintunSha256) {
        Remove-Item $zip
        throw "wintun-$WintunVersion.zip has SHA-256 $hash, not $WintunSha256"
    }
}
$wintun = Join-Path $work "wintun-$WintunVersion"
if (Test-Path $wintun) { Remove-Item -Recurse -Force $wintun }
Expand-Archive $zip -DestinationPath $wintun
$dllArch = @{ x64 = 'amd64'; arm64 = 'arm64' }[$Arch]
Copy-Item (Join-Path $wintun "wintun\bin\$dllArch\wintun.dll") $stage

# The notices: the crates' licenses, then Wintun's.
$crates = (Get-Content -Raw (Join-Path $root 'THIRD-PARTY-NOTICES.md')).TrimEnd()
$license = (Get-Content -Raw (Join-Path $wintun 'wintun\LICENSE.txt')).TrimEnd()
$notices = @"
$crates

## Wintun

``wintun.dll`` is Wintun $WintunVersion, from
https://www.wintun.net/builds/wintun-$WintunVersion.zip (SHA-256
$WintunSha256). It is not modified, and cheesecloth uses it only through
the API in wintun.h. Section 3(d) of its license allows distribution
"alongside other software that uses the Software only via the Permitted API".

``````text
$license
``````

"@
Set-Content -Path (Join-Path $stage 'THIRD-PARTY-NOTICES.md') -Value $notices -Encoding utf8NoBOM -NoNewline

$iscc = @(
    '/Q', "/DVersion=$Version", "/DArch=$Arch", "/DStage=$stage", "/O$OutDir",
    (Join-Path $PSScriptRoot 'cheesecloth.iss')
)
& ISCC.exe @iscc
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$setup = Join-Path $OutDir "cheesecloth-$Version-windows-$Arch-setup.exe"
if (-not (Test-Path $setup)) { throw "$setup is missing" }
Write-Host "Built $setup"
