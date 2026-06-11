# FreeFlume (Windows)

A fast, native **Windows** YT client for keyboard + mouse on big screens — no ads, no account, no
telemetry. Built with WinUI 3 / .NET, powered by [libmpv](https://mpv.io/) and
[yt-dlp](https://github.com/yt-dlp/yt-dlp). Libre (GPL-3.0-or-later).

> **Note:** This is a personal project, built largely with the help of **AI** for my own use — it isn't
> professionally maintained or supported. If you're a developer and want to fork it, build on it, or borrow
> any part of it, you're more than welcome (it's GPL-3.0-or-later).

## Download

Grab the latest from the [**Releases**](https://github.com/velkadyneslop/freeflume-windows/releases) page:

| | |
|---|---|
| **`FreeFlume.exe`** | Portable, all-in-one. Just run it — nothing to install, everything bundled (~218 MB). |
| **`FreeFlume-1.0.0-online-setup.exe`** | Installer (~36 MB). Per-user, **no admin**: adds a Start Menu shortcut + uninstaller, installs .NET if missing, and downloads the media tools on first install. |

> **"Windows protected your PC" (SmartScreen):** the app isn't code-signed, so Windows may warn you.
> Click **More info → Run anyway**. (See *Signing* below.)

**Requirements:** 64-bit Windows 10 (1809+) or Windows 11. The portable build needs nothing else. The
installer needs an internet connection the first time (to fetch the .NET runtime if absent + the media tools).

## Features

Search with YouTube-style filters, embedded mpv playback, quality / speed / caption controls,
Picture-in-Picture, a mini-player, an Up Next queue, SponsorBlock, chapters, storyboard seek preview,
subscriptions + a "What's New" feed, history, local playlists, downloads, configurable shortcuts,
paste-to-play URLs, live-channel indicators, hardware-decode auto-recovery, self-updating yt-dlp, and
rich video metadata. See the release notes for details.

## Building from source

Requires the **.NET 10 SDK** and Windows. The three native tools are **not** in the repo (too large for
GitHub) — download them into `third_party/win-x64/` first:

- `yt-dlp.exe` — https://github.com/yt-dlp/yt-dlp/releases/latest
- `ffmpeg.exe` — any recent static build (e.g. https://www.gyan.dev/ffmpeg/builds/)
- `libmpv-2.dll` — a recent libmpv build (e.g. https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)

Then:

```powershell
# Portable all-in-one (single self-contained exe)
dotnet publish src\FreeFlume\FreeFlume.csproj -c Release -r win-x64 -o artifacts\FreeFlume `
  -p:PublishSingleFile=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true

# Installers (needs Inno Setup 6.1+):  iscc packaging\FreeFlume-web.iss
```

## Signing

Releases are currently **unsigned**, so SmartScreen shows a one-time warning. See the project's signing
notes for the options (a code-signing certificate or Azure Trusted Signing).

## License

GPL-3.0-or-later. See [LICENSE](LICENSE). FreeFlume is not affiliated with YouTube or Google.
