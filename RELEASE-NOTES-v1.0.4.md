# FreeFlume for Windows — v1.0.4

A fast, native YT client with no ads, no account, and no telemetry. This is a fix release that keeps
the Windows app at full parity with the Linux version (same fixes, same version number).

## What's fixed
- **Long playlists no longer stop at item ~200.** Paging deep into a big playlist (e.g. a 1,000-video
  list) used to show "No videos found" from page 6 onward, because yt-dlp's anonymous extraction stops
  past ~200 items. FreeFlume now talks to YouTube's playlist API directly and jumps straight to any
  page, so the whole playlist is reachable — and "Play all" now queues the entire list, not just the
  first chunk. Still fully anonymous: no account, cookies, or sign-in.
- **Channel pager no longer hides later pages.** A channel's first page always shows working
  pagination so you can advance through its full back catalogue, instead of a stream count
  occasionally capping it to a single page.

## Downloads
- **`FreeFlume.exe`** — portable all-in-one, nothing to install.
- **`FreeFlume-1.0.4-online-setup.exe`** — per-user installer (no admin); installs .NET if missing and
  fetches the media tools (yt-dlp, ffmpeg, libmpv, Deno) on install.

> Unsigned, so SmartScreen shows a one-time **More info → Run anyway**.

## Updating
- Already on v1.0.3? Use **Settings → Backends → Check for updates** to update in place.
- The portable build swaps itself and restarts; the installed build runs the new setup.

## Notes
- **Requirements:** 64-bit Windows 10 (1809+) / Windows 11.
- The installer needs internet on first run; the portable build needs nothing.
- Not affiliated with YouTube or Google. GPL-3.0-or-later.
