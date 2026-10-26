using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeminiLauncher
{
    /// <summary>Loads shell icons (apps, UWP logos, file-type icons) on a background STA thread with a small cache.</summary>
    public static class IconLoader
    {
        static readonly BlockingCollection<(string key, string parsingName, Action<ImageSource> done)> Queue =
            new BlockingCollection<(string, string, Action<ImageSource>)>();
        static readonly ConcurrentDictionary<string, ImageSource> Cache = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static Thread _worker;
        public static int Size = 48;

        static readonly HashSet<string> UniqueIconExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };

        static void EnsureWorker()
        {
            if (_worker != null) return;
            _worker = new Thread(() =>
            {
                foreach (var job in Queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (!Cache.TryGetValue(job.key, out var img))
                        {
                            img = Load(job.parsingName);
                            if (img != null)
                            {
                                if (Cache.Count > 600) Cache.Clear();
                                Cache[job.key] = img;
                            }
                        }
                        if (img != null) job.done(img);
                    }
                    catch { }
                }
            })
            { IsBackground = true, Name = "IconLoader" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        /// <summary>Assigns item.Icon asynchronously.</summary>
        public static void Request(ResultItem item)
        {
            string parsing = null, key = null;
            switch (item.Kind)
            {
                case ItemKind.App:
                    parsing = "shell:AppsFolder\\" + item.Target; key = "app:" + item.Target; break;
                case ItemKind.File:
                    parsing = item.Target;
                    var ext = Path.GetExtension(item.Target ?? "");
                    key = UniqueIconExt.Contains(ext) || ext == "" ? "f:" + item.Target : "ext:" + ext;
                    break;
                case ItemKind.Folder:
                    parsing = item.Target; key = "folder:" + item.Target; break;
                case ItemKind.Url:
                    item.Icon = Favicon(item.Target); return;
                default: return;
            }
            if (parsing == null) return;
            if (Cache.TryGetValue(key, out var cached)) { item.Icon = cached; return; }
            EnsureWorker();
            var disp = Application.Current.Dispatcher;
            Queue.Add((key, parsing, img => disp.BeginInvoke(new Action(() => item.Icon = img))));
        }

        static ImageSource Favicon(string url)
        {
            try
            {
                var host = new Uri(url).Host;
                var key = "fav:" + host;
                if (Cache.TryGetValue(key, out var c)) return c;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri("https://www.google.com/s2/favicons?sz=64&domain=" + Uri.EscapeDataString(host));
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                Cache[key] = bi;
                return bi;
            }
            catch { return null; }
        }

        static ImageSource Load(string parsingName)
        {
            Native.IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                int hr = Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out f);
                if (hr != 0 || f == null) return null;
                hr = f.GetImage(new Native.SIZE(Size, Size), Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                return HBitmapToSource(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        /// <summary>Copies a 32bpp HBITMAP into a frozen premultiplied BGRA BitmapSource, preserving alpha.</summary>
        static BitmapSource HBitmapToSource(IntPtr hbm)
        {
            var bmp = new Native.BITMAP();
            Native.GetObject(hbm, Marshal.SizeOf(typeof(Native.BITMAP)), ref bmp);
            int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            var bits = new byte[w * h * 4];
            IntPtr hdc = Native.GetDC(IntPtr.Zero);
            try { Native.GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bi, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, hdc); }

            // If the image has no alpha at all (some legacy icons), make it opaque.
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
            src.Freeze();
            return src;
        }
    }
}
