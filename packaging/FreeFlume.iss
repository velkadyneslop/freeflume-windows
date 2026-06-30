; FreeFlume — per-user (non-admin) installer.
; Build with Inno Setup 6:  iscc packaging\FreeFlume.iss
; Produces artifacts\installer\FreeFlume-<ver>-setup.exe. Installs the self-contained single-file exe to
; %LOCALAPPDATA%\Programs\FreeFlume with a Start Menu shortcut + uninstaller. No admin / UAC required.

#define MyAppName "FreeFlume"
#define MyAppVersion "1.0.4"
#define MyAppPublisher "velkadyne"
#define MyAppExeName "FreeFlume.exe"
#define MyAppUrl "https://github.com/velkadyneslop/freeflume-windows"

[Setup]
; Keep AppId stable across versions so upgrades/uninstall track correctly.
AppId={{B7E9C2A4-5D3F-4A1E-9C8B-2F6A1D4E7C09}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}

; --- Non-admin, per-user install (never prompts for UAC) ---
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

; --- Output ---
OutputDir=..\artifacts\installer
OutputBaseFilename=FreeFlume-{#MyAppVersion}-setup
SetupIconFile=..\src\FreeFlume\Assets\FreeFlume.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; --- 64-bit only, Windows 10 1809+ (matches the app) ---
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole app is one self-contained file — just copy it.
Source: "..\artifacts\FreeFlume\FreeFlume.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

; Note: user data in %LOCALAPPDATA%\velkadyne\FreeFlume (history, settings, playlists) is intentionally
; left in place on uninstall.
