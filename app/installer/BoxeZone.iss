; Boxe Zone installer. Built by build.ps1 (locally) and .github/workflows/release.yml (releases):
;   ISCC /DAppVersion=1.2.0-rc.1 /DNumericVersion=1.2.0 /DArch=x64 BoxeZone.iss
; Per-user install, no admin. Fixed output names so the site's "latest" link never changes.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

#define PublishDir "..\bin\Release\net10.0-windows10.0.19041.0\win-" + Arch + "\publish"

[Setup]
; Never change AppId: it is how a new version finds and replaces the installed one.
AppId={{5B7E0C4A-2D61-4B3F-9A7E-1C8D2F4B6A90}
AppName=Boxe Zone
AppVersion={#AppVersion}
AppVerName=Boxe Zone {#AppVersion}
AppPublisher=Lucas Venancio
AppPublisherURL=https://weblucasdev.github.io/BoxeZone/
AppSupportURL=https://weblucasdev.github.io/BoxeZone/
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=Boxe Zone
DefaultDirName={localappdata}\Programs\Boxe Zone
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=BoxeZone-Setup-{#Arch}
SetupIconFile=..\Assets\BoxeZone.ico
UninstallDisplayIcon={app}\BoxeZone.exe
UninstallDisplayName=Boxe Zone
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The glove instead of Inno Setup's default images, one file per DPI size (Setup picks the
; closest). Regenerated from Assets\BoxeZone.svg; sizes in app\docs\arquitetura.md.
WizardImageFile=images\wizard-large-202.png,images\wizard-large-269.png,images\wizard-large-336.png,images\wizard-large-403.png,images\wizard-large-430.png,images\wizard-large-498.png,images\wizard-large-534.png
WizardSmallImageFile=images\wizard-small-58.png,images\wizard-small-77.png,images\wizard-small-97.png,images\wizard-small-116.png,images\wizard-small-124.png,images\wizard-small-143.png,images\wizard-small-159.png
; Privacy summary shown before installing (project rule: the app contacts a system the user
; did not choose, GitHub, for the update check): per language, in [Languages].
ShowLanguageDialog=no
; Tasks start from their defaults on every run instead of the previous install's choices
; (the update-check choice lives in the app's Settings after the first start).
UsePreviousTasks=no
; The app lives in the tray and cancels window closes, so it is ended before files are replaced.
CloseApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
; Picked from the Windows display language (no dialog); any language other than Portuguese
; gets English, the first entry.
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "privacy-en.txt"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"; InfoBeforeFile: "privacidade.txt"

[CustomMessages]
en.DeleteUserData=Also delete Boxe Zone's preferences and connected accounts on this computer?
en.UpdateCheckGroup=Updates:
en.UpdateCheckTask=Check for new versions (asks GitHub once a day)
ptbr.DeleteUserData=Apagar também as preferências e as contas conectadas do Boxe Zone neste computador?
ptbr.UpdateCheckGroup=Atualizações:
ptbr.UpdateCheckTask=Verificar versões novas (consulta o GitHub uma vez por dia)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Checked on every install (UsePreviousTasks=no): an upgrade must not undo a choice made later in Settings.
Name: "updatecheck"; Description: "{cm:UpdateCheckTask}"; GroupDescription: "{cm:UpdateCheckGroup}"

[Registry]
; Unchecked: a one-time flag the app turns into its saved preference on the next start, then deletes.
Root: HKCU; Subkey: "Software\BoxeZone"; ValueType: dword; ValueName: "DisableUpdateCheck"; ValueData: 1; Tasks: not updatecheck

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files of an older version that the new one no longer ships. Only in a folder that is really
; ours: /DIR (or winget --location) can point {app} at a folder with the user's own files.
Type: filesandordirs; Name: "{app}\*"; Check: IsOwnAppDir

[UninstallDelete]
; The uninstaller removes the files but left the empty program folder behind.
Type: dirifempty; Name: "{app}"

[Icons]
; Same AppUserModelID as the app's notifications.
Name: "{autoprograms}\Boxe Zone"; Filename: "{app}\BoxeZone.exe"; AppUserModelID: "BoxeZone.App"
Name: "{autodesktop}\Boxe Zone"; Filename: "{app}\BoxeZone.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BoxeZone.exe"; Description: "{cm:LaunchProgram,Boxe Zone}"; Flags: nowait postinstall skipifsilent
; The app's automatic update runs this installer silently: open the app again afterwards.
Filename: "{app}\BoxeZone.exe"; Flags: nowait; Check: WizardSilent

[Code]
procedure CloseBoxeZone();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM BoxeZone.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

// The install folder is named "Boxe Zone" and already holds a previous Boxe Zone.
function IsOwnAppDir(): Boolean;
var
  AppDir: String;
begin
  AppDir := RemoveBackslash(ExpandConstant('{app}'));
  Result := (CompareText(ExtractFileName(AppDir), 'Boxe Zone') = 0) and FileExists(AppDir + '\BoxeZone.exe');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseBoxeZone();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    CloseBoxeZone();

  if CurUninstallStep = usPostUninstall then
  begin
    // Written by the app itself on every start.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'BoxeZone');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\AppUserModelId\BoxeZone.App');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\boxezone');
    // Written by this installer (update-check choice); normally already consumed by the app.
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\BoxeZone');

    if (not UninstallSilent) and
       (MsgBox(CustomMessage('DeleteUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{localappdata}\BoxeZone'), True, True, True);
  end;
end;
