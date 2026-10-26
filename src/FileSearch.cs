using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GeminiLauncher
{
    public class FileHit
    {
        public string Path, Name;
        public DateTime Modified;
        public bool IsFolder;
        public bool ContentMatch;
        public double Score;
    }

    /// <summary>Searches the Windows Search index (the same index Explorer/Start use), so it's instant and needs no crawling.</summary>
    public static class FileSearch
    {
        const string Conn = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

        static string Clean(string q)
        {
            var sb = new StringBuilder();
            foreach (char c in q)
                sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' ? c : ' ');
            return sb.ToString().Trim();
        }

        static readonly string[] SkipPath =
        {
            "\\appdata\\", "\\$recycle.bin", "\\windows\\", "\\program files", "\\programdata\\", "\\node_modules\\", "\\.git\\", "\\site-packages\\", "\\microsoft\\windows\\start menu\\"
        };

        public static List<FileHit> Search(string query, bool contents, int max, CancellationToken ct)
        {
            var hits = new List<FileHit>();
            var q = Clean(query);
            if (q.Length < 2) return hits;
            var tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray();
            string contains = string.Join(" AND ", tokens.Select(t => "\"" + t.Replace("'", "''") + "*\""));

            string sqlName =
                "SELECT TOP 40 System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.ItemType FROM SystemIndex " +
                "WHERE SCOPE='file:' AND CONTAINS(System.ItemNameDisplay, '" + contains + "') ORDER BY System.DateModified DESC";
            string sqlContent =
                "SELECT TOP 8 System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.ItemType FROM SystemIndex " +
                "WHERE SCOPE='file:' AND System.Kind = 'document' AND CONTAINS(*, '" + contains + "') ORDER BY System.Search.Rank DESC";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var c = new OleDbConnection(Conn))
                {
                    c.Open();
                    Run(c, sqlName, false, hits, seen, ct);
                    if (contents && !ct.IsCancellationRequested && q.Length >= 3)
                        Run(c, sqlContent, true, hits, seen, ct);
                }
            }
            catch { }

            var ql = q.ToLowerInvariant();
            foreach (var h in hits)
            {
                var nl = System.IO.Path.GetFileNameWithoutExtension(h.Name ?? "").ToLowerInvariant();
                double s = h.ContentMatch ? 20 : 40;
                if (nl == ql) s = 90;
                else if (nl.StartsWith(ql)) s = 75;
                else if (nl.Contains(ql)) s = 55;
                double days = Math.Max(0, (DateTime.Now - h.Modified).TotalDays);
                s += Math.Max(0, 15 - Math.Log(1 + days) * 3);
                if (h.Path.IndexOf("\\Users\\", StringComparison.OrdinalIgnoreCase) >= 0) s += 5;
                h.Score = s;
            }
            return hits.OrderByDescending(h => h.Score).Take(max).ToList();
        }

        static void Run(OleDbConnection c, string sql, bool content, List<FileHit> hits, HashSet<string> seen, CancellationToken ct)
        {
            using (var cmd = new OleDbCommand(sql, c))
            using (var r = cmd.ExecuteReader())
            {
                while (r != null && r.Read())
                {
                    if (ct.IsCancellationRequested) return;
                    var path = r.IsDBNull(0) ? null : r.GetValue(0) as string;
                    if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                    var pl = path.ToLowerInvariant();
                    if (SkipPath.Any(sp => pl.Contains(sp))) continue;
                    if (pl.EndsWith(".lnk") || pl.EndsWith(".tmp") || pl.EndsWith(".ini")) continue;
                    var type = r.IsDBNull(3) ? "" : r.GetValue(3) as string ?? "";
                    DateTime mod = DateTime.MinValue;
                    try { if (!r.IsDBNull(2)) mod = Convert.ToDateTime(r.GetValue(2)).ToLocalTime(); } catch { }
                    hits.Add(new FileHit
                    {
                        Path = path,
                        Name = r.IsDBNull(1) ? System.IO.Path.GetFileName(path) : r.GetValue(1) as string,
                        Modified = mod,
                        IsFolder = type.Equals("Directory", StringComparison.OrdinalIgnoreCase) || (type == "" && Directory.Exists(path)),
                        ContentMatch = content
                    });
                }
            }
        }

        // ---------- Recent files (Windows "Recent items") ----------
        static List<FileHit> _recentCache;
        static DateTime _recentAt;

        public static List<FileHit> Recent(int max)
        {
            if (_recentCache != null && (DateTime.Now - _recentAt).TotalSeconds < 20) return _recentCache.Take(max).ToList();
            var list = new List<FileHit>();
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                var lnks = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(60);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in lnks)
                {
                    var target = Native.ResolveShortcut(l.FullName);
                    if (string.IsNullOrEmpty(target) || !seen.Add(target)) continue;
                    if (target.StartsWith("\\\\")) continue; // skip network paths (may hang)
                    if (!File.Exists(target)) continue;   // files only for "Recommended"
                    var tl = target.ToLowerInvariant();
                    if (tl.EndsWith(".exe") || tl.EndsWith(".lnk") || tl.Contains("\\appdata\\local\\temp")) continue;
                    list.Add(new FileHit { Path = target, Name = Path.GetFileName(target), Modified = l.LastWriteTime });
                    if (list.Count >= 20) break;
                }
            }
            catch { }
            _recentCache = list;
            _recentAt = DateTime.Now;
            return list.Take(max).ToList();
        }

        public static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "Just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            if (d.TotalDays < 2) return "Yesterday";
            if (d.TotalDays < 7) return t.ToString("dddd");
            return t.ToString("MMM d");
        }
    }
}
