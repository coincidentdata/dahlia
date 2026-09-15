#ifndef AppVersion
  #error Build with build.ps1 to supply the version and payload paths.
#endif

#define ProductName "Dahlia for SOLIDWORKS"
#define ProductId "{7D3C3DC0-1237-4EB1-827D-0BA158655073}"

[Setup]
AppId={{#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=Coincident Data
AppPublisherURL=https://coincidentdata.com
DefaultDirName={autopf}\Dahlia\SOLIDWORKS
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
WizardStyle=modern
OutputDir={#OutputDirectory}
OutputBaseFilename=Dahlia-SOLIDWORKS-{#AppVersion}-x64-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\SldworksPlugin.comhost.dll
InfoAfterFile=installed.txt
LicenseFile=..\LICENSE

[Files]
Source: "{#RuntimeInstaller}"; DestName: "windowsdesktop-runtime.exe"; Flags: dontcopy noencryption nocompression
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.comhost.dll,*.tlb"; Flags: ignoreversion recursesubdirs
Source: "{#PayloadDirectory}\SldworksPlugin.tlb"; DestDir: "{app}"; Flags: ignoreversion regtypelib 64bit
Source: "{#PayloadDirectory}\SldworksPlugin.comhost.dll"; DestDir: "{app}"; Flags: ignoreversion regserver 64bit
Source: "installed.txt"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Code]
#include "prerequisites.iss"

function InitializeSetup: Boolean;
var
  PreviousVersion: String;
  Previous, Current: Int64;
begin
  Result := False;
  if WizardSilent and (ExpandConstant('{param:ACCEPTLICENSE|0}') <> '1') then begin
    Log('Silent installation requires /ACCEPTLICENSE=1 to accept the Dahlia Community License.');
    SuppressibleMsgBox('Read the Dahlia Community License, then pass /ACCEPTLICENSE=1 to accept it for silent installation.',
      mbError, MB_OK, IDOK);
    Exit;
  end;
  if SolidWorksRunning then begin
    Log(CloseSolidWorksMessage);
    SuppressibleMsgBox(CloseSolidWorksMessage, mbError, MB_OK, IDOK);
    Exit;
  end;
  if not RegKeyExists(HKLM64,
      'SOFTWARE\Classes\CLSID\{83A33D30-27C5-11CE-BFD4-00400513BB57}\LocalServer32') then begin
    SuppressibleMsgBox('Install SOLIDWORKS before installing Dahlia.', mbError, MB_OK, IDOK);
    Exit;
  end;
  if RegQueryStringValue(HKLM64,
      'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1',
      'DisplayVersion', PreviousVersion) then begin
    if StrToVersion(PreviousVersion, Previous) and StrToVersion('{#AppVersion}', Current) then
      if ComparePackedVersion(Previous, Current) > 0 then begin
        SuppressibleMsgBox('A newer version of Dahlia is installed. Uninstall it before installing an older version.',
          mbError, MB_OK, IDOK);
        Exit;
      end;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if SolidWorksRunning then begin
    Result := CloseSolidWorksMessage;
    Exit;
  end;
  if RuntimeInstalled then Exit;
  WizardForm.StatusLabel.Caption := 'Installing Microsoft .NET Desktop Runtime...';
  ExtractTemporaryFile('windowsdesktop-runtime.exe');
  if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'),
      '/install /quiet /norestart /log "' + ExpandConstant('{tmp}\dahlia-dotnet.log') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    Result := 'Could not start the .NET installer: ' + SysErrorMessage(ExitCode);
    Exit;
  end;
  Log(Format('.NET installer exit code: %d', [ExitCode]));
  if (ExitCode = 3010) or (ExitCode = 1641) then begin
    NeedsRestart := True;
    Result := 'Restart Windows, then run Dahlia Setup again to finish installing.';
    Exit;
  end;
  if ExitCode <> 0 then begin
    Result := Format('.NET installation failed (exit code %d). See %s.', [ExitCode,
      ExpandConstant('{tmp}\dahlia-dotnet.log')]);
    Exit;
  end;
  if not RuntimeInstalled then begin
    Result := '.NET Desktop Runtime {#RuntimeVersion} x64 was not detected after installation.';
    Exit;
  end;
  if SolidWorksRunning then Result := CloseSolidWorksMessage;
end;

function InitializeUninstall: Boolean;
begin
  Result := not SolidWorksRunning;
  if not Result then begin
    Log(CloseSolidWorksMessage);
    SuppressibleMsgBox(CloseSolidWorksMessage, mbError, MB_OK, IDOK);
  end;
end;
