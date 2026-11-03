#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using SkiaSharp;
using System.Drawing;
using System.Linq;
using System.Threading;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>Takes screenshots through <see cref="SpectacleCapture"/>. Rectangles are in X11 coordinates, like DesktopScreen's.</summary>
    public class Screenshot
    {
        public bool CaptureCursor { get; set; } = false;
        public bool CaptureClientArea { get; set; } = false;
        public bool CaptureShadow { get; set; } = false;

        public Bitmap CaptureRectangle(Rectangle rect)
        {
            KWinDesktop desktop = KWinDesktop.Query();

            using Bitmap image = CaptureFullscreen();
            DesktopCoordinates coordinates = desktop.Coordinates.ForImage(desktop.Outputs, new Size(image.Width, image.Height));
            Rectangle crop = Rectangle.Intersect(coordinates.X11ToImage(rect), new Rectangle(0, 0, image.Width, image.Height));

            return crop.IsEmpty ? null : Crop(image, crop);
        }

        public Bitmap CaptureFullscreen()
        {
            return SpectacleCapture.Capture(SpectacleMode.AllMonitors, CaptureCursor).Bitmap;
        }

        public Bitmap CaptureActiveWindow()
        {
            SpectacleMode mode = CaptureClientArea ? SpectacleMode.ActiveWindowClientArea :
                CaptureShadow ? SpectacleMode.ActiveWindow : SpectacleMode.ActiveWindowNoShadow;

            return SpectacleCapture.Capture(mode, CaptureCursor).Bitmap;
        }

        /// <summary>Captures a window by making it active first. Spectacle can only capture the active window.</summary>
        public Bitmap CaptureWindow(string windowId)
        {
            if (!KWinDesktop.ActivateWindow(windowId))
            {
                return null;
            }

            Thread.Sleep(100);

            return CaptureActiveWindow();
        }

        /// <summary>Captures the monitor with the active window, which is not always the one with the cursor.</summary>
        public Bitmap CaptureActiveMonitor()
        {
            return SpectacleCapture.Capture(SpectacleMode.ActiveMonitor, CaptureCursor).Bitmap;
        }

        internal static Bitmap Crop(Bitmap image, Rectangle rectangle)
        {
            using Bitmap subset = new Bitmap();
            image.ExtractSubset(subset, new SKRectI(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
            return subset.Copy();
        }
    }
}
