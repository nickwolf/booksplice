; Per-user setup for BookSplice. Build it with scripts\booksplice\Build-Installer.ps1, which passes the
; defines below. The files go to {app}\app; the launcher's --register then creates the managed folder
; ({app}\.booksplice-managed-<id>) and the Start menu shortcut, so Inno creates no shortcut of its own.

#ifndef AppVersion
  #error AppVersion is not defined.
#endif
#ifndef NumericVersion
  #error NumericVersion is not defined.
#endif
#ifndef SourceDir
  #error SourceDir is not defined.
#endif
#ifndef OutputDir
  #error OutputDir is not defined.
#endif
#ifndef Flavor
  #define Flavor "Test"
#endif

; Test-flavor builds get their own AppId, name and default folder so a test run can never touch a real install.
#if Flavor == "Release"
  #define AppIdGuid "{{3B7D5A4E-6F1C-4C58-9E0A-52D7B1F46A19}"
  #define AppTitle "BookSplice"
  #define DefaultFolder "Programs\BookSplice"
#else
  #define AppIdGuid "{{C94E2B87-0D3A-4F6B-A1D5-7E88F20B3C46}"
  #define AppTitle "BookSplice (test build)"
  #define DefaultFolder "Programs\BookSplice-Test"
#endif

[Setup]
AppId={#AppIdGuid}
AppName={#AppTitle}
AppVersion={#AppVersion}
AppPublisher=BookSplice
AppPublisherURL=https://github.com/nickwolf/booksplice
VersionInfoVersion={#NumericVersion}
DefaultDirName={localappdata}\{#DefaultFolder}
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=BookSplice-{#AppVersion}-win-x64-setup
UninstallDisplayName={#AppTitle}
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}\app"; Flags: recursesubdirs createallsubdirs ignoreversion

[UninstallDelete]
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\.booksplice-managed-*"

[Code]
const
  RegisterDone = 0;
  RegisterCollision = 2;
  RegisterBusy = 3;

procedure ExitProcess(ExitCode: Cardinal); external 'ExitProcess@kernel32.dll stdcall';

function IsSilent: Boolean;
var
  I: Integer;
  Argument: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Argument := Uppercase(ParamStr(I));
    if (Argument = '/SILENT') or (Argument = '/VERYSILENT') then Result := True;
  end;
end;

function HasSwitch(Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if Uppercase(ParamStr(I)) = Uppercase(Name) then Result := True;
end;

// {app} is not available yet in InitializeSetup, so work out the folder the same way Setup will.
function InstallFolder: String;
begin
  Result := ExpandConstant('{param:DIR}');
  if Result = '' then Result := ExpandConstant('{localappdata}\{#DefaultFolder}');
  Result := RemoveBackslashUnlessRoot(Result);
end;

// Runs a launcher command hidden and returns its exit code. Stderr (one line on failure) goes to Reason.
function RunLauncher(Launcher, Arguments: String; var Reason: String): Integer;
var
  ErrorFile: String;
  Output: AnsiString;
begin
  ErrorFile := ExpandConstant('{tmp}\booksplice-launcher-error.txt');
  DeleteFile(ErrorFile);
  Reason := '';
  if not Exec(ExpandConstant('{cmd}'), '/S /C ""' + Launcher + '" ' + Arguments + ' 2> "' + ErrorFile + '""', '',
    SW_HIDE, ewWaitUntilTerminated, Result) then
  begin
    Reason := SysErrorMessage(Result);
    Result := -1;
    Exit;
  end;
  if LoadStringFromFile(ErrorFile, Output) then Reason := Trim(String(Output));
end;

// 0 when the folder is installed, 1 when it is not (or holds no app yet), anything else when the
// launcher could not tell; setup refuses in that case rather than overwrite a possible install.
function InstalledState(Root: String; var Reason: String): Integer;
var
  Launcher: String;
begin
  Reason := '';
  Launcher := Root + '\app\BookSplice.Launcher.exe';
  if FileExists(Launcher) then Result := RunLauncher(Launcher, '--installed', Reason)
  else Result := 1;
end;

function InitializeSetup: Boolean;
var
  Root, Shortcut, Reason: String;
  Answer, State: Integer;
begin
  Result := True;
  Root := InstallFolder;
  State := InstalledState(Root, Reason);
  if FileExists(Root + '\BookSplice.Gui.exe') then
  begin
    if not IsSilent then
      MsgBox('A portable copy of BookSplice is in ' + Root + '. Delete it before running setup. ' +
        'Your settings in %LOCALAPPDATA%\BookSplice are kept.', mbError, MB_OK);
    Result := False;
  end
  else if (State <> 0) and (State <> 1) then
  begin
    if not IsSilent then
      MsgBox('Setup could not check the BookSplice already in ' + Root + ', so it changed nothing. ' + Reason,
        mbError, MB_OK);
    Result := False;
  end
  else if State = 0 then
  begin
    if not IsSilent then
    begin
      Shortcut := ExpandConstant('{userprograms}\BookSplice.lnk');
      if FileExists(Shortcut) then
      begin
        Answer := MsgBox('BookSplice is already installed. Updates come from Settings > Updates inside BookSplice.' + #13#10#13#10 +
          'Open BookSplice now?', mbInformation, MB_YESNO);
        if Answer = IDYES then ShellExec('open', Shortcut, '', '', SW_SHOWNORMAL, ewNoWait, Answer);
      end
      else
        MsgBox('BookSplice is already installed. Updates come from Settings > Updates inside BookSplice.',
          mbInformation, MB_OK);
    end;
    Result := False;
  end;
end;

// Registers the installation, retrying while BookSplice is open. Returns the final launcher exit code.
function RegisterInstallation(Replace: Boolean; var Reason: String): Integer;
var
  Launcher, Arguments: String;
begin
  Launcher := ExpandConstant('{app}\app\BookSplice.Launcher.exe');
  Arguments := '--register';
  if Replace then Arguments := Arguments + ' --replace-shortcut';
  repeat
    Result := RunLauncher(Launcher, Arguments, Reason);
    if (Result <> RegisterBusy) or IsSilent then Exit;
  until MsgBox('BookSplice is running, so setup cannot finish. Close BookSplice, then choose Retry.',
    mbError, MB_RETRYCANCEL) <> IDRETRY;
end;

// Files are already copied and the uninstall entry exists by now, and Setup does not roll back from here.
// Abort and RaiseException both end with exit code 0, so leave through ExitProcess with a failure code.
// Without a binding the folder does not count as installed, so running setup again overwrites it.
procedure FailRegister(Message: String);
begin
  Log(Message);
  if not IsSilent then MsgBox(Message, mbError, MB_OK);
  ExitProcess(4);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Reason: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Code := RegisterInstallation(False, Reason);
  if Code = RegisterCollision then
  begin
    if IsSilent then
    begin
      if HasSwitch('/REPLACESHORTCUT') then Code := RegisterInstallation(True, Reason);
    end
    else if MsgBox('A BookSplice shortcut from another copy of BookSplice already exists. Replace it?',
      mbConfirmation, MB_YESNO) = IDYES then
      Code := RegisterInstallation(True, Reason);
    if Code = RegisterCollision then Code := RegisterDone;
  end;
  if Code = RegisterBusy then
    FailRegister('BookSplice is running, so setup could not finish. Close BookSplice and run setup again.')
  else if Code <> RegisterDone then
    FailRegister('Setup could not finish registering BookSplice. ' + Reason + #13#10#13#10 +
      'Run setup again to retry.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Root, Launcher, Reason, Failures: String;
  Find: TFindRec;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Root := ExpandConstant('{app}');
  Failures := '';
  if FindFirst(Root + '\.booksplice-managed-*', Find) then
  try
    repeat
      if (Find.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Launcher := Root + '\' + Find.Name + '\BookSplice.Launcher.exe';
        if FileExists(Launcher) then
        begin
          Code := RunLauncher(Launcher, '--unregister', Reason);
          if Code = RegisterBusy then
          begin
            if not UninstallSilent then
              MsgBox('BookSplice is running. Close it and run the uninstaller again.', mbError, MB_OK);
            Abort;
          end;
          if Code <> RegisterDone then Failures := Failures + Reason + #13#10;
        end;
      end;
    until not FindNext(Find);
  finally
    FindClose(Find);
  end;
  if (Failures <> '') and not UninstallSilent then
    MsgBox('BookSplice was removed, but cleaning up its shortcut and registration failed:' + #13#10 + Failures,
      mbError, MB_OK);
end;
