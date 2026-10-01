; nocat.farm setup - built by tools/package-release.ps1 with Inno Setup 7:
;
;   ISCC /DAppVersion=1.4.8 /DSourceDir=<staged release folder> tools\installer\nocatfarm.iss
;   ISCC /DAppVersion=1.4.8 /DPreview tools\installer\nocatfarm.iss      (the real screens, installs nothing -
;                                                                     /UPGRADE, /NEWER, /FRESH or /SHOTS to see the others)
;
; One screen in nocat.farm's own look - black, a thin grey border, the wordmark, monospace, [x] toggles, flat
; buttons - instead of Windows' wizard pages. It does the Windows side only (files, shortcuts, start with Windows,
; moving an existing copy over); phone, "from anywhere", updates and the firewall are chosen in the dashboard's
; first-run setup, which opens when it's done.
;
; Installs for the person running it - %LOCALAPPDATA%\Programs\nocat.farm, no admin - so nocat.farm can keep
; updating itself (Program Files would need an admin prompt for every update). The app, its settings and its logs
; all live in that one folder, exactly like the portable zip.
;
; Unattended: /VERYSILENT /DIR=... /MOVE=no /FROM=fresh|asf|idlemaster|other /STARTUP=no /DESKTOP=no /HIDDEN=yes /PORT=7300
; and to uninstall: unins000.exe /VERYSILENT /DELETEDATA=yes (accounts, settings, backups and logs too; default no)

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif

#define AppName "nocat.farm"
#define AppExe "nocatFarm.exe"

[Setup]
; Never change the AppId: it is how Windows (and Windows/InstallRecord.cs) knows this is the same app.
AppId={{89595F01-E60C-4593-9BA7-51B5A3A2F7C5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=reap.
AppPublisherURL=https://nocat.lol/nocatfarm
AppSupportURL=https://github.com/VisaHolder/nocatfarm/issues
AppUpdatesURL=https://github.com/VisaHolder/nocatfarm/releases
AppComments=Free. If you paid for it, you got scammed.
AppCopyright=reap.
VersionInfoCompany=reap.
VersionInfoDescription={#AppName} setup
VersionInfoProductName={#AppName}
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
; Windows 10 1607 or later - what .NET 10 itself runs on.
MinVersion=10.0.14393
DefaultDirName={autopf}\{#AppName}
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\src\NocatFarm\nocatFarm.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
; Never Inno's own language box: a language nocat.farm doesn't speak gets English.
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
CloseApplications=no
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\..\dist
#ifdef Preview
OutputBaseFilename=nocat.farm-v{#AppVersion}-setup-PREVIEW
Uninstallable=no
CreateUninstallRegKey=no
#else
OutputBaseFilename=nocat.farm-v{#AppVersion}-setup
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

#include "messages.iss"

[Files]
Source: "art\logo-128.bmp"; Flags: dontcopy
#ifndef Preview
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:Tagline}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:Tagline}"; Check: WantsDesktopIcon
#endif

#include "code.iss"
