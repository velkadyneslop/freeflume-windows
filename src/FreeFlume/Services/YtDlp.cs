// FreeFlume — yt-dlp executable resolution + self-update.
// YouTube breaks yt-dlp's extraction constantly, so a bundled-only copy rots within weeks. The bundled
// yt-dlp.exe lives inside the single-file exe (temp-extracted each launch) and can't update itself
// persistently, so we keep a WRITABLE copy in app-data and run `yt-dlp -U` against that. mpv and every
// yt-dlp call are pointed at this copy. (mpv/libmpv is NOT self-updated — it's a loaded DLL shipped with
// app releases.)
// Author: velkadyne

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FreeFlume.Services
{
    public static class YtDlp
    {
        private static readonly string Bundled = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        private static readonly string Writable = Path.Combine(AppPaths.DataDir(), "bin", "yt-dlp.exe");

        /// <summary>The yt-dlp to invoke: the updatable app-data copy if present, else the bundled one.</summary>
        public static string ExePath => File.Exists(Writable) ? Writable : Bundled;

        /// <summary>Seed the writable copy from the bundled exe on first run, so `-U` can update it.</summary>
        public static void EnsureLocalCopy()
        {
            try
            {
                if (File.Exists(Writable) || !File.Exists(Bundled)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(Writable)!);
                File.Copy(Bundled, Writable, overwrite: false);
            }
            catch { /* fall back to the bundled copy */ }
        }

        /// <summary>Run `yt-dlp -U` (updates the writable copy in place). Returns a short status line.</summary>
        public static async Task<string> UpdateAsync()
        {
            EnsureLocalCopy();
            string exe = ExePath;
            if (!File.Exists(exe)) return "yt-dlp not found.";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-U",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return "Could not start yt-dlp.";
                string outp = await p.StandardOutput.ReadToEndAsync();
                string err = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                var lines = (outp + "\n" + err).Split('\n');
                foreach (var raw in lines)
                {
                    var t = raw.Trim();
                    if (t.Contains("up to date", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("Updated yt-dlp", StringComparison.OrdinalIgnoreCase)
                        || t.Contains("Updating to", StringComparison.OrdinalIgnoreCase))
                        return t;
                }
                for (int i = lines.Length - 1; i >= 0; i--)
                    if (lines[i].Trim().Length > 0) return lines[i].Trim();
                return "Update check complete.";
            }
            catch (Exception ex) { return "Update failed: " + ex.Message; }
        }

        /// <summary>Best-effort background auto-update, throttled to once per day. Silent.</summary>
        public static void MaybeAutoUpdate()
        {
            var s = Settings.Shared;
            if (!s.AutoUpdateYtDlp) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - s.LastYtDlpCheck < 86_400) return;   // once / 24h
            s.LastYtDlpCheck = now; s.Save();
            _ = Task.Run(async () => { try { await UpdateAsync(); } catch { } });
        }
    }
}
