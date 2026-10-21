; Cre8Labs Net Tools installer (Inno Setup 6)
; Build with build.ps1, or: ISCC /DAppVersion=1.0.0 installer\NetTools.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Cre8Labs Net Tools"
#define AppExe "Cre8Labs-NetTools.exe"

[Setup]
; Never change AppId: it is how Windows matches upgrades and the uninstaller.
AppId={{D42A4200-F3E1-445B-BC1E-966E9F381037}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Cre8Labs
AppPublisherURL=https://cre8labs.uk
AppSupportURL=https://cre8labs.uk
DefaultDirName={autopf}\Cre8Labs\Net Tools
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Cre8Labs-NetTools-Setup-{#AppVersion}
SetupIconFile=..\src\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
WizardImageFile=wizard-1x.bmp,wizard-2x.bmp
WizardSmallImageFile=wizard-small-1x.bmp,wizard-small-2x.bmp
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Cre8Labs
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\release-build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[InstallDelete]
; Files from before the rename to Net Tools
Type: files; Name: "{app}\Cre8Labs-IPSwitcher.exe"
Type: files; Name: "{autoprograms}\Cre8Labs IP Switcher.lnk"
Type: files; Name: "{autodesktop}\Cre8Labs IP Switcher.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Allow inbound DHCP (UDP 67 and 68) for this program only, so the DHCP pool can hear devices.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Cre8Labs IP Switcher DHCP"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Cre8Labs Net Tools DHCP"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Cre8Labs Net Tools DHCP"" dir=in action=allow protocol=UDP localport=67,68 profile=any program=""{app}\{#AppExe}"""; Flags: runhidden
; Setup is already elevated, so launching as the current (admin) user avoids a second UAC prompt.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Cre8Labs Net Tools DHCP"""; Flags: runhidden; RunOnceId: "RemoveDhcpFirewallRule"

[Code]
// The app targets .NET Framework 4.8, built into Windows 10 1903+ and Windows 11.
function IsDotNet48Installed: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
    and (Release >= 528040);
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNet48Installed;
  if not Result then
    MsgBox('{#AppName} needs .NET Framework 4.8. It is included with Windows 10 (May 2019 update or later) and Windows 11.' + #13#10#13#10 +
      'Install .NET Framework 4.8 from Microsoft, then run this setup again.', mbError, MB_OK);
end;
