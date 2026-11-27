; Ori per-user installer (Inno Setup 6, free/open source).
;
; Installs the self-contained single-file Ori.exe to
; %LOCALAPPDATA%\Programs\Ori (binaries only), adds Start menu and Add/Remove
; Programs (uninstall) entries, and leaves the launch-at-sign-in HKCU\...\Run
; entry to the app itself (Settings -> Startup). Ori's user data lives in the
; SEPARATE folder %LOCALAPPDATA%\Ori; this script never writes or removes it.
; No administrator rights are required and no secrets are embedded.
;
; Build (Inno Setup is OPTIONAL; see tools/installer/README.md):
;   iscc /DAppVersion=1.0.0 /DSourceExe="..\..\artifacts\Ori.exe" `
;        /DSourceUpdater="..\..\artifacts\Ori.Updater.exe" Ori.iss
; Or let scripts/build-installer.ps1 discover the published version.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceExe
  #define SourceExe "..\..\artifacts\Ori.exe"
#endif

#ifndef SourceUpdater
  #define SourceUpdater "..\..\artifacts\Ori.Updater.exe"
#endif

#ifndef SourceIcon
  #define SourceIcon "..\..\assets\Ori.ico"
#endif

#define AppName "Ori"
#define AppPublisher "DetectiveLon"
#define AppExeName "Ori.exe"
#define AppIconName "Ori.ico"
#define AppId "{{6E9D1F0A-8B4C-4C6E-9E2A-0B7F3C1D5A90}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/DetectiveLon/ori-wallpaper-manager
AppSupportURL=https://github.com/DetectiveLon/ori-wallpaper-manager/issues
AppUpdatesURL=https://github.com/DetectiveLon/ori-wallpaper-manager/releases/latest
; Binary location only. Keep it distinct from the app's data folder
; %LOCALAPPDATA%\Ori so uninstalling can never touch user data.
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\..\artifacts
OutputBaseFilename=ori-{#AppVersion}-setup
SetupIconFile={#SourceIcon}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
UninstallDisplayIcon={app}\{#AppIconName}
UninstallDisplayName={#AppName} {#AppVersion}
WizardStyle=modern
DisableProgramGroupPage=yes
; Do not require admin: this is a per-user install into LocalAppData.
AllowNoIcons=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; The published single-file EXE (self-contained, no .NET runtime needed).
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
; The separate updater must sit next to Ori.exe for the startup check to run.
Source: "{#SourceUpdater}"; DestDir: "{app}"; DestName: "Ori.Updater.exe"; Flags: ignoreversion
Source: "{#SourceIcon}"; DestDir: "{app}"; DestName: "{#AppIconName}"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists("..\..\artifacts\RELEASE-NOTES.txt")
Source: "..\..\artifacts\RELEASE-NOTES.txt"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{userprograms}\{#AppName}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIconName}"; Comment: "Ori wallpaper manager"
Name: "{userprograms}\{#AppName}\Uninstall Ori"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#AppIconName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppIconName}"; Tasks: desktopicon

; NOTE: the launch-at-sign-in HKCU\Software\Microsoft\Windows\CurrentVersion\Run
; entry is created and managed by the app itself (Settings -> Startup). The
; installer deliberately does NOT write it, so the two never disagree.

[Run]
; Offer to launch Ori after install, but do not run silently.
Filename: "{app}\{#AppExeName}"; Description: "Launch Ori"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
; Remove only files this installer created under {app} (= %LOCALAPPDATA%\Programs\Ori).
; Ori's user data (library, logs, schedules, feed, cache, ui-preferences.json)
; lives in the SEPARATE %LOCALAPPDATA%\Ori folder. No entry here targets that
; folder, so uninstall NEVER deletes user data.
Type: files; Name: "{app}\RELEASE-NOTES.txt"
; The updater swaps Ori.exe via a temporary .bak; remove any leftover copy.
Type: files; Name: "{app}\Ori.exe.bak"
