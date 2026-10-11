# Checks a Windows installer on this machine: it installs it, checks what it
# installed, runs the service through its lifecycle, upgrades, reinstalls the
# same version, fixes a failing service and uninstalls. For CI: it needs an
# elevated shell, and it changes the machine. It stops at the first failure.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    # The cheesecloth.exe that the installer holds.
    [Parameter(Mandatory)] [string] $Exe
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$work = Join-Path $root "target\windows\$Arch"
$Setup = (Resolve-Path $Setup).Path
$Exe = (Resolve-Path $Exe).Path
$app = Join-Path $env:ProgramFiles 'cheesecloth'
$cli = Join-Path $app 'cheesecloth.exe'
$state = Join-Path $env:ProgramData 'cheesecloth'
# The AppId in cheesecloth.iss.
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{CFACBA51-554C-488D-B946-84F644BFB8D5}_is1'
$eventLogKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\cheesecloth'
$since = Get-Date

function Check([bool] $ok, [string] $what) {
    if (-not $ok) { throw "check failed: $what" }
    Write-Host "ok: $what"
}

function WaitUntil([string] $what, [scriptblock] $test, [int] $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (& $test)) {
        if ((Get-Date) -gt $deadline) { throw "timed out waiting for $what" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "ok: $what"
}

# Runs the installer silently. It must exit with $expect; else its log is
# printed.
function Install([string] $installer, [string] $name, [string] $more = '', [int] $expect = 0) {
    $log = Join-Path $work "$name.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/LOG=$log`" $more"
    Write-Host "running $installer $arguments"
    $process = Start-Process $installer -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne $expect) {
        if (Test-Path $log) { Get-Content $log | Write-Host }
        throw "$installer exited with $($process.ExitCode), not $expect"
    }
}

# Runs the installed CLI, which must succeed.
function Cli {
    $out = & $cli @args
    if ($LASTEXITCODE -ne 0) { throw "cheesecloth $args failed with exit code $LASTEXITCODE" }
    $out
}

# The daemon's status, or $null while it does not answer.
function Status {
    $out = & $cli --json status 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $out | Out-String | ConvertFrom-Json
}

function TheService {
    Get-CimInstance Win32_Service -Filter "Name = 'cheesecloth'"
}

function Events([int] $level) {
    Get-WinEvent -FilterHashtable @{
        LogName = 'Application'; ProviderName = 'cheesecloth'; Level = $level; StartTime = $since
    } -ErrorAction SilentlyContinue
}

function HasEvent([int] $level, [string] $text) {
    @(Events $level | Where-Object { $_.Message -like "*$text*" }).Count -gt 0
}

# The machine type in a PE file's header.
function Machine([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
}

function UninstallEntries {
    @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { ($_.GetValue('DisplayName') -as [string]) -like 'cheesecloth*' })
}

$version = (& $Exe --version) -replace '^cheesecloth ', ''
if ($LASTEXITCODE -ne 0) { throw "$Exe --version failed" }
New-Item -ItemType Directory -Force $work | Out-Null

function CheckOptions([string] $opts, [string] $what) {
    $command = "`"$cli`" daemon$opts"
    Check ((TheService).PathName -eq $command) "$what ($command)"
}

Write-Host '== Install'
Install $Setup 'install' '/CHEESECLOTH_OPTS="--no-port-mapping --log info"'
CheckOptions ' --no-port-mapping --log info' 'the service has the options'
Check ((Get-Item $Setup).VersionInfo.FileVersionRaw -eq [version]"$version.0") 'the installer has the version of the binary'
foreach ($file in 'cheesecloth.exe', 'wintun.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'docs\WINDOWS.md', 'unins000.exe') {
    Check (Test-Path (Join-Path $app $file)) "$file is installed"
}
$machine = @{ x64 = 0x8664; arm64 = 0xAA64 }[$Arch]
foreach ($file in 'cheesecloth.exe', 'wintun.dll') {
    Check ((Machine (Join-Path $app $file)) -eq $machine) "$file is for $Arch"
}
Check (Select-String -Path (Join-Path $app 'THIRD-PARTY-NOTICES.md') -Pattern '## Wintun' -SimpleMatch -Quiet) 'the notices include Wintun'

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
Check (($machinePath -split ';') -contains $app) 'the machine PATH has the install folder'
$savedPath = $env:Path
try {
    $env:Path = $machinePath
    $found = (Get-Command cheesecloth -CommandType Application | Select-Object -First 1).Source
    & cheesecloth --version
    if ($LASTEXITCODE -ne 0) { throw 'cheesecloth --version failed' }
} finally {
    $env:Path = $savedPath
}
Check ($found -eq $cli) 'cheesecloth runs from the machine PATH'

# The installer's service setup that stopping the service undoes.
function CheckRestarts {
    Check ((TheService).StartMode -eq 'Auto') 'the service starts automatically'
    $failure = sc.exe qfailure cheesecloth | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe qfailure failed' }
    Check ($failure -match 'RESET_PERIOD \(in seconds\)\s*:\s*86400') 'failures are counted for one day'
    Check ([regex]::Matches($failure, 'RESTART -- Delay = 5000 milliseconds').Count -eq 3) 'the service restarts 3 times after 5 s'
    $flag = sc.exe qfailureflag cheesecloth | Out-String
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe qfailureflag failed' }
    Check ($flag -match 'FAILURE_ACTIONS_ON_NONCRASH_FAILURES\s*:\s*TRUE') 'a daemon error restarts the service too'
}

$service = TheService
Check ($service.StartName -eq 'LocalSystem') 'the service runs as LocalSystem'
Check ($service.State -eq 'Running') 'the service runs'
Check ($service.Description -eq 'cheesecloth peer-to-peer WireGuard mesh') 'the service has its description'
CheckRestarts
$preshutdown = Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Services\cheesecloth' 'PreshutdownTimeout'
Check ($preshutdown -eq 30000) 'the pre-shutdown timeout is 30 s'

Check ((Get-ItemPropertyValue $eventLogKey 'EventMessageFile') -eq $cli) 'the Event Log source names the binary'
Check ((Get-ItemPropertyValue $eventLogKey 'TypesSupported') -eq 7) 'the Event Log source has its types'
Check ((Get-ItemPropertyValue $uninstallKey 'DisplayVersion') -eq $version) 'the Uninstall entry has the version'

$acl = Get-Acl $state
$system = 'S-1-5-18'
$admins = 'S-1-5-32-544'
Check ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $system) 'SYSTEM owns the state directory'
Check $acl.AreAccessRulesProtected 'the state directory does not inherit access'
$who = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
    ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique
Check ($null -eq (Compare-Object @($who) @($admins, $system))) "only SYSTEM and Administrators may use the state directory ($who)"

Write-Host '== Lifecycle'
WaitUntil 'the local API' { $null -ne (Status) }
Cli init
WaitUntil 'userspace WireGuard on cheesecloth0' {
    $s = Status
    $s -and $s.wg_backend -eq 'userspace' -and $s.wg_interface -eq 'cheesecloth0'
}
Check (HasEvent 4 'cheesecloth daemon starting') 'the start is in the Event Log'

Stop-Service cheesecloth
$service = TheService
Check ($service.State -eq 'Stopped') 'Stop-Service stops the service'
Check ($service.ExitCode -eq 0) 'the service stopped with exit code 0'
Check (HasEvent 4 'shutting down') 'the shutdown is in the Event Log'
Check (@(Events 2).Count -eq 0) 'the Event Log has no errors'

Start-Service cheesecloth
WaitUntil 'a member after Start-Service' { $s = Status; $s -and $s.phase -eq 'member' }

$old = (TheService).ProcessId
Stop-Process -Id $old -Force
WaitUntil 'a restart after a crash' {
    $s = TheService
    $s.State -eq 'Running' -and $s.ProcessId -ne 0 -and $s.ProcessId -ne $old
} 30

Write-Host '== Upgrade'
$v = [version]$version
$next = "$($v.Major).$($v.Minor).$($v.Build + 1)"
$upgradeDir = Join-Path $work 'upgrade'
& (Join-Path $PSScriptRoot 'build.ps1') -Arch $Arch -Exe $Exe -Version $next -OutDir $upgradeDir
$upgrade = Join-Path $upgradeDir "cheesecloth-$next-windows-$Arch-setup.exe"
$old = (TheService).ProcessId
Install $upgrade 'upgrade'
$entries = UninstallEntries
Check ($entries.Count -eq 1) 'the upgrade replaced the old install'
Check ($entries[0].GetValue('DisplayVersion') -eq $next) 'the Uninstall entry has the new version'
$service = TheService
Check ($service.State -eq 'Running' -and $service.ProcessId -ne $old) 'the upgrade restarted the service'
CheckOptions ' --no-port-mapping --log info' 'the upgrade kept the options'
WaitUntil 'a member after the upgrade' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== Same-version reinstall'
Install $upgrade 'reinstall' '/CHEESECLOTH_OPTS='
Check ((TheService).State -eq 'Running') 'the service runs after a reinstall'
CheckOptions '' 'an empty /CHEESECLOTH_OPTS= cleared the options'
WaitUntil 'a member after the reinstall' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== A failing service'
# The daemon cannot bind to an address that the machine does not have. So the
# service fails, and the SCM restarts it again and again.
# Each failed start logs the bind error once, as it stops, so a second such
# error shows a restart.
Install $upgrade 'failing' '/CHEESECLOTH_OPTS="--bind 192.0.2.1"' 10
WaitUntil 'the SCM restarts the failing service' {
    @(Events 2 | Where-Object { $_.Message -like '*binding 192.0.2.1*' }).Count -ge 2
}
Install $upgrade 'fixed' '/CHEESECLOTH_OPTS=--no-port-mapping'
Check ((TheService).State -eq 'Running') 'a reinstall with good options starts the service'
CheckRestarts
WaitUntil 'a member after the fix' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== Uninstall'
$quiet = Get-ItemPropertyValue $uninstallKey 'QuietUninstallString'
if ($quiet -notmatch '^"([^"]+)"\s*(.*)$') { throw "unexpected QuietUninstallString: $quiet" }
$uninstaller = $Matches[1]
$arguments = "$($Matches[2]) /SUPPRESSMSGBOXES `"/LOG=$(Join-Path $work 'uninstall.log')`""
Write-Host "running $uninstaller $arguments"
$process = Start-Process $uninstaller -ArgumentList $arguments -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "the uninstaller exited with $($process.ExitCode)" }
Check ($null -eq (Get-Service cheesecloth -ErrorAction SilentlyContinue)) 'the service is gone'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
Check (($machinePath -split ';') -notcontains $app) 'the machine PATH no longer has the install folder'
Check (-not (Test-Path $eventLogKey)) 'the Event Log source is gone'
Check (-not (Test-Path $uninstallKey)) 'the Uninstall entry is gone'
Check (-not (Test-Path $app)) 'the install folder is gone'
Check (@(Get-ChildItem $state).Count -gt 0) 'the state directory is kept'
