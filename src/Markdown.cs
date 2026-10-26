using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GeminiLauncher
{
    /// <summary>Minimal Markdown -> WPF FlowDocument blocks (headings, bullets, bold, italic, code, links).</summary>
    public static class Markdown
    {
        static readonly Regex Inline = new Regex(@"(\*\*(.+?)\*\*)|(`([^`]+)`)|(\[([^\]]+)\]\((https?://[^\s\)]+)\))|((?<![\w*])\*(?!\s)([^*\n]+?)\*(?![\w*]))|(https?://[^\s\)\]]+)", RegexOptions.Compiled);

        public static void Render(FlowDocument doc, string md, Brush fg, Brush fg2, Brush accent, Brush codeBg)
        {
            doc.Blocks.Clear();
            var lines = (md ?? "").Replace("\r", "").Split('\n');
            List list = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) { list = null; continue; }

                var bullet = Regex.Match(line, @"^(\s*)([-*•]|\d+[.)])\s+(.*)$");
                if (bullet.Success)
                {
                    int indent = bullet.Groups[1].Value.Length;
                    bool ordered = char.IsDigit(bullet.Groups[2].Value[0]);
                    if (list == null || (indent == 0 && (list.MarkerStyle == TextMarkerStyle.Decimal) != ordered))
                    {
                        list = new List
                        {
                            MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 2, 0, 6),
                            Padding = new Thickness(18, 0, 0, 0),
                            Foreground = fg
                        };
                        doc.Blocks.Add(list);
                    }
                    var p = new Paragraph { Margin = new Thickness(indent > 1 ? 16 : 0, 1, 0, 3) };
                    AddInlines(p.Inlines, bullet.Groups[3].Value, accent, codeBg);
                    list.ListItems.Add(new ListItem(p));
                    continue;
                }
                list = null;

                var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    var hp = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = h.Groups[1].Value.Length <= 2 ? 16 : 14.5, Margin = new Thickness(0, 6, 0, 4), Foreground = fg };
                    AddInlines(hp.Inlines, h.Groups[2].Value.Replace("**", ""), accent, codeBg);
                    doc.Blocks.Add(hp);
                    continue;
                }
                if (line.StartsWith(">"))
                {
                    var q = new Paragraph { Foreground = fg2, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(10, 0, 0, 0), BorderBrush = accent, BorderThickness = new Thickness(2, 0, 0, 0) };
                    AddInlines(q.Inlines, line.TrimStart('>', ' '), accent, codeBg);
                    doc.Blocks.Add(q);
                    continue;
                }
                var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8), Foreground = fg };
                AddInlines(para.Inlines, line, accent, codeBg);
                doc.Blocks.Add(para);
            }
        }

        static void AddInlines(InlineCollection target, string text, Brush accent, Brush codeBg)
        {
            int pos = 0;
            foreach (Match m in Inline.Matches(text))
            {
                if (m.Index > pos) target.Add(new Run(text.Substring(pos, m.Index - pos)));
                if (m.Groups[1].Success) target.Add(new Bold(new Run(m.Groups[2].Value)));
                else if (m.Groups[3].Success) target.Add(new Run(m.Groups[4].Value) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = codeBg });
                else if (m.Groups[5].Success) target.Add(Link(m.Groups[6].Value, m.Groups[7].Value, accent));
                else if (m.Groups[8].Success) target.Add(new Italic(new Run(m.Groups[9].Value)));
                else if (m.Groups[10].Success) target.Add(Link(m.Groups[10].Value, m.Groups[10].Value, accent));
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) target.Add(new Run(text.Substring(pos)));
        }

        static Inline Link(string label, string url, Brush accent)
        {
            var h = new Hyperlink(new Run(label)) { Foreground = accent, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
            try { h.NavigateUri = new Uri(url); } catch { }
            h.Click += (s, e) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } (Application.Current as App)?.HideLauncher(); };
            h.ToolTip = url;
            return h;
        }

        public static string ToPlain(string md)
        {
            var s = Regex.Replace(md ?? "", @"\*\*(.+?)\*\*", "$1");
            s = Regex.Replace(s, @"\[([^\]]+)\]\((https?://[^\)]+)\)", "$1 ($2)");
            s = Regex.Replace(s, @"^#{1,6}\s+", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^\s*\*\s+", "• ", RegexOptions.Multiline);
            return s.Trim();
        }
    }
}
