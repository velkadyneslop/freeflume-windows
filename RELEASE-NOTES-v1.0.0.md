# FreeFlume for Windows — v1.0.0

The first release of FreeFlume for Windows — a fast, native YT client with no ads, no account, and no
telemetry.

## Downloads
- **`FreeFlume.exe`** — portable all-in-one (~218 MB), nothing to install.
- **`FreeFlume-1.0.0-online-setup.exe`** — per-user installer (~36 MB), no admin; installs .NET if
  missing and fetches the media tools on install.

> Unsigned, so SmartScreen shows a one-time **More info → Run anyway**.

## Features
Search (YouTube-style filters + protobuf `sp=`), embedded **mpv** playback, quality / speed / captions /
loop / screenshot / chapters controls, **Picture-in-Picture** (separate window) and **mini-player**, the
**Up Next** queue, **SponsorBlock**, **storyboard** seek-preview, **subscriptions** + "What's New" feed
with import/export, **history** + watch-progress, local **playlists**, **downloads**, idle inhibition, full
subtitle styling, and configurable keyboard shortcuts.

## Also includes
- **Full metadata on every row** — channel · duration · views · upload date everywhere (dates filled in
  the background + cached for search).
- **Paste-to-play** YouTube URLs in the search bar.
- **Live-channel indicator** (red ring) in Search & Subscriptions; **all live streams pinned to the top**
  of a channel view.
- **Self-updating yt-dlp** (daily, toggleable) so playback keeps working as YouTube changes.
- **Hardware-decode auto-recovery** — detects a hung GPU decoder and falls back to software seamlessly.
- **HiDPI video sharpness**, **remembered PiP size**, **1440p/4K** quality presets.
- Fullscreen **cursor auto-hide**, prev/next buttons, crash-resilient UI, in-app **version display**.

## Notes
- **Requirements:** 64-bit Windows 10 (1809+) / Windows 11.
- The installer needs internet on first run; the portable build needs nothing.
- Not affiliated with YouTube or Google. GPL-3.0-or-later.
