// FreeFlume — yt-dlp download queue (one active process at a time) with live progress.
// Author: velkadyne

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FreeFlume.Models;
using Microsoft.UI.Dispatching;

namespace FreeFlume.Services;

public sealed class DownloadManager
{
    public static DownloadManager Shared { get; } = new();

    /// <summary>Newest first; bound directly by the Downloads page.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = new();

    private static string _exe => YtDlp.ExePath;
    private DispatcherQueue? _ui;
    private DownloadItem? _active;

    public void Enqueue(SearchResult r, DownloadKind kind, IReadOnlyList<string>? formatArgs = null)
    {
        if (string.IsNullOrEmpty(r.Url)) return;
        _ui ??= DispatcherQueue.GetForCurrentThread();
        Items.Insert(0, new DownloadItem { Title = r.Title, Url = r.Url, Kind = kind, FormatArgs = formatArgs });
        StartNext();
    }

    public void Cancel(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Queued)
        {
            item.Status = DownloadStatus.Canceled;
            item.StatusText = "Canceled";
            return;
        }
        try { item.Process?.Kill(entireProcessTree: true); } catch { }
    }

    public void ClearFinished()
    {
        foreach (var done in Items.Where(IsFinished).ToList()) Items.Remove(done);
    }

    private static bool IsFinished(DownloadItem i) =>
        i.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

    private void StartNext()
    {
        if (_active is not null) return;
        var next = Items.LastOrDefault(i => i.Status == DownloadStatus.Queued); // FIFO
        if (next is not null) _ = RunAsync(next);
    }

    private async System.Threading.Tasks.Task RunAsync(DownloadItem item)
    {
        _active = item;
        UI(() => { item.Status = DownloadStatus.Downloading; item.StatusText = "Starting…"; });

        var folder = Settings.Shared.EffectiveDownloadFolder;
        Directory.CreateDirectory(folder);

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArgs(item, folder)) psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        item.Process = proc;
        string lastError = "";
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(item, e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data!; };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();
        }
        catch (Exception ex) { lastError = ex.Message; }

        int code = SafeExitCode(proc);
        UI(() =>
        {
            if (item.Status == DownloadStatus.Canceled) { /* leave canceled */ }
            else if (code == 0) { item.Percent = 100; item.Status = DownloadStatus.Completed; item.StatusText = "Completed"; }
            else { item.Status = DownloadStatus.Failed; item.StatusText = "Failed" + (lastError.Length > 0 ? ": " + lastError : ""); }
        });

        _active = null;
        StartNext();
    }

    private void OnLine(DownloadItem item, string line)
    {
        if (line.StartsWith("FFDL "))
        {
            var parts = line.Substring(5).Split('|');
            if (parts.Length >= 3)
            {
                int pct = ParsePercent(parts[0]);
                UI(() =>
                {
                    if (pct >= 0) item.Percent = pct;
                    item.Speed = parts[1].Trim();
                    item.Eta = parts[2].Trim();
                    item.StatusText = "Downloading";
                });
            }
        }
        else if (line.StartsWith("[Merger] Merging formats into ") || line.StartsWith("[download] Destination: "))
        {
            int q = line.IndexOf('"');
            item.FilePath = q >= 0 ? line.Substring(q + 1).TrimEnd('"') : line[(line.IndexOf(": ") + 2)..];
        }
    }

    private IEnumerable<string> BuildArgs(DownloadItem item, string folder)
    {
        var args = new List<string>
        {
            "--no-playlist", "--no-warnings", "--newline",
            "--extractor-args", "youtube:player_client=default,android",
            "--progress-template", "FFDL %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "-o", Path.Combine(folder, "%(title)s.%(ext)s"),
        };
        if (item.FormatArgs is { Count: > 0 })
            args.AddRange(item.FormatArgs);                 // explicit codec/container/subtitle choice
        else if (item.Kind == DownloadKind.Audio)
            args.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        else
        {
            int h = Settings.Shared.DownloadMaxHeight;
            string hf = h > 0 ? $"[height<={h}]" : "";
            args.AddRange(new[] { "-f", $"bv*{hf}+ba/b{hf}", "--merge-output-format", "mp4" });
        }
        args.Add(item.Url);
        return args;
    }

    private static int ParsePercent(string s)
    {
        s = s.Trim().TrimEnd('%').Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d) : -1;
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    private void UI(Action action)
    {
        if (_ui is not null) _ui.TryEnqueue(() => action());
        else action();
    }
}
