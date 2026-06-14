// FreeFlume — in-app self-update (manual: triggered from Settings → Backends).
// Author: velkadyne
// Checks the GitHub "latest release" for a newer version and, on request, downloads + applies it.
//  - Portable single-file build: swap the running FreeFlume.exe in place (rename the running exe aside,
//    drop the new one in, relaunch). Windows allows renaming — but not overwriting — a running exe.
//  - Installed build (online setup): download the installer and run it; it upgrades in place (stable AppId).
// No background checks — the user presses a button.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlume.Services;

public static class AppUpdate
{
    private const string LatestApi =
        "https://api.github.com/repos/velkadyneslop/freeflume-windows/releases/latest";

    public sealed record Info(Version Latest, string Tag, string ReleaseUrl, string? PortableUrl, string? InstallerUrl)
    {
        public bool IsNewer => Latest > CurrentVersion;
    }

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    private static HttpClient Client()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("FreeFlume-Updater");   // GitHub API requires a UA
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Query the latest GitHub release. Throws on network/parse failure.</summary>
    public static async Task<Info> CheckAsync(CancellationToken ct = default)
    {
        using var http = Client();
        var json = await http.GetStringAsync(LatestApi, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        string relUrl = root.TryGetProperty("html_url", out var hu) ? (hu.GetString() ?? "") : "";

        string? portable = null, installer = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? (u.GetString() ?? "") : "";
                if (url.Length == 0) continue;
                if (name.Equals("FreeFlume.exe", StringComparison.OrdinalIgnoreCase)) portable = url;
                else if (name.EndsWith("online-setup.exe", StringComparison.OrdinalIgnoreCase)) installer = url;
            }

        return new Info(ParseTag(tag), tag, relUrl, portable, installer);
    }

    private static Version ParseTag(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var v) ? v : new Version(0, 0, 0);

    /// <summary>True if running as the portable single-file exe (extracted to a temp dir), false if installed
    /// (the app folder is the exe's own folder).</summary>
    public static bool IsPortable()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
            var baseDir = AppContext.BaseDirectory.TrimEnd('\\');
            return !string.Equals(exeDir.TrimEnd('\\'), baseDir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Best-effort cleanup of the renamed-aside old exe + any leftover download (call at startup).</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var cur = Environment.ProcessPath;
            if (string.IsNullOrEmpty(cur)) return;
            foreach (var stale in new[] { cur + ".old", Path.Combine(Path.GetDirectoryName(cur)!, "FreeFlume.update.exe") })
                try { if (File.Exists(stale)) File.Delete(stale); } catch { }
        }
        catch { }
    }

    /// <summary>Download the right asset and apply it. On success the caller should exit the app (the new exe
    /// / installer has been launched). Returns (false, reason) if it couldn't proceed.</summary>
    public static async Task<(bool ok, string message)> ApplyAsync(Info info, IProgress<double>? progress, CancellationToken ct = default)
    {
        bool portable = IsPortable();
        string? url = portable ? info.PortableUrl : info.InstallerUrl;
        if (string.IsNullOrEmpty(url))
            return (false, portable ? "The latest release has no portable build." : "The latest release has no installer.");

        try
        {
            if (portable)
            {
                string cur = Environment.ProcessPath!;
                string dir = Path.GetDirectoryName(cur)!;
                string tmp = Path.Combine(dir, "FreeFlume.update.exe");
                await DownloadAsync(url, tmp, progress, ct);

                string old = cur + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                File.Move(cur, old);     // rename the running exe aside (allowed on Windows)
                File.Move(tmp, cur);     // drop the new exe into its place
                Process.Start(new ProcessStartInfo { FileName = cur, UseShellExecute = true });
                return (true, "Updated — restarting…");
            }
            else
            {
                string tmp = Path.Combine(Path.GetTempPath(), "FreeFlume-setup.exe");
                await DownloadAsync(url, tmp, progress, ct);
                // Run the installer (it upgrades in place); the app exits so its files aren't locked.
                Process.Start(new ProcessStartInfo { FileName = tmp, UseShellExecute = true });
                return (true, "Installer launched — follow its prompts.");
            }
        }
        catch (Exception ex)
        {
            return (false, "Update failed: " + ex.Message);
        }
    }

    private static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = Client();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 20];
        long read = 0; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total is > 0) progress?.Report((double)read / total.Value);
        }
    }
}
