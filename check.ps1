# Checks a Windows installer on this machine: it installs it, checks what it
# installed, runs the service through its lifecycle, upgrades, reinstalls the
# same version, fixes a failing service and uninstalls. For CI: it needs an
# elevated shell, and it changes the machine. It stops at the first failure.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '',
    Justification = 'It reports progress to the console and the CI log.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [ValidateSet('x64', 'arm64')] [string] $Arch,
    # The cheesecloth.exe that the installer holds.
    [Parameter(Mandatory)] [string] $Exe,
    # The cheesecloth checkout that build.ps1 took, for the upgrade.
    [Parameter(Mandatory)] [string] $Source
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$work = Join-Path $PSScriptRoot "target\windows\$Arch"
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

# Runs the installer silently. $during runs while it runs, with the path of
# its log. It must exit with $expect; else its log is printed, with the
# cheesecloth processes that still run, which may hold files that Setup could
# not replace.
function Install([string] $installer, [string] $name, [string] $more = '', [int] $expect = 0,
    [scriptblock] $during = {}) {
    $log = Join-Path $work "$name.log"
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART `"/LOG=$log`" $more"
    Write-Host "running $installer $arguments"
    $process = Start-Process $installer -ArgumentList $arguments -PassThru
    # Without an open handle, the exit code is lost when the process exits.
    $null = $process.Handle
    & $during $log
    $process.WaitForExit()
    if ($process.ExitCode -ne $expect) {
        if (Test-Path $log) { Get-Content $log | Write-Host }
        $running = @(Get-Process cheesecloth -ErrorAction SilentlyContinue)
        Write-Host "cheesecloth processes: $($running.Count)"
        $running | Format-Table Id, StartTime, Path -AutoSize | Out-String | Write-Host
        $service = TheService
        if ($service) {
            Write-Host "the service is $($service.State), process $($service.ProcessId)"
        } else {
            Write-Host 'the service does not exist'
        }
        throw "$installer exited with $($process.ExitCode), not $expect"
    }
}

# Runs the installed CLI, which must succeed. Not named Cli: that is an alias
# of Clear-Item, and an alias comes before a function.
function RunCli {
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

# The messages of cheesecloth's events at $level since the start, one string
# for each event ('' if it has no message). A child process reads them:
# reading a message loads cheesecloth.exe, the source's message file, and the
# reader holds it for a while, so Setup could not replace it.
function EventMessages([int] $level) {
    pwsh -NoProfile -NonInteractive -Command {
        param([int] $level, [datetime] $since)
        # No events is an error for Get-WinEvent; any other error fails.
        try {
            Get-WinEvent -FilterHashtable @{
                LogName = 'Application'; ProviderName = 'cheesecloth'; Level = $level; StartTime = $since
            } -ErrorAction Stop | ForEach-Object { [string] $_.Message }
        } catch {
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound,*') { throw }
        }
    } -args $level, $since
    if ($LASTEXITCODE -ne 0) { throw "reading the Event Log failed with exit code $LASTEXITCODE" }
}

function HasEvent([int] $level, [string] $text) {
    @(EventMessages $level | Where-Object { $_ -like "*$text*" }).Count -gt 0
}

# The machine type in a PE file's header.
function Machine([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3C) + 4)
}

# PowerShell unrolls what a function returns, so wrap the call in @() to
# count the entries.
function UninstallEntries {
    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { ($_.GetValue('DisplayName') -as [string]) -like 'cheesecloth*' }
}

$version = (& $Exe --version) -replace '^cheesecloth ', ''
if ($LASTEXITCODE -ne 0) { throw "$Exe --version failed" }
New-Item -ItemType Directory -Force $work | Out-Null

function CheckOptions([string] $opts, [string] $what) {
    $command = "`"$cli`" daemon$opts"
    Check ((TheService).PathName -eq $command) "$what ($command)"
}

Write-Host '== Install'
Install $Setup 'install' -More '/CHEESECLOTH_OPTS="--no-port-mapping --log info"'
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
RunCli init
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
Check (@(EventMessages 2).Count -eq 0) 'the Event Log has no errors'

Start-Service cheesecloth
WaitUntil 'a member after Start-Service' { $s = Status; $s -and $s.phase -eq 'member' }

$old = (TheService).ProcessId
Stop-Process -Id $old -Force
WaitUntil 'a restart after a crash' {
    $s = TheService
    $s.State -eq 'Running' -and $s.ProcessId -ne 0 -and $s.ProcessId -ne $old
} -Seconds 30

Write-Host '== Upgrade'
$v = [version]$version
$next = "$($v.Major).$($v.Minor).$($v.Build + 1)"
$upgradeDir = Join-Path $work 'upgrade'
& (Join-Path $PSScriptRoot 'build.ps1') -Arch $Arch -Exe $Exe -Source $Source -Version $next -OutDir $upgradeDir
$upgrade = Join-Path $upgradeDir "cheesecloth-$next-windows-$Arch-setup.exe"
$old = (TheService).ProcessId
Install $upgrade 'upgrade'
$entries = @(UninstallEntries)
Check ($entries.Count -eq 1) 'the upgrade replaced the old install'
Check ($entries[0].GetValue('DisplayVersion') -eq $next) 'the Uninstall entry has the new version'
$service = TheService
Check ($service.State -eq 'Running' -and $service.ProcessId -ne $old) 'the upgrade restarted the service'
CheckOptions ' --no-port-mapping --log info' 'the upgrade kept the options'
WaitUntil 'a member after the upgrade' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== Same-version reinstall'
Install $upgrade 'reinstall' -More '/CHEESECLOTH_OPTS='
Check ((TheService).State -eq 'Running') 'the service runs after a reinstall'
CheckOptions '' 'an empty /CHEESECLOTH_OPTS= cleared the options'
WaitUntil 'a member after the reinstall' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== A failing service'
# The daemon cannot bind to an address that the machine does not have. So the
# service fails, and the SCM restarts it again and again.
# Each failed start logs the bind error once, as it stops, so a second such
# error shows a restart.
Install $upgrade 'failing' -More '/CHEESECLOTH_OPTS="--bind 192.0.2.1"' -Expect 10
WaitUntil 'the SCM restarts the failing service' {
    @(EventMessages 2 | Where-Object { $_ -like '*binding 192.0.2.1*' }).Count -ge 2
}
Install $upgrade 'fixed' -More '/CHEESECLOTH_OPTS=--no-port-mapping'
Check ((TheService).State -eq 'Running') 'a reinstall with good options starts the service'
CheckRestarts
WaitUntil 'a member after the fix' { $s = Status; $s -and $s.phase -eq 'member' }

Write-Host '== cheesecloth.exe in use'
# A program that shows cheesecloth's events, such as Event Viewer, loads
# cheesecloth.exe as a resource file, as this does. Setup must wait for it,
# and stop before it changes any file if it stays in use.
Add-Type -Namespace Check -Name Kernel32 -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool FreeLibrary(IntPtr module);
'@
function HoldExe {
    # LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE
    $module = [Check.Kernel32]::LoadLibraryExW($cli, [IntPtr]::Zero, 0x22)
    if ($module -eq [IntPtr]::Zero) {
        throw "loading cheesecloth.exe failed: error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    $module
}

function ReleaseExe([IntPtr] $module) {
    if (-not [Check.Kernel32]::FreeLibrary($module)) { throw 'FreeLibrary failed' }
}

# Held all the time: Setup waits a few seconds, then stops with code 7.
$module = HoldExe
try {
    Install $upgrade 'in-use' -Expect 7
} finally {
    ReleaseExe $module
}
$log = Join-Path $work 'in-use.log'
Check (Select-String -Path $log -Pattern 'Close that program and run Setup again' -SimpleMatch -Quiet) 'Setup says that cheesecloth.exe is in use'

# Released while Setup waits: Setup goes on.
$module = HoldExe
$held = $true
try {
    Install $upgrade 'released' -During {
        param($log)
        WaitUntil 'Setup finds cheesecloth.exe in use' {
            (Test-Path $log) -and (Select-String -Path $log -Pattern 'cheesecloth.exe is in use:' -SimpleMatch -Quiet)
        } -Seconds 30
        ReleaseExe $module
        $script:held = $false
    }
} finally {
    if ($held) { ReleaseExe $module }
}
Check ((TheService).State -eq 'Running') 'Setup waits until cheesecloth.exe is free'
CheckRestarts
WaitUntil 'a member after the release' { $s = Status; $s -and $s.phase -eq 'member' }

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
