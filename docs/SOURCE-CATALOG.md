# FreeFlume — Linux source behavioral catalog

Reference for the C#/.NET 10 + WinUI 3 rewrite. Source studied: `velkadyne/FreeFlume-linux`
(C++/Qt6 Widgets, libmpv, yt-dlp, SQLite via Qt Sql). This documents *what the Linux app does*
so the Windows rewrite mirrors behavior. Keep in sync as we port.

## Stack mapping (Linux → Windows)

| Concern    | Linux                         | Windows (this project)                          |
|------------|-------------------------------|-------------------------------------------------|
| UI         | Qt6 Widgets                   | WinUI 3 (Windows App SDK), unpackaged           |
| Extraction | yt-dlp subprocess             | yt-dlp.exe subprocess (bundled)                 |
| Playback   | libmpv + QOpenGLWidget (GL)   | libmpv (mpv-2.dll) — **embedding TBD, see risk**|
| Storage    | Qt Sql / SQLite               | Microsoft.Data.Sqlite                           |
| Settings   | QSettings INI (apppaths)      | JSON settings in %LOCALAPPDATA%\velkadyne\FreeFlume |
| Icons      | QIcon::fromTheme (Breeze/gtk) | Segoe Fluent Icons + Segoe MDL2 fallback        |

## App shell / navigation
- Window title "FreeFlume", default 1100×720.
- Left sidebar (~210px) nav: **Search · Subscriptions · History · Playlists · Downloads · Settings**.
- Right: stacked content pages. Player floats as an overlay over content (full, or mini 420px bottom-right).
- CLI args: `--tab <i>`, `--play <url>`, or trailing text = search query.

## Paths & naming (upstream apppaths.h, synced 2026-06-03)
- All data/config routed through `apppaths`; org namespace = **velkadyne**.
- DB: Linux `<GenericData>/velkadyne/FreeFlume/freeflume.db` → **Windows `%LOCALAPPDATA%\velkadyne\FreeFlume\freeflume.db`** (+ `-wal`, `-shm`).
- Config: Linux `<GenericConfig>/velkadyne/FreeFlume.conf` → **Windows `%LOCALAPPDATA%\velkadyne\FreeFlume\settings.json`** (we use JSON, not INI).
- Downloads default: **`%USERPROFILE%\Downloads\FreeFlume`** (upstream changed from plain Downloads).
- **User-facing text says "YT", not "YouTube"** — follow in all WinUI strings. (yt-dlp args / internal comments unaffected.)
- apppaths.h legacy migration is Linux-only; not needed for the fresh Windows app.

## SQLite schema (file in app data dir)
- `history(url PK, title, channel, thumbnail, duration INT, watched_at INT, position INT DEFAULT 0, completed INT DEFAULT 0)`
- `subscriptions(channel_url PK, channel_name, added_at INT, avatar, channel_id)`
- `playlists(id PK AUTOINC, name, created_at INT)`
- `playlist_items(id PK AUTOINC, playlist_id FK->playlists ON DELETE CASCADE, url, title, channel, thumbnail, duration INT, added_at INT, position INT)`
- `search_history(query PK, searched_at INT)`
- `PRAGMA foreign_keys = ON`. Migrations are idempotent `ALTER TABLE ADD COLUMN` at startup (errors ignored).

## Core data structs
- **SearchResult**: id, url, title, channel, durationSeconds, viewCount(-1=unknown), thumbnailUrl, isLive, kind(Video/Short/Channel/Playlist), published.
- **VideoDetails**: url, title, channel, channelUrl, description, uploadDate(YYYYMMDD), durationSeconds, viewCount, likeCount, thumbnailUrl, Storyboard, Chapter[].
- **Storyboard**: tileWidth/Height, rows, columns, fragments[{url,duration}] — seek-bar hover preview sprites.
- **Chapter**: startSeconds, title.
- **WatchProgress**: position, duration, completed.

## yt-dlp integration (exact args)
Executable `yt-dlp` on PATH. Separate processes for search/details/playlist. Output = NDJSON unless noted.
- **Search**: `--flat-playlist --dump-json --no-warnings --ignore-errors --playlist-start N --playlist-end M <target>`
  where target = `ytsearchN:<query>` OR `https://www.youtube.com/results?search_query=<q>[&sp=<base64>]`.
  - Filters encoded into YouTube `sp=` protobuf (base64): sort, uploadDate, type, duration, HD, subtitles, live, 4K.
- **Channel**: same flat args; target normalized to a `/videos` (or list=/shorts/streams/playlists) tab.
- **Search-in-channel**: `<channel>/search?query=<q>` with flat args.
- **Video details**: `--dump-single-json --no-warnings --no-playlist --extractor-args youtube:player_client=default,android <url>`.
  Parse formats[] for storyboard (format_note=="storyboard" or id starts "sb", pick ~160px wide); chapters[] {start_time,title}.
- **Playlist items**: flat args, filter to Video/Short only.
- JSON keys consumed: id, url/webpage_url, title, channel/uploader, duration, view_count, like_count,
  thumbnails[]/thumbnail, live_status, ie_key, upload_date, description, channel_url/uploader_url, formats, chapters, playlist_count.

## Playback (libmpv) — the hard part
- Linux: `mpv_create`/`initialize`, **render API** `MPV_RENDER_API_TYPE_OPENGL` into a QOpenGLWidget FBO (FLIP_Y),
  wakeup/update callbacks marshalled to UI thread, `mpv_observe_property` + `mpv_wait_event` pump.
- Init opts: `ytdl=yes`, `script-opts=ytdl_hook-ytdl_path=yt-dlp`, `vo=libmpv`, `hwdec=auto-safe|no`, `keep-open=yes`, `sub-auto=no`.
- Load: `loadfile <url>`; mpv's ytdl hook calls yt-dlp; `ytdl-format` sets quality; seek applied on FILE_LOADED.
- Observed props: time-pos, duration, pause, mute, media-title, sid, eof-reached, dwidth/dheight, container-fps.
- Quality presets → ytdl-format strings (Best/Auto/1080/720/480/360, each `bestvideo[height<=H]+bestaudio/best`).
- Subtitles: `sid` cycle; styling via sub-font-size/color/border/bold/back-color/font/shadow; caption fetch via `ytdl-raw-options`.
- **Resume**: resumable if pos>5s && !completed && pos<duration-15s. Modes: resume/ask(12s banner)/start. Autosave every 5s.
- **SponsorBlock**: GET `https://sponsor.ajay.app/api/skipSegments/<sha256(videoId)[:4]>` ?categories=[...]&actionTypes=["skip"],
  UA "FreeFlume/1.0", filter exact videoID, 3 retries @700ms. Per-category Auto-skip/Manual/Disabled. Colored bands on seek bar.
- **PiP**: second mpv instance in always-on-top window (can't reparent live GL surface).
- Keyboard (rebindable unless noted): Space play/pause, ←/→ ∓5s, ↑/↓ vol, M mute, C captions cycle, F fullscreen,
  R loop, I info, Q queue, P/N prev/next, Shift+←/→ frame step, +/− vol; **Enter** SB skip/revert (fixed), **Esc** exit FS/back (fixed).

## Downloads
- One yt-dlp process at a time, rest queued. Common: `--no-playlist --no-warnings --newline --extractor-args youtube:player_client=default,android --progress-template "FFDL %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s"`.
- Video: codec-specific format selector (AV1/VP9/H264/Best) + `[height<=N]` cap + `--merge-output-format <container>`, optional `--embed-subs`.
- Audio: `-x --audio-format <mp3/m4a/opus/vorbis> --audio-quality 0`.
- Subs: `--skip-download --write-subs --convert-subs srt`.
- Output template `%(title)s.%(ext)s`; folder = `%USERPROFILE%\Downloads\FreeFlume` or setting. Progress parsed from `FFDL ` lines.

## Pages (UX)
- **Search**: filter bar (Upload date/Type/Duration/Sort/Features HD·4K·Subs·Live) + result list (120×68 thumb, title, "Channel · Duration · Views") + pagination + collapsible detail pane (thumb, title, clickable channel, views·likes·duration·date, Play/Subscribe/Add-to-playlist, linkified description). Context menus per result kind. Shorts filtered out.
- **Subscriptions**: channel list (avatar, name, hover-reveal unsub) + "What's New" RSS feed via `https://www.youtube.com/feeds/videos.xml?channel_id=<UC…>` (parallel fetch, newest first, generation-counter cancels stale). Import (NewPipe/FreeTube/Takeout/OPML/URLs) + Export.
- **History**: list newest-first, Clear button, per-row remove + context menu.
- **Playlists**: playlist list "Name (count)" + New/Delete; right = items VideoList with drag-reorder + remove.
- **Settings**: Appearance(scheme/style), Playback(quality/volume0-130/hwdec/autoplay/mini/resume), Privacy(remember watch+search, clear), SponsorBlock(enable + per-category mode), Downloads(folder/maxHeight/embedSubs), Screenshots(folder/format png·jxl·jpg), Shortcuts(rebind), Search(limit5-100/include channels·playlists), Subtitles(lang/auto/translate/font/size/color/outline/shadow/bold/bg), Backends(yt-dlp+mpv versions, data folder — read-only).

## Settings keys (persisted)
appearance/colorScheme(system), appearance/style(native); playback/quality(Auto), volume(100), hwdec(true), autoplayNext(true), miniPlayer(true), resumeMode(resume); history/rememberWatch(true), rememberSearch(true); sponsorblock/enabled(false), sponsorblock/mode/<cat>(int); downloads/folder, maxHeight(0), embedSubs(false); screenshot/folder, format(png); shortcuts/<action>(int); search/limit(20), includeChannels(true), includePlaylists(true); subtitles/language(en), includeAuto(false), translateTo(""), translateEnabled(false), fontSize(55), color(#FFFFFF), outline(3), bold(false), background(false), font(""), shadowOffset(0), shadowColor(#FF000000).

## Theme
- Schemes: System / Light (#f5f5f5 win, #1a1a1a text, #2a82da accent) / Dark (#353535 win, #eaeaea text, #232323 base, #2a82da accent). Follows system light/dark; overridable. WinUI: use ElementTheme + system accent; honor app-level override.
