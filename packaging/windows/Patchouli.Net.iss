#ifndef SourceDir
  #error SourceDir must point to the published application directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif

#define AppName "Patchouli.Net"
#define AppPublisher "Patchouli.Net"
#define AppExeName "Patchouli.UI.exe"

[Setup]
AppId={{DCBB7F21-2751-4C90-A9B4-9459523CFF70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Patchouli.Net-{#AppVersion}-win-x64-setup
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
Name: "addtopath"; Description: "Add patchouli-cli to the user PATH"; GroupDescription: "Command line tool:"; Flags: unchecked

; Wipe the migrations folder on upgrade so removed SQL files cannot survive side-by-side installs.
[InstallDelete]
Type: filesandordirs; Name: "{app}\migrations"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  CliRelativeDir = 'cli';

procedure SetPathEntry(AddPath: Boolean);
var
  PathValue, Entry, NewPath, Part, Remainder: string;
  Position, Separator: Integer;
begin
  Entry := ExpandConstant('{app}\' + CliRelativeDir);
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
    if (CompareText(Part, Entry) <> 0) and (Trim(Part) <> '') then
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
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    SetPathEntry(True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    SetPathEntry(False);
end;
