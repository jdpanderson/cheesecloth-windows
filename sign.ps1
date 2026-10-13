# Signs the Windows installer: it rebuilds dist\cheesecloth-<v>-windows-<arch>-setup.exe
# with Inno's SignTool, which signs cheesecloth.exe, the uninstaller and the
# installer. WINDOWS_SIGNING_CERT (a base64 PFX file) and
# WINDOWS_SIGNING_PASSWORD come from the environment. It never runs
# cheesecloth.exe. CI runs it in its own job, on a runner where neither cargo
# nor our binary has run.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'It reports progress to the console and the CI log.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingConvertToSecureStringWithPlainText', '',
    Justification = 'WINDOWS_SIGNING_PASSWORD comes from a CI secret as plain text, and Import-PfxCertificate needs a SecureString.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    [Parameter(Mandatory)] [string] $Exe,
    # The cheesecloth checkout that build.ps1 took.
    [Parameter(Mandatory)] [string] $Source
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $env:WINDOWS_SIGNING_CERT -or -not $env:WINDOWS_SIGNING_PASSWORD) {
    throw 'WINDOWS_SIGNING_CERT and WINDOWS_SIGNING_PASSWORD must be set'
}

# The version of the unsigned installer that build.ps1 made, read without
# running the binary.
$unsigned = @(Get-Item (Join-Path $PSScriptRoot "dist\cheesecloth-*-windows-$Arch-setup.exe"))
if ($unsigned.Count -ne 1) { throw "expected one unsigned installer in dist, found $($unsigned.Count)" }
$v = $unsigned[0].VersionInfo.FileVersionRaw
$version = "$($v.Major).$($v.Minor).$($v.Build)"

# The newest signtool for this machine.
$hostArch = @{ AMD64 = 'x64'; ARM64 = 'arm64' }[$env:PROCESSOR_ARCHITECTURE]
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\$hostArch\signtool.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $signtool) { throw "no signtool.exe for $hostArch" }

$pfx = Join-Path ([IO.Path]::GetTempPath()) 'cheesecloth-signing.pfx'
$imported = @()
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERT))
    $password = ConvertTo-SecureString $env:WINDOWS_SIGNING_PASSWORD -AsPlainText -Force
    $imported = @(Import-PfxCertificate -FilePath $pfx -CertStoreLocation Cert:\CurrentUser\My -Password $password)
    Remove-Item $pfx
    $cert = @($imported | Where-Object HasPrivateKey)
    if ($cert.Count -ne 1) { throw "expected one certificate with a private key, found $($cert.Count)" }

    # The command holds the thumbprint, not the password, so ISCC's output
    # shows no secret. Inno replaces $q with a quote and $f with the file.
    $command = "`$q$($signtool.FullName)`$q sign /sha1 $($cert[0].Thumbprint) /fd SHA256 " +
        '/tr http://timestamp.digicert.com /td SHA256 $f'
    & (Join-Path $PSScriptRoot 'build.ps1') -Arch $Arch -Exe $Exe -Source $Source -Version $version -OutDir (Join-Path $PSScriptRoot 'dist') -SignTool $command

    $setup = Join-Path $PSScriptRoot "dist\cheesecloth-$version-windows-$Arch-setup.exe"
    & (Join-Path $PSScriptRoot 'verify.ps1') -File (Join-Path $PSScriptRoot "target\windows\$Arch\stage\cheesecloth.exe"), (Join-Path $PSScriptRoot "target\windows\$Arch\stage\cheesecloth-tray.exe"), $setup
} finally {
    if (Test-Path $pfx) { Remove-Item $pfx }
    foreach ($c in $imported) {
        Remove-Item "Cert:\CurrentUser\My\$($c.Thumbprint)" -DeleteKey:$c.HasPrivateKey
    }
}
