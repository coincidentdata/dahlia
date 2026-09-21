[Setup]
AppName=Dahlia prerequisite checks
AppVersion=1
CreateAppDir=no
Uninstallable=no
PrivilegesRequired=lowest
SetupArchitecture=x64
OutputDir=..\obj\tests
OutputBaseFilename=prerequisites-test
SetupLogging=yes

[Code]
#include "..\prerequisites.iss"

procedure Check(const Version: String; Expected: Boolean);
begin
  if CompatibleRuntimeVersion(Version) <> Expected then
    RaiseException('Incorrect prerequisite decision for ' + Version);
end;

function InitializeSetup: Boolean;
begin
  Check('8.0.4', False);
  Check('8.0.30', False);
  Check('8.0.31', True);
  Check('8.0.32', True);
  Check('8.0.100', True);
  Check('9.0.31', False);
  Check('10.0.0', False);
  Check('8.0.31-preview.1', False);
  Check('8.0.invalid', False);
  Log(Format('Live runtime prerequisite satisfied: %d', [Ord(RuntimeInstalled)]));
  Log(Format('SOLIDWORKS running: %d', [Ord(SolidWorksRunning)]));
  Log('DAHLIA_PREREQUISITES_PASSED');
  Result := False;
end;
