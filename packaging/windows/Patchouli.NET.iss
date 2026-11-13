#ifndef SourceDir
  #error SourceDir must point to the published application directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\.tmp\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.4.1"
#endif
#ifndef AppId
  #define AppId "{{DCBB7F21-2751-4C90-A9B4-9459523CFF70}"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "Patchouli.NET-" + AppVersion + "-win-x64-setup"
#endif

#define AppName "Patchouli.NET"
#define AppPublisher "Patchouli.NET"
#define AppExeName "Patchouli.UI.exe"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\..\src\Patchouli.UI\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
Name: "addtopath"; Description: "Add patchouli-cli to the user PATH"; GroupDescription: "Command line tool:"; Flags: unchecked
Name: "registerprotocol"; Description: "处理 patchouli:// 文献虚拟链接"; GroupDescription: "系统集成："

[Registry]
Root: HKCU; Subkey: "Software\Classes\patchouli"; ValueType: string; ValueName: "PatchouliProtocolOwner"; ValueData: "net.patchouli.app"; Tasks: registerprotocol; Check: ShouldRegisterProtocol
Root: HKCU; Subkey: "Software\Classes\patchouli"; ValueType: string; ValueData: "URL:Patchouli Literature Link"; Tasks: registerprotocol; Check: ShouldRegisterProtocol
Root: HKCU; Subkey: "Software\Classes\patchouli"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: registerprotocol; Check: ShouldRegisterProtocol
Root: HKCU; Subkey: "Software\Classes\patchouli\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"; Tasks: registerprotocol; Check: ShouldRegisterProtocol
Root: HKCU; Subkey: "Software\Classes\patchouli\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" --open-uri ""%1"""; Tasks: registerprotocol; Check: ShouldRegisterProtocol

; Wipe the migrations folder on upgrade so removed SQL files cannot survive side-by-side installs.
[InstallDelete]
Type: filesandordirs; Name: "{app}\migrations"
; Retire the duplicated dependency/runtime trees from earlier packages.
Type: filesandordirs; Name: "{app}\cli"
Type: filesandordirs; Name: "{app}\extension-compiler"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function ShouldRegisterProtocol: Boolean;
var
  Disabled: Cardinal;
  Owner: string;
begin
  Result := not (RegQueryDWordValue(HKCU, 'Software\Patchouli.NET', 'ProtocolRegistrationDisabled', Disabled) and (Disabled = 1));
  if RegKeyExists(HKCU, 'Software\Classes\patchouli') then
    Result := Result and RegQueryStringValue(HKCU, 'Software\Classes\patchouli', 'PatchouliProtocolOwner', Owner) and (Owner = 'net.patchouli.app');
end;

procedure RemoveProtocolRegistration;
var
  Owner, Command: string;
begin
  if RegQueryStringValue(HKCU, 'Software\Classes\patchouli', 'PatchouliProtocolOwner', Owner) and
     (Owner = 'net.patchouli.app') and
     RegQueryStringValue(HKCU, 'Software\Classes\patchouli\shell\open\command', '', Command) and
     (CompareText(Command, ExpandConstant('"{app}\{#AppExeName}" --open-uri "%1"')) = 0) then
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\patchouli');
end;

procedure SetPathEntry(AddPath, MigrateLegacy: Boolean);
var
  PathValue, Entry, LegacyEntry, NewPath, Part, Remainder: string;
  Position, Separator: Integer;
begin
  Entry := ExpandConstant('{app}');
  LegacyEntry := ExpandConstant('{app}\cli');
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', PathValue) then
    PathValue := '';
  NewPath := '';
  Remainder := PathValue;
  while Remainder <> '' do
  begin
    Separator := Pos(';', Remainder);
    if Separator = 0 then
    begin
      Part := Remainder;
      Remainder := '';
    end
    else
    begin
      Part := Copy(Remainder, 1, Separator - 1);
      Remainder := Copy(Remainder, Separator + 1, Length(Remainder) - Separator);
    end;
    if MigrateLegacy and ((CompareText(Part, Entry) = 0) or (CompareText(Part, LegacyEntry) = 0)) then
      AddPath := True;
    if (CompareText(Part, Entry) <> 0) and (CompareText(Part, LegacyEntry) <> 0) and (Trim(Part) <> '') then
      NewPath := NewPath + Part + ';';
  end;
  if AddPath then
    NewPath := NewPath + Entry + ';';
  if NewPath <> '' then
    NewPath := Copy(NewPath, 1, Length(NewPath) - 1);
  if NewPath <> PathValue then
  begin
    if RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath) then
      SendMessage(HWND_BROADCAST, $001A, 0, 0);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SetPathEntry(WizardIsTaskSelected('addtopath'), True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveProtocolRegistration;
    SetPathEntry(False, False);
  end;
end;
