using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GeminiLauncher
{
    /// <summary>Per-user self-installer: no admin rights, no extra files. Registers in Settings → Apps for uninstall.</summary>
    public static class Installer
    {
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GeminiLauncher");
        public static readonly string InstalledExe = Path.Combine(InstallDir, "GeminiLauncher.exe");
        static readonly string ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Gemini Launcher.lnk");
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GeminiLauncher";

        static string CurrentExe => System.Reflection.Assembly.GetExecutingAssembly().Location;
        public static bool RunningInstalled => string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns true if the app should continue running from the current location.</summary>
        public static bool MaybeInstall()
        {
            if (RunningInstalled) return true;
            bool upgrade = File.Exists(InstalledExe);
            var choice = Prompt(upgrade);
            if (choice == 0) return false;      // cancelled
            if (choice == 2) return true;       // run portable
            try
            {
                StopOtherInstances();
                Directory.CreateDirectory(InstallDir);
                File.Copy(CurrentExe, InstalledExe, true);
                CreateShortcut(ShortcutPath, InstalledExe, "Search apps, files and the web with Gemini");
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    var ver = typeof(Installer).Assembly.GetName().Version.ToString(3);
                    k.SetValue("DisplayName", "Gemini Launcher");
                    k.SetValue("DisplayVersion", ver);
                    k.SetValue("Publisher", "Gemini Launcher");
                    k.SetValue("DisplayIcon", InstalledExe + ",0");
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + InstalledExe + "\" --uninstall --quiet");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024) + 1, RegistryValueKind.DWord);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
                using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    run.SetValue("GeminiLauncher", "\"" + InstalledExe + "\" --background");
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Installation failed:\n\n" + ex.Message, "Gemini Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }

        public static void Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall Gemini Launcher?\n\nThe regular Windows Start menu and search will work as before.",
                    "Gemini Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool removeData = quiet || MessageBox.Show("Also delete your settings, pins and search history?", "Gemini Launcher",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            StopOtherInstances();
            try { using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) run?.DeleteValue("GeminiLauncher", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
            try { if (Settings.ElevatedTaskExists) Settings.SetElevatedStartup(false); } catch { }
            if (removeData) { try { Directory.Delete(Settings.Dir, true); } catch { } }
            // Delete our own folder after we exit.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            if (!quiet) MessageBox.Show("Gemini Launcher was removed.", "Gemini Launcher");
        }

        static void StopOtherInstances()
        {
            var me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("GeminiLauncher").Where(p => p.Id != me))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        static void CreateShortcut(string lnk, string target, string description)
        {
            object o = new Native.ShellLink();
            try
            {
                var sl = (Native.IShellLinkW)o;
                sl.SetPath(target);
                sl.SetWorkingDirectory(Path.GetDirectoryName(target));
                sl.SetDescription(description);
                sl.SetIconLocation(target, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                ((Native.IPersistFile)o).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(o); }
        }

        /// <summary>0 = cancel, 1 = install, 2 = run without installing</summary>
        static int Prompt(bool upgrade)
        {
            int result = 0;
            var w = new Window
            {
                Title = "Gemini Launcher", Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13.5
            };
            w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
            w.SetResourceReference(Window.ForegroundProperty, "Fg1");
            w.SourceInitialized += (s, e) => Theme.ApplyBackdrop(w, true, 2);
            var sp = new StackPanel { Margin = new Thickness(28, 22, 28, 22) };
            sp.Children.Add(new TextBlock
            {
                Text = upgrade ? "Update Gemini Launcher" : "Install Gemini Launcher",
                FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFontDisplay")
            });
            var desc = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18),
                Text = (upgrade ? "A version is already installed. It will be replaced; your settings and pins are kept.\n\n" : "") +
                       "Installs for your account only (no admin needed), adds it to the Start menu and Apps list, and starts it with Windows.\n\n" +
                       "After installing, tap the Windows key to search. You can switch the Windows key and Start button takeover on or off in Settings at any time."
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "Fg2");
            sp.Children.Add(desc);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var portable = new Button { Content = "Run without installing", Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SubtleButton") };
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true, Style = (Style)Application.Current.FindResource("FluentButton") };
            var ok = new Button { Content = upgrade ? "Update" : "Install", Width = 100, IsDefault = true, Style = (Style)Application.Current.FindResource("AccentButton") };
            portable.Click += (s, e) => { result = 2; w.Close(); };
            ok.Click += (s, e) => { result = 1; w.Close(); };
            buttons.Children.Add(portable); buttons.Children.Add(cancel); buttons.Children.Add(ok);
            sp.Children.Add(buttons);
            w.Content = sp;
            w.ShowDialog();
            return result;
        }
    }
}
