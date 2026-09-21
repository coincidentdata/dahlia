const
  CloseSolidWorksMessage = 'Save your work and close SOLIDWORKS, then try again. Dahlia Setup will not close it for you.';

function SolidWorksRunning: Boolean;
var
  Locator, Services, Processes: Variant;
begin
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''SLDWORKS.exe''');
  Result := Processes.Count > 0;
end;

function CompatibleRuntimeVersion(const Version: String): Boolean;
var
  Installed, Minimum: Int64;
begin
  Result := False;
  if Copy(Version, 1, 4) <> '8.0.' then Exit;
  if not StrToVersion(Version, Installed) then Exit;
  if not StrToVersion('{#RuntimeVersion}', Minimum) then
    RaiseException('Invalid runtime prerequisite version.');
  Result := ComparePackedVersion(Installed, Minimum) >= 0;
end;

function FrameworkInstalled(const Framework: String): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  { The x64 .NET installer records its shared frameworks in the 32-bit registry view. }
  if not RegGetValueNames(HKLM32,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Framework, Versions) then Exit;
  for I := 0 to GetArrayLength(Versions) - 1 do
    if CompatibleRuntimeVersion(Versions[I]) then begin
      Result := True;
      Exit;
    end;
end;

function RuntimeInstalled: Boolean;
begin
  Result := FrameworkInstalled('Microsoft.NETCore.App') and
    FrameworkInstalled('Microsoft.WindowsDesktop.App');
end;
