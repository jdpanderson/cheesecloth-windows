; The cheesecloth installer for Windows, built by build.ps1 with Inno Setup.
; It installs the daemon, tray app, wintun.dll and docs, adds the folder to
; the system PATH, registers the Event Log source, and sets up and starts the
; service, as packaging/cheesecloth.service in cheesecloth does with systemd.
; /CHEESECLOTH_OPTS="..." on the command line, or the wizard's options page,
; sets the daemon's options, as CHEESECLOTH_OPTS does on Linux.
;
; build.ps1 defines Version, NumericVersion, Arch (x64 or arm64) and Stage.

#if Ver < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1 or later is needed
#endif
#if !Defined(Version) || !Defined(NumericVersion) || !Defined(Arch) || !Defined(Stage)
  #error Build with build.ps1, which defines Version, NumericVersion, Arch and Stage
#endif

; build.ps1 -SignTool defines Sign, and the Sign Tool "cheesecloth".
#ifdef Sign
  #define SignFlag " sign"
#else
  #define SignFlag ""
#endif

#if Arch == "x64"
  #define Archs "x64os"
#elif Arch == "arm64"
  #define Archs "arm64"
#else
  #error Arch must be x64 or arm64
#endif

; The only folder Setup installs into. In Program Files only administrators
; may change files, and the service runs cheesecloth.exe from it as
; LocalSystem. check.ps1 uses it too.
#define InstallDir "{autopf}\cheesecloth"

[Setup]
; Never change the AppId: a new one would install a second copy. check.ps1
; uses it too.
AppId={{CFACBA51-554C-488D-B946-84F644BFB8D5}
AppName=cheesecloth
AppVersion={#Version}
VersionInfoVersion={#NumericVersion}
AppPublisher=Jonathan Anderson
AppPublisherURL=https://github.com/jdpanderson/cheesecloth
AppSupportURL=https://github.com/jdpanderson/cheesecloth/issues
DefaultDirName={#InstallDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=admin
MinVersion=10.0
WizardStyle=modern
SetupLogging=yes
; A 64-bit Setup, which sees the native registry and System32. On Arm64 it
; runs under x64 emulation, which only Windows 11 has. x64os, not
; x64compatible: the x64 wintun.dll does not work on Arm64 Windows.
SetupArchitecture=x64
ArchitecturesAllowed={#Archs}
ArchitecturesInstallIn64BitMode={#Archs}
ChangesEnvironment=yes
; PrepareToInstall closes the tray app and stops the service itself.
; Restart Manager cannot close the tray app, which shows no window.
CloseApplications=no
UninstallDisplayIcon={app}\cheesecloth.exe
OutputBaseFilename=cheesecloth-{#Version}-windows-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
#ifdef Sign
; Signs the installer and the uninstaller.
SignTool=cheesecloth
#endif

[Files]
Source: "{#Stage}\cheesecloth.exe"; DestDir: "{app}"; Flags: ignoreversion{#SignFlag}
Source: "{#Stage}\cheesecloth-tray.exe"; DestDir: "{app}"; Flags: ignoreversion{#SignFlag}
Source: "{#Stage}\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Stage}\docs\*.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Registry]
; The Event Log source. Event Viewer reads each event's text from the message
; table in cheesecloth.exe (see crates/cheesecloth/build.rs in cheesecloth).
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\cheesecloth"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{app}\cheesecloth.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\cheesecloth"; ValueType: dword; ValueName: "TypesSupported"; ValueData: 7
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath
; Start one tray app in each signed-in user's session. The service keeps
; running when a user closes the tray app or signs out.
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "cheesecloth"; ValueData: """{app}\cheesecloth-tray.exe"""; Flags: uninsdeletevalue

[Icons]
Name: "{autoprograms}\Cheesecloth"; Filename: "{app}\cheesecloth-tray.exe"

[Run]
Filename: "{app}\cheesecloth-tray.exe"; Description: "Open Cheesecloth"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
const
  ServiceName = 'cheesecloth';
  ServiceDescription = 'cheesecloth peer-to-peer WireGuard mesh';
  { How long to wait for the service to stop or start, in ms. }
  StateTimeout = 60000;
  { Matches WAIT_HINT in crates/cheesecloth-daemon/src/service/windows.rs in
    cheesecloth. }
  PreshutdownTimeout = 30000;
  { Setup's exit code when the files are installed but the service is not
    set up. }
  ServiceSetupFailed = 10;
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  { How long to wait for cheesecloth.exe to be free before asking, in ms. }
  InUseWait = 5000;
  InUseMessage = 'cheesecloth.exe is in use, for example by Event Viewer showing cheesecloth''s events.';
  WM_CLOSE = $0010;

  SC_MANAGER_CONNECT = $0001;
  SERVICE_CHANGE_CONFIG = $0002;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOP = $0020;
  SERVICE_CONTROL_STOP = 1;
  SERVICE_CONFIG_PRESHUTDOWN_INFO = 7;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;
  ERROR_SERVICE_ALREADY_RUNNING = 1056;
  ERROR_SERVICE_DOES_NOT_EXIST = 1060;
  ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
  ERROR_SERVICE_NOT_ACTIVE = 1062;
  { DELETE, which is the name of a Pascal Script procedure. }
  DELETE_ACCESS = $00010000;
  FILE_SHARE_ALL = 7;
  OPEN_EXISTING = 3;
  FileDispositionInfo = 4;

type
  TServiceStatus = record
    ServiceType: DWORD;
    CurrentState: DWORD;
    ControlsAccepted: DWORD;
    Win32ExitCode: DWORD;
    ServiceSpecificExitCode: DWORD;
    CheckPoint: DWORD;
    WaitHint: DWORD;
  end;

{ What sc.exe cannot do: read the service's state, stop it, and set its
  pre-shutdown timeout. }
function OpenSCManager(MachineName, DatabaseName: String; Access: DWORD): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: String; Access: DWORD): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function ControlService(Service: THandle; Control: DWORD; var Status: TServiceStatus): BOOL;
  external 'ControlService@advapi32.dll stdcall';
{ Info is a SERVICE_PRESHUTDOWN_INFO, which holds one DWORD. }
function ChangeServiceConfig2(Service: THandle; InfoLevel: DWORD; var Info: DWORD): BOOL;
  external 'ChangeServiceConfig2W@advapi32.dll stdcall';

{ What FileInUse needs. The file handle is an Int64, not a THandle, so that
  INVALID_HANDLE_VALUE is -1. Info is a FILE_DISPOSITION_INFO, which holds
  one BOOLEAN. }
function CreateFile(FileName: String; Access, ShareMode: DWORD; Security: Int64;
  Disposition, Flags: DWORD; Template: Int64): Int64;
  external 'CreateFileW@kernel32.dll stdcall';
function SetFileInformationByHandle(F: Int64; InfoClass: Integer; var Info: Byte;
  Size: DWORD): BOOL;
  external 'SetFileInformationByHandle@kernel32.dll stdcall';
function CloseHandle(Handle: Int64): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

var
  { The error that stopped the service setup, if any. }
  ServiceError: String;
  OptsPage: TInputQueryWizardPage;

{ The value of /CHEESECLOTH_OPTS= on the command line, if it is there. An
  empty value is a value: it clears the options. }
function CommandLineOpts(var Opts: String): Boolean;
var
  Prefix: String;
  I: Integer;
begin
  Prefix := '/CHEESECLOTH_OPTS=';
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, Length(Prefix)), Prefix) = 0 then
    begin
      Opts := Copy(ParamStr(I), Length(Prefix) + 1, Length(ParamStr(I)));
      Result := True;
    end;
end;

{ The options for `cheesecloth daemon`: from the command line, else from the
  last install, else none. Silent installs skip the page and keep them. }
procedure InitializeWizard;
var
  Opts: String;
begin
  OptsPage := CreateInputQueryPage(wpSelectDir, 'Service options',
    'Options for the cheesecloth service.',
    'The service runs "cheesecloth daemon" with these options, for example ' +
    '--no-port-mapping --name office-pc. See "cheesecloth daemon --help". ' +
    'Leave it empty for the defaults.');
  OptsPage.Add('&Options:', False);
  if not CommandLineOpts(Opts) then
    Opts := GetPreviousData('Options', '');
  OptsPage.Values[0] := Opts;
end;

function DaemonOpts: String;
begin
  Result := Trim(OptsPage.Values[0]);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Options', DaemonOpts);
end;

{ Whether Path, one entry of PATH, names the install folder. }
function IsAppPath(Path: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Trim(Path)),
    RemoveBackslashUnlessRoot(ExpandConstant('{app}'))) = 0;
end;

function NeedsAddPath: Boolean;
var
  Path: String;
  Entries: TArrayOfString;
  I: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    Exit;
  Entries := StringSplit(Path, [';'], stExcludeEmpty);
  for I := 0 to GetArrayLength(Entries) - 1 do
    if IsAppPath(Entries[I]) then
      Result := False;
end;

procedure RemoveFromPath;
var
  Path, Kept: String;
  Entries: TArrayOfString;
  I: Integer;
  Found: Boolean;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Path) then
    Exit;
  Entries := StringSplit(Path, [';'], stExcludeEmpty);
  Kept := '';
  Found := False;
  for I := 0 to GetArrayLength(Entries) - 1 do
    if IsAppPath(Entries[I]) then
      Found := True
    else if Kept = '' then
      Kept := Entries[I]
    else
      Kept := Kept + ';' + Entries[I];
  if Found and not RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Kept) then
    RaiseException('Removing the install folder from PATH failed.');
end;

{ S quoted as one argument for a program that splits its command line by the
  C runtime's rules, such as sc.exe. }
function SysArg(S: String): String;
var
  I, Backslashes: Integer;
begin
  Result := '"';
  Backslashes := 0;
  for I := 1 to Length(S) do
  begin
    if S[I] = '\' then
      Backslashes := Backslashes + 1
    else
    begin
      if S[I] = '"' then
        Result := Result + StringOfChar('\', Backslashes + 1);
      Backslashes := 0;
    end;
    Result := Result + S[I];
  end;
  Result := Result + StringOfChar('\', Backslashes) + '"';
end;

{ Runs sc.exe, with its output in the Setup log. Its exit code is a Win32
  error; 0 and Allowed mean success. }
procedure ScAllowing(Params: String; Allowed: Integer);
var
  ResultCode: Integer;
begin
  Log('sc.exe ' + Params);
  if not ExecAndLogOutput(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode, nil) then
    RaiseException('Running sc.exe failed: ' + SysErrorMessage(ResultCode));
  if (ResultCode <> 0) and (ResultCode <> Allowed) then
    RaiseException('sc.exe ' + Params + ' failed: ' + SysErrorMessage(ResultCode));
end;

procedure Sc(Params: String);
begin
  ScAllowing(Params, 0);
end;

{ Opens the service with Access, and the service manager in Manager. Returns
  0 when the service does not exist. }
function OpenTheService(Access: DWORD; var Manager: THandle): THandle;
var
  Error: Integer;
begin
  Manager := OpenSCManager('', 'ServicesActive', SC_MANAGER_CONNECT);
  if Manager = 0 then
    RaiseException('Opening the service manager failed: ' + SysErrorMessage(DLLGetLastError));
  Result := OpenService(Manager, ServiceName, Access);
  if Result = 0 then
  begin
    Error := DLLGetLastError;
    CloseServiceHandle(Manager);
    if Error <> ERROR_SERVICE_DOES_NOT_EXIST then
      RaiseException('Opening the service failed: ' + SysErrorMessage(Error));
  end;
end;

procedure CloseTheService(Service, Manager: THandle);
begin
  CloseServiceHandle(Service);
  CloseServiceHandle(Manager);
end;

{ The service's state, or 0 when it does not exist. }
function ServiceState: DWORD;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
begin
  Result := 0;
  Service := OpenTheService(SERVICE_QUERY_STATUS, Manager);
  if Service = 0 then
    Exit;
  try
    if not QueryServiceStatus(Service, Status) then
      RaiseException('Reading the service state failed: ' + SysErrorMessage(DLLGetLastError));
    Result := Status.CurrentState;
  finally
    CloseTheService(Service, Manager);
  end;
end;

{ One step of a wait for the service, which is in State: it raises an error
  after StateTimeout in all, else sleeps 250 ms. }
procedure WaitStep(var Waited: Integer; State: DWORD);
begin
  if Waited >= StateTimeout then
    RaiseException('The service did not start or stop in ' + IntToStr(StateTimeout div 1000) +
      ' s; it is in state ' + IntToStr(State) + '.');
  Sleep(250);
  Waited := Waited + 250;
end;

{ Waits until the service runs. A service that stops instead has failed. }
procedure WaitUntilRunning;
var
  State: DWORD;
  Waited: Integer;
begin
  Waited := 0;
  State := ServiceState;
  while State <> SERVICE_RUNNING do
  begin
    if State = SERVICE_STOPPED then
      RaiseException('The service stopped; see Event Viewer, Windows Logs, Application.');
    WaitStep(Waited, State);
    State := ServiceState;
  end;
end;

{ Asks the service to stop. Returns False if it cannot take the request now,
  because it has begun to stop or has stopped since its state was read. }
function RequestStop: Boolean;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
  Error: Integer;
begin
  Result := True;
  Service := OpenTheService(SERVICE_STOP, Manager);
  if Service = 0 then
    RaiseException('The service does not exist.');
  try
    if not ControlService(Service, SERVICE_CONTROL_STOP, Status) then
    begin
      Error := DLLGetLastError;
      if (Error <> ERROR_SERVICE_CANNOT_ACCEPT_CTRL) and (Error <> ERROR_SERVICE_NOT_ACTIVE) then
        RaiseException('Stopping the service failed: ' + SysErrorMessage(Error));
      Result := False;
    end;
  finally
    CloseTheService(Service, Manager);
  end;
end;

{ Stops the service, if it exists, and waits until it has stopped. A service
  that fails is restarted by the SCM, so this first disables it and clears
  its failure actions. A disabled service cannot start, even by a restart
  that is already queued. SetUpService sets both again, so an install or
  uninstall that fails or is cancelled after this leaves the service stopped
  and disabled until an install succeeds. }
procedure StopService;
var
  State: DWORD;
  Waited: Integer;
  Sent: Boolean;
begin
  if ServiceState = 0 then
    Exit;
  Log('Stopping the service');
  Sc('config ' + ServiceName + ' start= disabled');
  Sc('failure ' + ServiceName + ' reset= 0 actions= ""');
  { A starting service takes no requests, so STOP waits until it runs. }
  Waited := 0;
  Sent := False;
  State := ServiceState;
  while (State <> SERVICE_STOPPED) and (State <> 0) do
  begin
    if (State = SERVICE_RUNNING) and not Sent then
      Sent := RequestStop;
    WaitStep(Waited, State);
    State := ServiceState;
  end;
end;

procedure SetPreshutdownTimeout(Ms: DWORD);
var
  Manager, Service: THandle;
  Info: DWORD;
begin
  Service := OpenTheService(SERVICE_CHANGE_CONFIG, Manager);
  if Service = 0 then
    RaiseException('The service does not exist.');
  Info := Ms;
  try
    if not ChangeServiceConfig2(Service, SERVICE_CONFIG_PRESHUTDOWN_INFO, Info) then
      RaiseException('Setting the pre-shutdown timeout failed: ' +
        SysErrorMessage(DLLGetLastError));
  finally
    CloseTheService(Service, Manager);
  end;
end;

{ Creates or updates the service, then starts it and waits until it runs. }
procedure SetUpService;
var
  Command, Config: String;
begin
  Command := '"' + ExpandConstant('{app}\cheesecloth.exe') + '" daemon';
  if DaemonOpts <> '' then
    Command := Command + ' ' + DaemonOpts;
  Config := ' binPath= ' + SysArg(Command) + ' start= auto obj= LocalSystem DisplayName= ' +
    ServiceName;
  if ServiceState = 0 then
    Sc('create ' + ServiceName + Config)
  else
    Sc('config ' + ServiceName + Config);
  Sc('description ' + ServiceName + ' ' + SysArg(ServiceDescription));
  { Restart after 5 s, also after a daemon error, as Restart=on-failure does. }
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('failureflag ' + ServiceName + ' 1');
  SetPreshutdownTimeout(PreshutdownTimeout);
  { A restart that the SCM queued before StopService can start the service
    as soon as it is enabled again. }
  ScAllowing('start ' + ServiceName, ERROR_SERVICE_ALREADY_RUNNING);
  WaitUntilRunning;
end;

{ Whether an installed file cannot be deleted, as Setup must do to replace
  it. It does what DeleteFile does: it opens the file and marks it for
  deletion. Then it clears the mark, so the file is not deleted. }
function FileInUse(Name: String): Boolean;
var
  Exe: String;
  F: Int64;
  Mark: Byte;
begin
  Result := False;
  Exe := ExpandConstant('{app}\' + Name);
  if not FileExists(Exe) then
    Exit;
  F := CreateFile(Exe, DELETE_ACCESS, FILE_SHARE_ALL, 0, OPEN_EXISTING, 0, 0);
  if F = -1 then
  begin
    Log(Name + ' is in use: opening it to delete it failed: ' +
      SysErrorMessage(DLLGetLastError));
    Result := True;
    Exit;
  end;
  try
    Mark := 1;
    if not SetFileInformationByHandle(F, FileDispositionInfo, Mark, 1) then
    begin
      Log(Name + ' is in use: marking it for deletion failed: ' +
        SysErrorMessage(DLLGetLastError));
      Result := True;
    end
    else
    begin
      Mark := 0;
      if not SetFileInformationByHandle(F, FileDispositionInfo, Mark, 1) then
        RaiseException('Clearing the deletion mark of ' + Exe + ' failed: ' +
          SysErrorMessage(DLLGetLastError));
    end;
  finally
    CloseHandle(F);
  end;
end;

{ Waits up to InUseWait for cheesecloth.exe to be free, since a program
  that read cheesecloth's events may hold it for a moment. Returns whether
  it is free. }
function WaitForExe: Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  Result := not FileInUse('cheesecloth.exe');
  while not Result and (Waited < InUseWait) do
  begin
    Sleep(500);
    Waited := Waited + 500;
    Result := not FileInUse('cheesecloth.exe');
  end;
end;

{ Closes the tray app in this session, before Setup or the uninstaller stops
  the service. If another session still uses it, raises an error, so the
  caller stops before it changes anything and the user can close it there. }
procedure CloseTray;
var
  Window: HWND;
  Waited: Integer;
begin
  if not FileInUse('cheesecloth-tray.exe') then
    Exit;
  Window := FindWindowByWindowName('Cheesecloth tray CFACBA51-554C-488D-B946-84F644BFB8D5');
  if Window <> 0 then
    PostMessage(Window, WM_CLOSE, 0, 0);
  Waited := 0;
  while FileInUse('cheesecloth-tray.exe') and (Waited < InUseWait) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  if FileInUse('cheesecloth-tray.exe') then
    RaiseException('Close Cheesecloth in every signed-in session, then try again.');
end;

{ The folder Setup would install into, if /DIR= named one other than
  InstallDir, else ''. Other users may be able to change files there. }
function OtherInstallFolder: String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
  if CompareText(Result, ExpandConstant('{#InstallDir}')) = 0 then
    Result := '';
end;

{ A failure here makes Setup exit with code 7 before it changes any file.
  Another install folder is refused first, so the service is left as it is.
  While cheesecloth.exe stays in use, the wizard asks the user to close the
  program and retry; /SUPPRESSMSGBOXES answers Cancel. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Folder: String;
begin
  Result := '';
  Folder := OtherInstallFolder;
  if Folder <> '' then
  begin
    Result := 'Setup installs cheesecloth only into ' + ExpandConstant('{#InstallDir}') +
      ', not into ' + Folder + ', because other users may be able to change files there.';
    Exit;
  end;
  try
    CloseTray;
  except
    Result := GetExceptionMessage;
    Exit;
  end;
  try
    StopService;
  except
    Result := 'The cheesecloth service could not be stopped: ' + GetExceptionMessage;
    Exit;
  end;
  while not WaitForExe do
    if SuppressibleMsgBox(InUseMessage + ' Close that program, then click Retry.',
      mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := InUseMessage + ' Close that program and run Setup again.';
      Exit;
    end;
end;

{ Inno has no rollback for this step, so a failure keeps the installed files
  and makes Setup exit with ServiceSetupFailed. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  try
    SetUpService;
  except
    ServiceError := GetExceptionMessage;
    Log('Setting up the service failed: ' + ServiceError);
    SuppressibleMsgBox('cheesecloth is installed, but its service could not be set up: ' +
      ServiceError, mbError, MB_OK, IDOK);
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if ServiceError <> '' then
    Result := ServiceSetupFailed;
end;

{ A failure to stop or delete the service raises an error, so the uninstall
  stops before it removes anything. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usUninstall:
      begin
        CloseTray;
        if ServiceState <> 0 then
        begin
          StopService;
          Sc('delete ' + ServiceName);
        end;
      end;
    usPostUninstall:
      RemoveFromPath;
  end;
end;
