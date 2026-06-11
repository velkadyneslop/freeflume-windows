// FreeFlume — turn a plain description into TextBlock inlines with clickable
// URLs (open in browser) and timestamps (seek/play callback).
// Author: velkadyne

using System;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace FreeFlume.Views
{
    public static class DescriptionInlines
    {
        // URL, or timestamp (m:ss / h:mm:ss).
        private static readonly Regex Token = new(
            @"(https?://[^\s]+)|(\b(?:\d{1,2}:)?\d{1,2}:\d{2}\b)", RegexOptions.Compiled);

        /// <summary>Populate <paramref name="target"/>.Inlines from <paramref name="text"/>.
        /// <paramref name="onTimestamp"/> (if set) is invoked with the time in seconds when a
        /// timestamp link is clicked; URLs open in the default browser automatically.</summary>
        public static void Fill(TextBlock target, string text, Action<double>? onTimestamp)
        {
            target.Inlines.Clear();
            int last = 0;
            foreach (Match m in Token.Matches(text ?? ""))
            {
                if (m.Index > last) target.Inlines.Add(new Run { Text = text![last..m.Index] });

                if (m.Groups[1].Success)   // URL
                {
                    var link = new Hyperlink();
                    if (Uri.TryCreate(m.Value, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
                    link.Inlines.Add(new Run { Text = m.Value });
                    target.Inlines.Add(link);
                }
                else                        // timestamp
                {
                    double secs = ParseTimestamp(m.Value);
                    var link = new Hyperlink();
                    link.Inlines.Add(new Run { Text = m.Value });
                    if (onTimestamp is not null) link.Click += (_, __) => onTimestamp(secs);
                    target.Inlines.Add(link);
                }
                last = m.Index + m.Length;
            }
            if (last < (text?.Length ?? 0)) target.Inlines.Add(new Run { Text = text![last..] });
        }

        private static double ParseTimestamp(string s)
        {
            double v = 0;
            foreach (var part in s.Split(':'))
                v = v * 60 + (int.TryParse(part, out var n) ? n : 0);
            return v;
        }
    }
}
