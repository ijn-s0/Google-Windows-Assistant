using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace GeminiLauncher
{
    /// <summary>
    /// Windows 11 Fluent look: system acrylic backdrop (DWM), immersive dark mode, rounded corners,
    /// accent color and light/dark palette that follow Windows settings live.
    /// </summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }
        public static bool Transparency { get; private set; } = true;
        public static Color Accent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);
        public static event Action Changed;
        public static string Mode = "system"; // system | dark | light

        static readonly int Build = GetBuild();
        public static bool IsWin11 => Build >= 22000;
        static bool HasSystemBackdrop => Build >= 22621;

        static int GetBuild()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.Parse((string)k.GetValue("CurrentBuildNumber"));
            }
            catch { return Environment.OSVersion.Version.Build; }
        }

        static bool _inited;
        public static void Init()
        {
            if (_inited) { ForceApply(); return; }
            _inited = true;
            Read();
            Apply();
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                    Application.Current.Dispatcher.BeginInvoke(new Action(Refresh));
            };
        }

        public static void Refresh()
        {
            bool d = IsDark, t = Transparency; var a = Accent;
            Read();
            if (d != IsDark || t != Transparency || a != Accent) { Apply(); Changed?.Invoke(); }
        }

        public static void ForceApply()
        {
            Read();
            Apply();
            Changed?.Invoke();
        }

        static void Read()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    // Start/taskbar follow "Windows mode" (SystemUsesLightTheme); fall back to app mode.
                    var light = k?.GetValue("SystemUsesLightTheme") ?? k?.GetValue("AppsUseLightTheme");
                    IsDark = light is int i ? i == 0 : false;
                    var tr = k?.GetValue("EnableTransparency");
                    Transparency = !(tr is int ti) || ti != 0;
                }
            }
            catch { IsDark = false; }
            if (Mode == "dark") IsDark = true;
            if (Mode == "light") IsDark = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (k?.GetValue("AccentColor") is int abgr)
                    {
                        uint v = unchecked((uint)abgr);
                        Accent = Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
                    }
                }
            }
            catch { }
        }

        static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static SolidColorBrush B(uint argb) => B(C(argb));

        static Color Mix(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        static void Apply()
        {
            var r = Application.Current.Resources;
            bool glass = Transparency;
            // Accent tuned for readability, as Windows does (lighter in dark mode, darker in light mode)
            var accentText = IsDark ? Mix(Accent, Colors.White, 0.45) : Mix(Accent, Colors.Black, 0.25);

            if (IsDark)
            {
                r["WindowBg"] = B(glass ? 0x00000000u : 0xFF202020u);
                r["Tint"] = B(glass ? (IsWin11 ? 0x33202020u : 0xCC1F1F1Fu) : 0x00000000u);
                r["Fg1"] = B(0xFFFFFFFFu);
                r["Fg2"] = B(0xC8FFFFFFu);
                r["Fg3"] = B(0x8BFFFFFFu);
                r["CardBg"] = B(0x0DFFFFFFu);
                r["CardStroke"] = B(0x14FFFFFFu);
                r["HoverBg"] = B(0x0FFFFFFFu);
                r["SelBg"] = B(0x15FFFFFFu);
                r["BoxBg"] = B(glass ? 0x0FFFFFFFu : 0xFF2B2B2Bu);
                r["BoxStroke"] = B(0x1AFFFFFFu);
                r["Divider"] = B(0x15FFFFFFu);
                r["ChipBg"] = B(0x12FFFFFFu);
                r["MenuBg"] = B(0xFF2C2C2Cu);
            }
            else
            {
                r["WindowBg"] = B(glass ? 0x00000000u : 0xFFF3F3F3u);
                r["Tint"] = B(glass ? (IsWin11 ? 0x40F3F3F3u : 0xD9F3F3F3u) : 0x00000000u);
                r["Fg1"] = B(0xE4000000u);
                r["Fg2"] = B(0x9E000000u);
                r["Fg3"] = B(0x72000000u);
                r["CardBg"] = B(0x99FFFFFFu);
                r["CardStroke"] = B(0x0F000000u);
                r["HoverBg"] = B(0x0A000000u);
                r["SelBg"] = B(0x10000000u);
                r["BoxBg"] = B(glass ? 0xB3FFFFFFu : 0xFFFFFFFFu);
                r["BoxStroke"] = B(0x14000000u);
                r["Divider"] = B(0x0F000000u);
                r["ChipBg"] = B(0x0A000000u);
                r["MenuBg"] = B(0xFFF9F9F9u);
            }
            r["Accent"] = B(Accent);
            r["AccentText"] = B(accentText);
            r["OnAccent"] = B(IsDark ? 0xFF000000u : 0xFFFFFFFFu);
            var grad = new LinearGradientBrush(C(0xFF4285F4u), C(0xFF9B72CBu), 45);
            grad.GradientStops.Add(new GradientStop(C(0xFFD96570u), 1));
            grad.Freeze();
            r["GeminiBrush"] = grad;
        }

        // ---------------- DWM / composition ----------------
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WCAData data);
        [StructLayout(LayoutKind.Sequential)] struct WCAData { public int Attribute; public IntPtr Data; public int Size; }
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags; public uint Gradient; public int Anim; }

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_MICA_EFFECT = 1029;
        const int DWMSBT_NONE = 1, DWMSBT_TRANSIENTWINDOW = 3; // 3 = Acrylic (the flyout / Start material)

        /// <summary>Call from SourceInitialized and again when the theme changes.</summary>
        public static void ApplyBackdrop(Window w, bool rounded = true, int backdrop = 3, bool popup = false)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var src = HwndSource.FromHwnd(hwnd);

            int dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            if (IsWin11)
            {
                int corner = rounded ? 2 : 3; // DWMWCP_ROUND / ROUNDSMALL
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);
                if (popup)
                {
                    // No title-bar color (removes the accent-colored strip when "Show accent color on title bars" is on)
                    int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
                    DwmSetWindowAttribute(hwnd, 35 /*DWMWA_CAPTION_COLOR*/, ref none, 4);
                    // Subtle neutral border like Start, instead of the accent color
                    int border = IsDark ? 0x00404040 : 0x00D6D6D6; // COLORREF 0x00BBGGRR
                    DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref border, 4);
                }
            }

            if (!Transparency)
            {
                if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = IsDark ? C(0xFF202020u) : C(0xFFF3F3F3u);
                if (HasSystemBackdrop) { int none = DWMSBT_NONE; DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, 4); }
                return;
            }

            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;

            if (HasSystemBackdrop)
            {
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int type = backdrop; // 2 = Mica (app windows), 3 = Acrylic (flyouts / Start)
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4);
            }
            else if (IsWin11)
            {
                // Windows 11 21H2: Mica via undocumented attribute (acrylic API not exposed)
                var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                int on = 1; DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref on, 4);
            }
            else
            {
                // Windows 10: acrylic blur-behind
                var m10 = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m10);
                uint tint = IsDark ? 0xCC1F1F1Fu : 0xCCF3F3F3u; // AABBGGRR
                var accent = new AccentPolicy { State = 4 /*ACCENT_ENABLE_ACRYLICBLURBEHIND*/, Flags = 2, Gradient = tint };
                int size = Marshal.SizeOf(accent);
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, p, false);
                    var data = new WCAData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = p, Size = size };
                    SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally { Marshal.FreeHGlobal(p); }
            }
        }
    }
}
