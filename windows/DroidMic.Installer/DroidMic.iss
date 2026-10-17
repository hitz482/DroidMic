; DroidMic for Windows installer (Inno Setup 6).
; Built by scripts/package-release.ps1:
;   ISCC /DAppVersion=0.1.0 /DSourceDir=<dist\windows\app> DroidMic.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\windows\app"
#endif
; The driver task only exists if the driver package was built (scripts/build-driver.ps1).
#define HaveDriver FileExists(SourceDir + "\driver\DroidMicVAD.inf")

[Setup]
AppId={{8C2E7B0E-3F0B-4B8E-9F11-6C1E2A5D7D11}
AppName=DroidMic
AppVersion={#AppVersion}
AppPublisher=DroidMic
DefaultDirName={autopf}\DroidMic
DefaultGroupName=DroidMic
OutputBaseFilename=DroidMic-{#AppVersion}-windows-x64-setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\DroidMic.Desktop\Assets\droidmic.ico
UninstallDisplayIcon={app}\DroidMic.exe
CloseApplications=yes

[Tasks]
#if HaveDriver
Name: "driver"; Description: "Install the DroidMic virtual microphone driver"
#endif
Name: "firewall"; Description: "Allow DroidMic through Windows Firewall on private networks"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "Start DroidMic when I sign in"; Flags: unchecked

[Files]
Source: "{#SourceDir}\DroidMic.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\DroidMic"; Filename: "{app}\DroidMic.exe"
Name: "{autodesktop}\DroidMic"; Filename: "{app}\DroidMic.exe"; Tasks: desktopicon
Name: "{userstartup}\DroidMic"; Filename: "{app}\DroidMic.exe"; Tasks: autostart

[Run]
#if HaveDriver
Filename: "{app}\driver\DroidMic.DriverSetup.exe"; Parameters: "install ""{app}\driver\DroidMicVAD.inf"""; \
  StatusMsg: "Installing the virtual microphone driver..."; Flags: runhidden waituntilterminated; Tasks: driver
#endif
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""DroidMic"" dir=in action=allow program=""{app}\DroidMic.exe"" protocol=TCP localport=48110 profile=private"; \
  Flags: runhidden; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""DroidMic"" dir=in action=allow program=""{app}\DroidMic.exe"" protocol=UDP localport=48111-48112 profile=private"; \
  Flags: runhidden; Tasks: firewall
Filename: "{app}\DroidMic.exe"; Description: "Start DroidMic"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\driver\DroidMic.DriverSetup.exe"; Parameters: "uninstall"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveDriver"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""DroidMic"""; Flags: runhidden; RunOnceId: "RemoveFirewall"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DroidMic\logs"

[Code]
function IsTestSigned(): Boolean;
var
  S: AnsiString;
begin
  Result := LoadStringFromFile(ExpandConstant('{app}\driver\signing.txt'), S) and (Pos('test', S) > 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsTestSigned() then
    MsgBox('This build contains a TEST-SIGNED driver. Windows only loads it on a PC in test-signing mode, ' +
           'which is meant for development only. If the DroidMic Microphone does not appear, ' +
           'see docs/DRIVER_INSTALLATION.md. Release builds use a Microsoft-signed driver.',
           mbInformation, MB_OK);
end;
