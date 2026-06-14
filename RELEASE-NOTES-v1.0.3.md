# FreeFlume for Windows — v1.0.3

A fast, native YT client with no ads, no account, and no telemetry. This release brings the Windows app
to full feature parity with the Linux version (and aligns the version number to match).

## What's new
- **Channel Videos / Streams tabs** — a channel view now has two tabs: **Videos** (uploads, with any
  live/upcoming stream pinned on top) and **Streams** (the full past + live stream list). Your choice
  persists as you page through.
- **Full-resolution playback** — bundles **Deno** so yt-dlp can solve YouTube's "nsig" JavaScript gate;
  videos that previously capped at low resolution now play up to 1080p / 1440p / 4K. (The online installer
  fetches Deno automatically, no admin needed.)
- **Opt-in search suggestions** — turn on **Settings → Search → Enable search suggestions** for live YT
  query suggestions as you type. Off by default; when off, the search box uses your local history only and
  makes no network requests.
- **Unified date format** — upload dates now read the same everywhere (lists, the detail pane, and the
  in-player info panel).

## Downloads
- **`FreeFlume.exe`** — portable all-in-one, nothing to install.
- **`FreeFlume-1.0.3-online-setup.exe`** — per-user installer (no admin); installs .NET if missing and
  fetches the media tools (yt-dlp, ffmpeg, libmpv, Deno) on install.

> Unsigned, so SmartScreen shows a one-time **More info → Run anyway**.

## Notes
- **Requirements:** 64-bit Windows 10 (1809+) / Windows 11.
- The installer needs internet on first run; the portable build needs nothing.
- Not affiliated with YouTube or Google. GPL-3.0-or-later.
