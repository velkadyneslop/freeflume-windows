; FreeFlume — "web" / downloader installer (per-user, non-admin).
; Ships the lean framework-dependent app (artifacts\FreeFlume-lean: app + bundled Windows App SDK, but
; NOT .NET and NOT the media tools). At install it: checks for the .NET 10 Desktop Runtime and installs
; it from Microsoft if missing, then downloads yt-dlp / ffmpeg / libmpv into the install folder.
;
; Build with Inno Setup 6.1+:  iscc packaging\FreeFlume-web.iss
;
; >>> BEFORE RELEASE: set MyRepoUrl below to your GitHub repo, and upload these as assets on the release
;     tagged "v{version}":  ffmpeg.exe, libmpv-2.dll  (yt-dlp + .NET come from official sources).

#define MyAppName "FreeFlume"
#define MyAppVersion "1.0.3"
#define MyAppPublisher "velkadyne"
#define MyAppExeName "FreeFlume.exe"
#define MyRepoUrl "https://github.com/velkadyneslop/freeflume-windows"

; Download sources
#define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define YtDlpUrl  "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
#define ToolsBaseUrl MyRepoUrl + "/releases/download/v" + MyAppVersion

[Setup]
AppId={{C1B6F0E2-3A47-4D9E-8E21-9D5A7C3F00FF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyRepoUrl}

PrivilegesRequired=lowest
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

OutputDir=..\artifacts\installer
OutputBaseFilename=FreeFlume-{#MyAppVersion}-online-setup
SetupIconFile=..\src\FreeFlume\Assets\FreeFlume.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The lean app (managed code + bundled Windows App SDK; no .NET runtime, no media tools).
Source: "..\artifacts\FreeFlume-lean\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; Media tools downloaded to {tmp} during the wizard, then placed next to the exe.
Source: "{tmp}\yt-dlp.exe";    DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\ffmpeg.exe";    DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\libmpv-2.dll";  DestDir: "{app}"; Flags: external ignoreversion
Source: "{tmp}\deno.exe";      DestDir: "{app}"; Flags: external ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}";  Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet: Boolean;

{ True if a .NET 10 Desktop Runtime (Microsoft.WindowsDesktop.App 10.x) folder exists under dotnetRoot. }
function HasDesktop10(dotnetRoot: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if dotnetRoot = '' then Exit;
  if FindFirst(dotnetRoot + '\shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ Match exactly where the framework-dependent apphost looks for the runtime: $DOTNET_ROOT(_X64), the
  HKLM-registered install location, then the default Program Files\dotnet. (A user-profile ~/.dotnet is
  deliberately NOT counted — the apphost won't use it unless DOTNET_ROOT points there, so the app would
  fail to launch.) }
function DotNetDesktopInstalled(): Boolean;
var
  regLoc: String;
begin
  Result := HasDesktop10(GetEnv('DOTNET_ROOT_X64'))
         or HasDesktop10(GetEnv('DOTNET_ROOT'))
         or HasDesktop10(ExpandConstant('{commonpf}\dotnet'));
  if (not Result) and RegQueryStringValue(HKLM,
      'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', regLoc) then
    Result := HasDesktop10(regLoc);
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ Runs right before file copy, in BOTH interactive and silent installs. Return '' on success or an
  error message to abort. We download the .NET runtime (if missing) + the media tools here. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  NeedDotNet := not DotNetDesktopInstalled();

  DownloadPage.Clear;
  if NeedDotNet then
    DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '');
  DownloadPage.Add('{#YtDlpUrl}', 'yt-dlp.exe', '');
  DownloadPage.Add('{#ToolsBaseUrl}/ffmpeg.exe', 'ffmpeg.exe', '');
  DownloadPage.Add('{#ToolsBaseUrl}/libmpv-2.dll', 'libmpv-2.dll', '');
  { Deno: unlocks full-resolution playback (yt-dlp's nsig solver). Hosted as a release asset like ffmpeg. }
  DownloadPage.Add('{#ToolsBaseUrl}/deno.exe', 'deno.exe', '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      Result := 'Download failed: ' + GetExceptionMessage;
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  { Install the .NET runtime if it was missing. This prompts UAC (Microsoft's runtime installer needs
    admin) — the only elevation in the whole flow; FreeFlume itself never needs it. }
  if NeedDotNet then
  begin
    if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime.exe'), '/install /quiet /norestart', '',
                SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      Result := 'Could not start the .NET 10 Desktop Runtime installer.'
    else if (ResultCode <> 0) and (ResultCode <> 3010) then  { 3010 = success, reboot required }
      Result := 'The .NET 10 Desktop Runtime install did not complete (code ' + IntToStr(ResultCode) + ').';
  end;
end;

// Note: user data in %LOCALAPPDATA%\velkadyne\FreeFlume is left in place on uninstall.
