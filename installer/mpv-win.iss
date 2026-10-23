#ifndef AppSource
  #error AppSource must identify the staged application directory
#endif
#ifndef RuntimeInstaller
  #error RuntimeInstaller must identify the verified Microsoft runtime installer
#endif
#ifndef OutputPath
  #error OutputPath must identify the installer output directory
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef LicenseAgreement
  #error LicenseAgreement must identify the application and Microsoft component terms
#endif
#ifndef SdkTerms
  #error SdkTerms must identify the original Windows SDK component terms
#endif

[Setup]
AppId={{1740352E-FAE0-47F0-AB69-29B71DD67790}
AppName=mpv.win
AppVersion={#AppVersion}
AppPublisher=mpv.win contributors
AppPublisherURL=https://github.com/arthurfc-dev/mpv.win
DefaultDirName={localappdata}\Programs\mpv.win
DefaultGroupName=mpv.win
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.26100
OutputDir={#OutputPath}
OutputBaseFilename=mpv-win-{#AppVersion}-windows-x64-setup
SetupIconFile=..\src\Player.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\mpv-win.exe
LicenseFile={#LicenseAgreement}
InfoBeforeFile={#SdkTerms}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
ChangesAssociations=yes
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "openwith"; Description: "Add mpv.win to Open with for video files"; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RuntimeInstaller}"; Flags: dontcopy

[Icons]
Name: "{userprograms}\mpv.win"; Filename: "{app}\mpv-win.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\mpv.win"; Filename: "{app}\mpv-win.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\mpv-win.exe"; Parameters: "--register-file-associations"; Tasks: openwith; Flags: runhidden waituntilterminated
Filename: "{app}\mpv-win.exe"; Description: "Open mpv.win"; Flags: nowait postinstall skipifsilent

[Code]
function HasRuntime: Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  // The official .NET installers register all architectures in the 32-bit
  // registry view; the x64 path selects the runtime architecture.
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App', Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      if (Copy(Versions[I], 1, 5) = '10.0.') and
        (StrToIntDef(Copy(Versions[I], 6, Length(Versions[I])), -1) >= 13) then
      begin
        Log('Found registered x64 Microsoft.NETCore.App ' + Versions[I]);
        Result := True;
      end;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not HasRuntime then
  begin
    ExtractTemporaryFile('{#ExtractFileName(RuntimeInstaller)}');
    if not ShellExec('runas', ExpandConstant('{tmp}\{#ExtractFileName(RuntimeInstaller)}'),
      '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then
      Result := 'The Microsoft .NET 10 runtime is required. Allow its installation to continue.'
    else if (ExitCode = 3010) or (ExitCode = 1641) then
      NeedsRestart := True
    else if ExitCode <> 0 then
      Result := Format('The Microsoft .NET runtime could not be installed (code %d).', [ExitCode]);
    if (Result = '') and not HasRuntime then
    begin
      if NeedsRestart then
        Result := 'The Microsoft .NET runtime installer requested a restart. Restart Windows and run setup again.'
      else
        Result := 'The Microsoft .NET runtime installer completed, but the required x64 runtime could not be detected. See the setup log for details.';
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RegisteredExe: String;
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\mpv.win\Explorer', 'Executable', RegisteredExe) then
      if CompareText(RegisteredExe, ExpandConstant('{app}\mpv-win.exe')) = 0 then
        Exec(ExpandConstant('{app}\mpv-win.exe'), '--unregister-file-associations', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;
