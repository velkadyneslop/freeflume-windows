// Generates the "Windows-exclusive features" document as both .txt and .pdf from one source.
// Run: dotnet run tools/additionsdoc.cs
#:package QuestPDF@2024.12.3

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

const string Title = "FreeFlume — Windows-Exclusive Features";
const string Subtitle = "Features present in the Windows build but not in the Linux version (v1.0.0).";
const string Dated = "Generated June 9, 2026";

var sections = new (string Heading, (string Name, string Desc)[] Items)[]
{
    ("Player & playback", new (string, string)[]
    {
        ("Higher quality presets (1440p & 2160p/4K)",
            "The quality selector adds explicit 1440p and 2160p (4K) options. The Linux preset list stops at 1080p."),
        ("Hardware-decode stall auto-recovery",
            "A watchdog detects when the GPU decoder hangs mid-stream (a frozen picture while audio keeps playing) and transparently switches that video to software decoding, then seeks back so playback continues."),
        ("HiDPI video sharpness",
            "An optional setting renders video at the display's native (physical) resolution for crisper output on scaled / high-DPI screens."),
        ("Remember Picture-in-Picture size",
            "The PiP window reopens at the size you last used."),
    }),
    ("Search & browsing", new (string, string)[]
    {
        ("Full metadata on every video row",
            "Every video — in search, history, playlists, subscriptions, and the Up Next queue — shows channel, duration, view count, and upload date (compact, e.g. \"5 Jan 2020\"). Upload dates that the fast listings leave out are fetched per-video in the background (throttled) and cached, so they appear within a few seconds and load instantly afterwards."),
        ("Paste-to-play URLs",
            "Paste a YouTube link straight into the search bar to open that video, instead of running a text search."),
        ("Live-channel indicator",
            "A red ring is drawn around a channel's avatar — in both Search results and the Subscriptions list — while that channel is currently live-streaming."),
        ("Enhanced pagination",
            "Jump directly to a specific page number, see a running \"of N+\" total that grows as you browse deeper, and page backward faster."),
    }),
    ("Reliability", new (string, string)[]
    {
        ("Crash-resilient UI",
            "Unhandled interface errors are written to a log and the app keeps running instead of closing to the desktop."),
        ("Self-healing video surface",
            "Moving the live video surface between the full player and the mini-player retries on the occasional WinUI reparent failure rather than crashing the app."),
    }),
    ("Captions (behavioral difference)", new (string, string)[]
    {
        ("Automatic human-caption fetching",
            "All human-made subtitle tracks are fetched automatically and chosen from the on-screen CC menu while watching, instead of configuring a language code and auto-translate target up front. (This simplifies — and replaces — the Linux per-language / auto-translate settings.)"),
    }),
};

// ---- plain text ----
var sb = new System.Text.StringBuilder();
sb.AppendLine(Title);
sb.AppendLine(new string('=', Title.Length));
sb.AppendLine(Subtitle);
sb.AppendLine(Dated);
sb.AppendLine();
int n = 0;
foreach (var (heading, items) in sections)
{
    sb.AppendLine(heading.ToUpperInvariant());
    sb.AppendLine(new string('-', heading.Length));
    foreach (var (name, desc) in items)
    {
        sb.AppendLine($"{++n}. {name}");
        foreach (var line in Wrap(desc, 92)) sb.AppendLine("   " + line);
        sb.AppendLine();
    }
}
System.IO.File.WriteAllText("FreeFlume-Windows-Additions.txt", sb.ToString());

static System.Collections.Generic.IEnumerable<string> Wrap(string text, int width)
{
    var words = text.Split(' ');
    var line = new System.Text.StringBuilder();
    foreach (var w in words)
    {
        if (line.Length > 0 && line.Length + 1 + w.Length > width) { yield return line.ToString(); line.Clear(); }
        if (line.Length > 0) line.Append(' ');
        line.Append(w);
    }
    if (line.Length > 0) yield return line.ToString();
}

// ---- PDF ----
QuestPDF.Settings.License = LicenseType.Community;
int idx = 0;
Document.Create(container =>
{
    container.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(48);
        page.DefaultTextStyle(t => t.FontSize(11).FontColor(Colors.Grey.Darken3));

        page.Header().Column(col =>
        {
            col.Item().Text(Title).FontSize(20).Bold().FontColor(Colors.Black);
            col.Item().PaddingTop(2).Text(Subtitle).FontSize(11).FontColor(Colors.Grey.Darken1);
            col.Item().Text(Dated).FontSize(9).FontColor(Colors.Grey.Medium);
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });

        page.Content().PaddingTop(12).Column(col =>
        {
            col.Spacing(14);
            foreach (var (heading, items) in sections)
            {
                col.Item().Column(sec =>
                {
                    sec.Spacing(8);
                    sec.Item().Text(heading).FontSize(13).Bold().FontColor(Colors.Blue.Darken2);
                    foreach (var (name, desc) in items)
                    {
                        sec.Item().Row(row =>
                        {
                            row.ConstantItem(24).Text($"{++idx}.").SemiBold();
                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text(name).SemiBold();
                                c.Item().Text(desc).FontColor(Colors.Grey.Darken2);
                            });
                        });
                    }
                });
            }
        });

        page.Footer().AlignCenter().Text("FreeFlume — github/codeberg: velkadyne").FontSize(8).FontColor(Colors.Grey.Medium);
    });
}).GeneratePdf("FreeFlume-Windows-Additions.pdf");

System.Console.WriteLine("Wrote FreeFlume-Windows-Additions.txt and .pdf");
