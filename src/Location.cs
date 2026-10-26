using System;
using System.Device.Location;

namespace GeminiLauncher
{
    /// <summary>Optional approximate location (Windows location service) for "near me" answers via Google Maps.</summary>
    public static class Location
    {
        static GeoCoordinateWatcher _w;
        public static GeoCoordinate Current { get; private set; }

        public static void Enable(bool on)
        {
            try
            {
                if (on && _w == null)
                {
                    _w = new GeoCoordinateWatcher(GeoPositionAccuracy.Default) { MovementThreshold = 500 };
                    _w.PositionChanged += (s, e) => { if (!e.Position.Location.IsUnknown) Current = e.Position.Location; };
                    _w.Start(false);
                }
                else if (!on && _w != null)
                {
                    _w.Stop(); _w.Dispose(); _w = null; Current = null;
                }
            }
            catch { }
        }
    }
}
