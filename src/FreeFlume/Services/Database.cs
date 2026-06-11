// FreeFlume — local persistence (SQLite). Mirrors the Linux schema (docs/SOURCE-CATALOG.md).
// Author: velkadyne
// History is implemented now; subscriptions/playlists tables are created up front and
// their methods will be filled in as those features are built.

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using FreeFlume.Models;

namespace FreeFlume.Services;

public sealed class Database
{
    /// <summary>App-wide shared instance over the default database file.</summary>
    public static Database Shared { get; } = new(AppPaths.DatabaseFile());

    private readonly string _connectionString;

    public event Action? HistoryChanged;
    public event Action? PlaylistsChanged;
    public event Action? SubscriptionsChanged;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        CreateSchema();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA foreign_keys = ON;");
        Exec(c, "PRAGMA busy_timeout = 3000;");   // tolerate concurrent writers (background metadata enrichment)
        return c;
    }

    private void CreateSchema()
    {
        using var c = Open();
        Exec(c, @"CREATE TABLE IF NOT EXISTS history(
                    url TEXT PRIMARY KEY, title TEXT, channel TEXT, thumbnail TEXT,
                    duration INTEGER, watched_at INTEGER,
                    position INTEGER DEFAULT 0, completed INTEGER DEFAULT 0,
                    view_count INTEGER DEFAULT -1, published INTEGER DEFAULT 0);");
        Exec(c, @"CREATE TABLE IF NOT EXISTS subscriptions(
                    channel_url TEXT PRIMARY KEY, channel_name TEXT, added_at INTEGER,
                    avatar TEXT, channel_id TEXT);");
        Exec(c, @"CREATE TABLE IF NOT EXISTS playlists(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, created_at INTEGER);");
        Exec(c, @"CREATE TABLE IF NOT EXISTS playlist_items(
                    id INTEGER PRIMARY KEY AUTOINCREMENT, playlist_id INTEGER, url TEXT,
                    title TEXT, channel TEXT, thumbnail TEXT, duration INTEGER,
                    added_at INTEGER, position INTEGER,
                    view_count INTEGER DEFAULT -1, published INTEGER DEFAULT 0,
                    FOREIGN KEY(playlist_id) REFERENCES playlists(id) ON DELETE CASCADE);");
        Exec(c, @"CREATE TABLE IF NOT EXISTS search_history(
                    query TEXT PRIMARY KEY, searched_at INTEGER);");
        // Cache of per-video metadata (views + upload date) fetched lazily for list rows (e.g. search,
        // which the fast listing returns without a date). Keyed by watch URL.
        Exec(c, @"CREATE TABLE IF NOT EXISTS video_meta(
                    url TEXT PRIMARY KEY, view_count INTEGER DEFAULT -1, published INTEGER DEFAULT 0, fetched_at INTEGER);");

        // Migrate older DBs that predate the view_count/published columns (ignore if already present).
        foreach (var t in new[] { "history", "playlist_items" })
        {
            TryExec(c, $"ALTER TABLE {t} ADD COLUMN view_count INTEGER DEFAULT -1;");
            TryExec(c, $"ALTER TABLE {t} ADD COLUMN published INTEGER DEFAULT 0;");
        }
    }

    private static void TryExec(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
    {
        try { Exec(c, sql); } catch { /* column already exists */ }
    }

    // ---------------- History ----------------

    public void AddHistory(SearchResult r)
    {
        if (string.IsNullOrEmpty(r.Url)) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO history(url,title,channel,thumbnail,duration,watched_at,view_count,published)
                            VALUES($url,$title,$channel,$thumb,$dur,$now,$views,$pub)
                            ON CONFLICT(url) DO UPDATE SET
                              title=excluded.title, channel=excluded.channel,
                              thumbnail=excluded.thumbnail, duration=excluded.duration,
                              watched_at=excluded.watched_at,
                              view_count=MAX(history.view_count, excluded.view_count),
                              published=MAX(history.published, excluded.published);";
        Bind(cmd, "$url", r.Url);
        Bind(cmd, "$title", r.Title);
        Bind(cmd, "$channel", r.Channel);
        Bind(cmd, "$thumb", r.ThumbnailUrl);
        Bind(cmd, "$dur", r.DurationSeconds);
        Bind(cmd, "$now", Now());
        Bind(cmd, "$views", r.ViewCount);
        Bind(cmd, "$pub", r.Published);
        cmd.ExecuteNonQuery();
        HistoryChanged?.Invoke();
    }

    public List<SearchResult> History(int limit = 200)
    {
        var list = new List<SearchResult>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT url,title,channel,thumbnail,duration,view_count,published
                            FROM history ORDER BY watched_at DESC LIMIT $limit;";
        Bind(cmd, "$limit", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new SearchResult
            {
                Url = rd.GetString(0),
                Title = rd.IsDBNull(1) ? "" : rd.GetString(1),
                Channel = rd.IsDBNull(2) ? "" : rd.GetString(2),
                ThumbnailUrl = rd.IsDBNull(3) ? "" : rd.GetString(3),
                DurationSeconds = rd.IsDBNull(4) ? 0 : rd.GetInt64(4),
                ViewCount = rd.IsDBNull(5) ? -1 : rd.GetInt64(5),
                Published = rd.IsDBNull(6) ? 0 : rd.GetInt64(6),
                Kind = ResultKind.Video,
            });
        }
        return list;
    }

    public void ClearHistory()
    {
        using var c = Open();
        Exec(c, "DELETE FROM history;");
        HistoryChanged?.Invoke();
    }

    public void RemoveHistoryItem(string url)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM history WHERE url=$url;";
        Bind(cmd, "$url", url);
        cmd.ExecuteNonQuery();
        HistoryChanged?.Invoke();
    }

    /// <summary>Set WatchedFraction on each result from the history table (for thumbnail progress bars).</summary>
    public void FillWatchProgress(IReadOnlyList<SearchResult> items)
    {
        if (items.Count == 0) return;
        var map = new Dictionary<string, double>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT url,position,duration,completed FROM history WHERE position > 0;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                string url = rd.GetString(0);
                long pos = rd.IsDBNull(1) ? 0 : rd.GetInt64(1);
                long dur = rd.IsDBNull(2) ? 0 : rd.GetInt64(2);
                bool done = !rd.IsDBNull(3) && rd.GetInt64(3) != 0;
                double frac = done ? 1.0 : (dur > 0 ? Math.Clamp((double)pos / dur, 0, 1) : 0);
                if (frac > 0) map[url] = frac;
            }
        }
        foreach (var r in items)
            if (map.TryGetValue(r.Url, out var f)) r.WatchedFraction = f;
    }

    // ---------------- Search history ----------------

    public void AddSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO search_history(query,searched_at) VALUES($q,$now)
                            ON CONFLICT(query) DO UPDATE SET searched_at=excluded.searched_at;";
        Bind(cmd, "$q", query.Trim());
        Bind(cmd, "$now", Now());
        cmd.ExecuteNonQuery();
    }

    public List<string> SearchHistory(int limit = 25)
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT query FROM search_history ORDER BY searched_at DESC LIMIT $limit;";
        Bind(cmd, "$limit", limit);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetString(0));
        return list;
    }

    public void ClearSearchHistory()
    {
        using var c = Open();
        Exec(c, "DELETE FROM search_history;");
    }

    // ---------------- Subscriptions ----------------

    public void Subscribe(string channelName, string channelUrl, string channelId, string avatarUrl = "")
    {
        if (string.IsNullOrEmpty(channelUrl)) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO subscriptions(channel_url,channel_name,added_at,avatar,channel_id)
                            VALUES($url,$name,$now,$avatar,$cid)
                            ON CONFLICT(channel_url) DO UPDATE SET
                              channel_name=excluded.channel_name, avatar=excluded.avatar,
                              channel_id=excluded.channel_id;";
        Bind(cmd, "$url", channelUrl);
        Bind(cmd, "$name", channelName);
        Bind(cmd, "$now", Now());
        Bind(cmd, "$avatar", avatarUrl);
        Bind(cmd, "$cid", channelId);
        cmd.ExecuteNonQuery();
        SubscriptionsChanged?.Invoke();
    }

    public void Unsubscribe(string channelUrl)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM subscriptions WHERE channel_url=$url;";
        Bind(cmd, "$url", channelUrl);
        cmd.ExecuteNonQuery();
        SubscriptionsChanged?.Invoke();
    }

    public bool IsSubscribed(string channelUrl)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM subscriptions WHERE channel_url=$url LIMIT 1;";
        Bind(cmd, "$url", channelUrl);
        return cmd.ExecuteScalar() is not null;
    }

    public List<Subscription> Subscriptions()
    {
        var list = new List<Subscription>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT channel_url,channel_name,avatar,channel_id FROM subscriptions ORDER BY channel_name COLLATE NOCASE;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new Subscription
            {
                ChannelUrl = rd.GetString(0),
                ChannelName = rd.IsDBNull(1) ? "" : rd.GetString(1),
                AvatarUrl = rd.IsDBNull(2) ? "" : rd.GetString(2),
                ChannelId = rd.IsDBNull(3) ? "" : rd.GetString(3),
            });
        return list;
    }

    // ---------------- Playlists ----------------

    public long CreatePlaylist(string name)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO playlists(name,created_at) VALUES($name,$now); SELECT last_insert_rowid();";
        Bind(cmd, "$name", name);
        Bind(cmd, "$now", Now());
        long id = (long)(cmd.ExecuteScalar() ?? 0L);
        PlaylistsChanged?.Invoke();
        return id;
    }

    public List<Playlist> Playlists()
    {
        var list = new List<Playlist>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT p.id, p.name,
                              (SELECT COUNT(*) FROM playlist_items WHERE playlist_id = p.id)
                            FROM playlists p ORDER BY p.created_at;";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new Playlist { Id = rd.GetInt64(0), Name = rd.IsDBNull(1) ? "" : rd.GetString(1), ItemCount = rd.GetInt32(2) });
        return list;
    }

    public void DeletePlaylist(long playlistId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlists WHERE id=$id;";
        Bind(cmd, "$id", playlistId);
        cmd.ExecuteNonQuery();
        PlaylistsChanged?.Invoke();
    }

    public void AddToPlaylist(long playlistId, SearchResult r)
    {
        if (string.IsNullOrEmpty(r.Url)) return;
        using var c = Open();

        // Skip if the url is already in this playlist.
        using (var exists = c.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM playlist_items WHERE playlist_id=$pid AND url=$url LIMIT 1;";
            Bind(exists, "$pid", playlistId);
            Bind(exists, "$url", r.Url);
            if (exists.ExecuteScalar() is not null) return;
        }

        long nextPos;
        using (var max = c.CreateCommand())
        {
            max.CommandText = "SELECT COALESCE(MAX(position),-1)+1 FROM playlist_items WHERE playlist_id=$pid;";
            Bind(max, "$pid", playlistId);
            nextPos = (long)(max.ExecuteScalar() ?? 0L);
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = @"INSERT INTO playlist_items(playlist_id,url,title,channel,thumbnail,duration,added_at,position,view_count,published)
                            VALUES($pid,$url,$title,$channel,$thumb,$dur,$now,$pos,$views,$pub);";
        Bind(cmd, "$pid", playlistId);
        Bind(cmd, "$url", r.Url);
        Bind(cmd, "$title", r.Title);
        Bind(cmd, "$channel", r.Channel);
        Bind(cmd, "$thumb", r.ThumbnailUrl);
        Bind(cmd, "$dur", r.DurationSeconds);
        Bind(cmd, "$now", Now());
        Bind(cmd, "$pos", nextPos);
        Bind(cmd, "$views", r.ViewCount);
        Bind(cmd, "$pub", r.Published);
        cmd.ExecuteNonQuery();
        PlaylistsChanged?.Invoke();
    }

    public void RemoveFromPlaylist(long playlistId, string url)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id=$pid AND url=$url;";
        Bind(cmd, "$pid", playlistId);
        Bind(cmd, "$url", url);
        cmd.ExecuteNonQuery();
        PlaylistsChanged?.Invoke();
    }

    public void ReorderPlaylist(long playlistId, IReadOnlyList<string> urlsInOrder)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (int i = 0; i < urlsInOrder.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE playlist_items SET position=$pos WHERE playlist_id=$pid AND url=$url;";
            Bind(cmd, "$pos", i);
            Bind(cmd, "$pid", playlistId);
            Bind(cmd, "$url", urlsInOrder[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<SearchResult> PlaylistItems(long playlistId)
    {
        var list = new List<SearchResult>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"SELECT url,title,channel,thumbnail,duration,view_count,published FROM playlist_items
                            WHERE playlist_id=$pid ORDER BY position;";
        Bind(cmd, "$pid", playlistId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add(new SearchResult
            {
                Url = rd.GetString(0),
                Title = rd.IsDBNull(1) ? "" : rd.GetString(1),
                Channel = rd.IsDBNull(2) ? "" : rd.GetString(2),
                ThumbnailUrl = rd.IsDBNull(3) ? "" : rd.GetString(3),
                DurationSeconds = rd.IsDBNull(4) ? 0 : rd.GetInt64(4),
                ViewCount = rd.IsDBNull(5) ? -1 : rd.GetInt64(5),
                Published = rd.IsDBNull(6) ? 0 : rd.GetInt64(6),
                Kind = ResultKind.Video,
            });
        return list;
    }

    /// <summary>Backfill view count + upload date for a URL across history and playlists (called once we
    /// have full metadata for a video the user opened). Only raises stored values, never clears them.</summary>
    public void SetVideoMeta(string url, long viewCount, long published)
    {
        if (string.IsNullOrEmpty(url) || (viewCount < 0 && published <= 0)) return;
        using var c = Open();
        foreach (var table in new[] { "history", "playlist_items" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $@"UPDATE {table} SET
                                   view_count = MAX(view_count, $views),
                                   published  = MAX(published, $pub)
                                 WHERE url = $url;";
            Bind(cmd, "$views", viewCount);
            Bind(cmd, "$pub", published);
            Bind(cmd, "$url", url);
            cmd.ExecuteNonQuery();
        }
        using (var cache = c.CreateCommand())
        {
            cache.CommandText = @"INSERT INTO video_meta(url,view_count,published,fetched_at)
                                  VALUES($url,$views,$pub,$now)
                                  ON CONFLICT(url) DO UPDATE SET
                                    view_count=MAX(video_meta.view_count, excluded.view_count),
                                    published =MAX(video_meta.published, excluded.published),
                                    fetched_at=excluded.fetched_at;";
            Bind(cache, "$url", url);
            Bind(cache, "$views", viewCount);
            Bind(cache, "$pub", published);
            Bind(cache, "$now", Now());
            cache.ExecuteNonQuery();
        }
    }

    /// <summary>Apply cached views + upload date to any results we've already fetched metadata for.
    /// Returns the URLs still missing an upload date (callers can fetch those in the background).</summary>
    public List<string> FillVideoMeta(IReadOnlyList<SearchResult> items)
    {
        var missing = new List<string>();
        if (items.Count == 0) return missing;

        var cache = new Dictionary<string, (long views, long pub)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT url,view_count,published FROM video_meta;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                cache[rd.GetString(0)] = (rd.IsDBNull(1) ? -1 : rd.GetInt64(1), rd.IsDBNull(2) ? 0 : rd.GetInt64(2));
        }

        foreach (var r in items)
        {
            if (r.Kind is not (ResultKind.Video or ResultKind.Short)) continue;
            if (cache.TryGetValue(r.Url, out var m))
            {
                if (r.ViewCount < 0 && m.views >= 0) r.ViewCount = m.views;
                if (r.Published <= 0 && m.pub > 0) r.Published = m.pub;
            }
            if (r.Published <= 0) missing.Add(r.Url);
        }
        return missing;
    }

    // ---------------- Watch progress (resume) ----------------

    /// <summary>Updates resume position for a video already in history (no-op otherwise).</summary>
    public void SetProgress(string url, long position, long duration, bool completed)
    {
        if (string.IsNullOrEmpty(url)) return;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE history SET position=$pos, duration=$dur, completed=$c WHERE url=$url;";
        Bind(cmd, "$pos", position);
        Bind(cmd, "$dur", duration);
        Bind(cmd, "$c", completed ? 1 : 0);
        Bind(cmd, "$url", url);
        cmd.ExecuteNonQuery();
    }

    public WatchProgress GetProgress(string url)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT position,duration,completed FROM history WHERE url=$url;";
        Bind(cmd, "$url", url);
        using var rd = cmd.ExecuteReader();
        if (rd.Read())
            return new WatchProgress(rd.GetInt64(0), rd.GetInt64(1), rd.GetInt64(2) != 0);
        return WatchProgress.None;
    }

    // ---------------- helpers ----------------

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
