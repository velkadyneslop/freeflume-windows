# FreeFlume

A native desktop YouTube client for Windows, inspired by
[NewPipe](https://github.com/TeamNewPipe/NewPipe) and reimagined for the desktop.

> Built with AI assistance. I made FreeFlume for my own personal use. If you're a
> developer and want to use it, or take parts of it for your own project, you're
> welcome to.

It's a small C#/WinUI 3 program written from scratch (it shares no code with NewPipe).
It ties together a few existing tools into a fast YouTube client for big screens,
keyboard and mouse:

- **mpv** for playback
- **yt-dlp** for search and extraction
- **Deno** for full-resolution playback

No ads, no account, no telemetry.

## Features

- Search with thumbnails, filters, and channels and playlists in the results
- mpv playback up to 4K with hardware decoding, quality selection, captions, and chapters
- Seek bar with thumbnail previews
- Picture-in-picture, fullscreen, and a mini-player
- SponsorBlock
- Channels with separate Videos and Streams tabs
- Subscriptions with channel feeds, watch history, local playlists, and downloads
- Native Windows look, follows your system light/dark theme

## Run the binary

The portable binary ships as `FreeFlume.exe` on the
[releases page](https://github.com/velkadyneslop/freeflume-windows/releases). Download
and run it — nothing to install, everything is bundled (mpv, yt-dlp, ffmpeg, and Deno):

It isn't code-signed, so SmartScreen may warn you the first time — click
**More info → Run anyway**.

Prefer an installer? `FreeFlume-<version>-online-setup.exe` is a per-user install (**no
admin**): it adds a Start Menu shortcut and uninstaller, installs the .NET runtime if
it's missing, and downloads the media tools on first install.

It needs 64-bit Windows 10 (1809+) or Windows 11. The portable build needs nothing
else; the installer needs an internet connection the first time.

Both downloads include Deno, so full-resolution playback works out of the box. YouTube
hides its high-resolution streams behind a JavaScript challenge that `yt-dlp` solves by
running the player code in Deno — without it, playback is capped to lower quality.

## Build

Requires the **.NET 10 SDK** on Windows. The native tools aren't in the repo (too large
for GitHub) — download them into `third_party/win-x64/` first:

- `yt-dlp.exe` — https://github.com/yt-dlp/yt-dlp/releases/latest
- `ffmpeg.exe` — any recent static build (e.g. https://www.gyan.dev/ffmpeg/builds/)
- `libmpv-2.dll` — a recent libmpv build (e.g. https://sourceforge.net/projects/mpv-player-windows/files/libmpv/)
- `deno.exe` — https://github.com/denoland/deno/releases/latest (the `...-pc-windows-msvc.zip`)

Then publish the portable all-in-one:

```powershell
dotnet publish src\FreeFlume\FreeFlume.csproj -c Release -r win-x64 -o artifacts\FreeFlume `
  -p:PublishSingleFile=true -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

The installers need [Inno Setup](https://jrsoftware.org/isinfo.php) 6: `iscc packaging\FreeFlume-web.iss`.

Licensed under **GPL-3.0-or-later** (see [LICENSE](LICENSE)). FreeFlume is not affiliated with YouTube or Google.
