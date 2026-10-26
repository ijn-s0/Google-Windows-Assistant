using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace GeminiLauncher
{
    public class AppEntry
    {
        public string Name;
        public string Id;       // parsing name inside shell:AppsFolder (AUMID or known-folder path)
        public string Lnk;      // matching Start-menu shortcut, if any
        public string NameLower;
        public string Initials;
    }

    /// <summary>All apps Windows shows in Start (desktop + Store apps) via shell:AppsFolder.</summary>
    public static class AppIndex
    {
        static readonly string CachePath = Path.Combine(Settings.Dir, "apps-cache.json");
        public static volatile List<AppEntry> Apps = new List<AppEntry>();
        public static event Action Updated;
        static DateTime _lastRefresh = DateTime.MinValue;
        static int _refreshing;

        static readonly string[] Junk = { "uninstall", "readme", "help", "documentation", "release notes", "website", "license", "manual" };

        public static void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var list = new List<AppEntry>();
                foreach (var o in Json.Parse(File.ReadAllText(CachePath, Encoding.UTF8)).Arr())
                    list.Add(Make(o.Str("n"), o.Str("i"), o.Str("l")));
                Apps = list;
            }
            catch { }
        }

        public static void RefreshAsync(bool force = false)
        {
            if (!force && (DateTime.Now - _lastRefresh).TotalMinutes < 10) return;
            if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
            _lastRefresh = DateTime.Now;
            var t = new Thread(() =>
            {
                try
                {
                    var list = Enumerate();
                    if (list.Count > 0)
                    {
                        Apps = list;
                        SaveCache(list);
                        Updated?.Invoke();
                    }
                }
                catch { }
                finally { Interlocked.Exchange(ref _refreshing, 0); }
            })
            { IsBackground = true, Name = "AppIndex" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static AppEntry Make(string name, string id, string lnk)
        {
            var lower = (name ?? "").ToLowerInvariant();
            var initials = new string(lower.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]).ToArray());
            return new AppEntry { Name = name, Id = id, Lnk = lnk, NameLower = lower, Initials = initials };
        }

        static List<AppEntry> Enumerate()
        {
            // Map Start-menu shortcut names -> .lnk path (used for "Run as administrator" / "Open file location")
            var lnkByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            })
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                        lnkByName[Path.GetFileNameWithoutExtension(f)] = f;
                }
                catch { }
            }

            var result = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Type t = Type.GetTypeFromProgID("Shell.Application");
            dynamic shell = Activator.CreateInstance(t);
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            dynamic items = folder.Items();
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic it = items.Item(i);
                    string name = it.Name;
                    string id = it.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    var ln = name.ToLowerInvariant();
                    if (Junk.Any(j => ln.Contains(j))) continue;
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name + "|" + id)) continue;
                    lnkByName.TryGetValue(name, out var lnk);
                    result.Add(Make(name, id, lnk));
                }
                catch { }
            }
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return result;
        }

        static void SaveCache(List<AppEntry> list)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var arr = list.Select(a => (object)new Dictionary<string, object> { ["n"] = a.Name, ["i"] = a.Id, ["l"] = a.Lnk }).ToList();
                File.WriteAllText(CachePath, Json.Write(arr), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Fuzzy-ish match score for app names. 0 = no match.</summary>
        public static double Score(string nameLower, string initials, string q)
        {
            if (q.Length == 0) return 0;
            if (nameLower == q) return 100;
            if (nameLower.StartsWith(q)) return 85 - Math.Min(10, nameLower.Length - q.Length) * 0.3;
            var words = nameLower.Split(new[] { ' ', '-', '_', '.', '(' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(w => w.StartsWith(q))) return 70;
            if (q.Length >= 2 && initials.StartsWith(q)) return 65;
            // all query words are prefixes of some word
            var qw = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (qw.Length > 1 && qw.All(x => words.Any(w => w.StartsWith(x)))) return 68;
            if (q.Length >= 3 && nameLower.Contains(q)) return 45;
            // subsequence
            if (q.Length >= 3)
            {
                int j = 0;
                foreach (char c in nameLower) if (j < q.Length && c == q[j]) j++;
                if (j == q.Length) return 20;
            }
            return 0;
        }

        public static List<(AppEntry app, double score)> Search(string q, Settings s, int max)
        {
            q = q.Trim().ToLowerInvariant();
            var res = new List<(AppEntry, double)>();
            foreach (var a in Apps)
            {
                var sc = Score(a.NameLower, a.Initials, q);
                if (sc <= 0) continue;
                sc += Math.Min(15, Math.Log(1 + s.LaunchCount("app:" + a.Id)) * 5);
                res.Add((a, sc));
            }
            return res.OrderByDescending(x => x.Item2).ThenBy(x => x.Item1.Name.Length).Take(max).ToList();
        }

        public static AppEntry FindById(string id) => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        public static AppEntry FindByName(string name) => Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        // ---------- Frequently used apps (from Windows' own UserAssist counters) ----------
        public static List<AppEntry> Frequent(Settings s, int max)
        {
            var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var ua = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist"))
                {
                    if (ua != null)
                        foreach (var guid in ua.GetSubKeyNames())
                            using (var c = ua.OpenSubKey(guid + @"\Count"))
                            {
                                if (c == null) continue;
                                foreach (var vn in c.GetValueNames())
                                {
                                    if (!(c.GetValue(vn) is byte[] data) || data.Length < 68) continue;
                                    int runs = BitConverter.ToInt32(data, 4);
                                    long ft = BitConverter.ToInt64(data, 60);
                                    if (runs <= 0) continue;
                                    double recencyDays = 365;
                                    try { if (ft > 0) recencyDays = (DateTime.UtcNow - DateTime.FromFileTimeUtc(ft)).TotalDays; } catch { }
                                    double score = Math.Log(1 + runs) * 10 + Math.Max(0, 30 - recencyDays);
                                    var key = Rot13(vn);
                                    if (!scores.TryGetValue(key, out var old) || old < score) scores[key] = score;
                                }
                            }
                }
            }
            catch { }
            foreach (var kv in s.LaunchCounts)
                if (kv.Key.StartsWith("app:"))
                {
                    var id = kv.Key.Substring(4);
                    scores.TryGetValue(id, out var old);
                    scores[id] = old + kv.Value * 8;
                }

            var pinned = new HashSet<string>(s.Pins.Where(p => p.Kind == "app").Select(p => p.Target), StringComparer.OrdinalIgnoreCase);
            return Apps.Where(a => scores.ContainsKey(a.Id) && !pinned.Contains(a.Id) && !s.IsHiddenRecommended("app:" + a.Id))
                       .OrderByDescending(a => scores[a.Id]).Take(max).ToList();
        }

        static string Rot13(string s)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                char x = c[i];
                if (x >= 'a' && x <= 'z') c[i] = (char)('a' + (x - 'a' + 13) % 26);
                else if (x >= 'A' && x <= 'Z') c[i] = (char)('A' + (x - 'A' + 13) % 26);
            }
            return new string(c);
        }

        /// <summary>Default pins on first run, if these apps exist.</summary>
        public static IEnumerable<AppEntry> DefaultPins()
        {
            string[] wanted = { "File Explorer", "Google Chrome", "Microsoft Edge", "Firefox", "Settings", "Notepad", "Calculator", "Terminal", "Visual Studio Code", "Photos", "Microsoft Store", "Spotify", "Discord", "Word", "Excel", "Outlook" };
            foreach (var w in wanted)
            {
                var a = Apps.FirstOrDefault(x => string.Equals(x.Name, w, StringComparison.OrdinalIgnoreCase));
                if (a != null) yield return a;
            }
        }
    }

    /// <summary>Common Windows Settings pages (ms-settings: URIs).</summary>
    public static class SettingsPages
    {
        public static readonly (string name, string keywords, string uri)[] All =
        {
            ("Bluetooth & devices", "bluetooth devices pair headphones mouse", "ms-settings:bluetooth"),
            ("Wi-Fi", "wifi wi-fi wireless network internet", "ms-settings:network-wifi"),
            ("Network & internet", "network internet ethernet proxy vpn", "ms-settings:network"),
            ("VPN", "vpn", "ms-settings:network-vpn"),
            ("Display", "display screen resolution brightness scale monitor hdr", "ms-settings:display"),
            ("Night light", "night light blue light", "ms-settings:nightlight"),
            ("Sound", "sound audio volume speaker microphone output input", "ms-settings:sound"),
            ("Notifications", "notifications focus do not disturb", "ms-settings:notifications"),
            ("Power & battery", "power battery sleep energy saver", "ms-settings:powersleep"),
            ("Storage", "storage disk space cleanup", "ms-settings:storagesense"),
            ("Default apps", "default apps browser", "ms-settings:defaultapps"),
            ("Installed apps", "installed apps uninstall programs remove", "ms-settings:appsfeatures"),
            ("Startup apps", "startup apps boot", "ms-settings:startupapps"),
            ("Personalization", "personalization wallpaper background theme", "ms-settings:personalization"),
            ("Background", "background wallpaper desktop", "ms-settings:personalization-background"),
            ("Colors", "colors dark mode light mode accent", "ms-settings:colors"),
            ("Taskbar", "taskbar", "ms-settings:taskbar"),
            ("Mouse", "mouse pointer cursor scroll", "ms-settings:mousetouchpad"),
            ("Touchpad", "touchpad trackpad gestures", "ms-settings:devices-touchpad"),
            ("Keyboard", "keyboard typing", "ms-settings:typing"),
            ("Language & region", "language region keyboard layout ime korean input", "ms-settings:regionlanguage"),
            ("Date & time", "date time clock timezone", "ms-settings:dateandtime"),
            ("Windows Update", "update windows update upgrade", "ms-settings:windowsupdate"),
            ("Privacy & security", "privacy security permissions", "ms-settings:privacy"),
            ("Windows Security", "antivirus defender security virus firewall", "windowsdefender:"),
            ("Accounts", "accounts user sign-in password pin", "ms-settings:accounts"),
            ("Sign-in options", "sign-in password pin hello fingerprint", "ms-settings:signinoptions"),
            ("Printers & scanners", "printer scanner print", "ms-settings:printers"),
            ("Camera", "camera webcam", "ms-settings:camera"),
            ("Microphone privacy", "microphone mic permission", "ms-settings:privacy-microphone"),
            ("Multitasking", "multitasking snap windows alt tab", "ms-settings:multitasking"),
            ("Clipboard", "clipboard history", "ms-settings:clipboard"),
            ("Accessibility", "accessibility ease of access narrator magnifier", "ms-settings:easeofaccess"),
            ("Gaming", "gaming game mode xbox", "ms-settings:gaming-gamemode"),
            ("About this PC", "about pc specs system info rename computer name", "ms-settings:about"),
            ("Recovery", "recovery reset pc restore", "ms-settings:recovery"),
            ("Mobile hotspot", "hotspot tethering", "ms-settings:network-mobilehotspot"),
            ("Control Panel", "control panel", "control"),
            ("Task Manager", "task manager processes", "taskmgr"),
        };

        public static List<((string name, string keywords, string uri) page, double score)> Search(string q, int max)
        {
            q = q.Trim().ToLowerInvariant();
            var res = new List<((string, string, string), double)>();
            if (q.Length < 2) return res;
            foreach (var p in All)
            {
                double sc = AppIndex.Score(p.name.ToLowerInvariant(), "", q);
                if (sc <= 0 && q.Length >= 3)
                {
                    foreach (var kw in p.keywords.Split(' '))
                        if (kw.StartsWith(q) || (q.Contains(" ") && q.Split(' ').All(w => p.keywords.Contains(w)))) { sc = 55; break; }
                }
                if (sc > 0) res.Add((p, sc - 5));
            }
            return res.OrderByDescending(x => x.Item2).Take(max).ToList();
        }
    }
}
