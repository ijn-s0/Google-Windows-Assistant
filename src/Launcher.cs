using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace GeminiLauncher
{
    /// <summary>Opens apps, files, settings pages and URLs. If the launcher itself runs elevated,
    /// everything is started through Explorer so launched apps run with normal (non-admin) rights.</summary>
    public static class Launcher
    {
        static readonly bool Elevated = Settings.IsElevated;

        static void Shell(string target, string verb = null)
        {
            if (Elevated && verb == null)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (verb != null) psi.Verb = verb;
            try { psi.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            Process.Start(psi);
        }

        public static bool Open(ResultItem it, bool admin = false)
        {
            try
            {
                switch (it.Kind)
                {
                    case ItemKind.App:
                        if (admin && !string.IsNullOrEmpty(it.LnkPath)) { Shell(it.LnkPath, "runas"); return true; }
                        Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + it.Target) { UseShellExecute = true });
                        return true;
                    case ItemKind.File:
                    case ItemKind.Folder:
                        if (admin && it.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { Shell(it.Target, "runas"); return true; }
                        Shell(it.Target);
                        return true;
                    case ItemKind.Setting:
                    case ItemKind.Url:
                        Shell(it.Target);
                        return true;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // UAC cancelled
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't open " + it.Title + "\n\n" + ex.Message, "Gemini Launcher");
            }
            return false;
        }

        public static void OpenUrl(string url)
        {
            try { Shell(url); } catch { }
        }

        public static void GoogleSearch(string q) => OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(q ?? ""));

        public static void ShowInFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }

        public static bool LooksLikeUrl(string q, out string url)
        {
            url = null;
            q = (q ?? "").Trim();
            if (q.Contains(" ") || q.Length < 4) return false;
            if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = q; return Uri.IsWellFormedUriString(q, UriKind.Absolute);
            }
            if (q.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) { url = "https://" + q; return true; }
            // domain.tld[/path]
            var host = q.Split('/')[0];
            int dot = host.LastIndexOf('.');
            if (dot <= 0 || dot >= host.Length - 2) return false;
            var tld = host.Substring(dot + 1).ToLowerInvariant();
            foreach (char c in tld) if (!char.IsLetter(c)) return false;
            if (tld.Length > 6) return false;
            // avoid treating file names (report.pdf) as URLs
            string[] fileExt = { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "png", "jpg", "jpeg", "gif", "exe", "zip", "csv", "md", "json", "mp3", "mp4", "py", "js", "cs", "html", "lnk" };
            if (Array.IndexOf(fileExt, tld) >= 0) return false;
            url = "https://" + q;
            return true;
        }
    }
}
