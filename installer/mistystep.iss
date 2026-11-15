; Misty Step installer (Inno Setup 6). Built by tools/build-release.ps1, which passes:
;   /DAppVersion=1.2.3  /DArch=x64|arm64  /DSourceExe=<published MistyStep.exe>  /DOutputDir=<folder>
; Per-machine install into Program Files. Elevation is needed anyway for the firewall rules.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceExe
  #error Pass /DSourceExe=<path to the published MistyStep.exe>
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

#define AppName "Misty Step"
#define ExeName "MistyStep.exe"
#define Repo "https://github.com/paulsonkantony/MistyStep"

[Setup]
AppId={{6F1D2C1E-6B1B-4C64-9E3A-4D0A5E2B7C11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=paulsonkantony
AppPublisherURL={#Repo}
AppSupportURL={#Repo}
AppUpdatesURL={#Repo}/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=MistyStep-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\MistyStep.App\Assets\MistyStep.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
InfoBeforeFile=notice.txt
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The running app holds this mutex; Setup asks to close it before replacing files.
AppMutex=Local\MistyStep.SingleInstance
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#ExeName}"

[Run]
; The app's own firewall helper: removes every inbound rule for this exe (including broad ones Windows
; may have made) and adds the two scoped rules for the default port.
Filename: "{app}\{#ExeName}"; Parameters: "--update-firewall 45654"; StatusMsg: "Setting up Windows Firewall rules..."; Flags: runhidden waituntilterminated
; Start the app as the signed-in user (not as admin). On a first install it opens the welcome window.
Filename: "{app}\{#ExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Misty Step (Tailscale)"""; Flags: runhidden; RunOnceId: "DelRuleTailscale"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Misty Step (Home Wi-Fi)"""; Flags: runhidden; RunOnceId: "DelRuleWifi"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all dir=in program=""{app}\{#ExeName}"""; Flags: runhidden; RunOnceId: "DelRuleProgram"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { "Start with Windows" lives in the signed-in user's Run key. }
    RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'MistyStep');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent then
      if MsgBox('Also delete Misty Step''s settings, paired devices and logs?' #13#10#13#10 +
                'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(ExpandConstant('{userappdata}\MistyStep'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\MistyStep'), True, True, True);
      end;
  end;
end;
