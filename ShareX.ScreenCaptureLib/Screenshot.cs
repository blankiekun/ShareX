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
    /// <summary>Takes screenshots through KWin's ScreenShot2 or spectacle, see <see cref="SpectacleCapture"/>. Rectangles are in X11 coordinates, like DesktopScreen's.</summary>
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
            return SpectacleCapture.Capture(WindowMode, CaptureCursor).Bitmap;
        }

        /// <summary>Captures a window as it is, without activating or raising it. Only if KWin won't do that does it make the window active first, as spectacle can only capture the active window.</summary>
        public Bitmap CaptureWindow(string windowId)
        {
            SpectacleImage image = KWinScreenShot.TryCaptureWindow(windowId, WindowMode, CaptureCursor, out bool windowGone);

            if (image != null)
            {
                return image.Bitmap;
            }

            if (windowGone || !KWinDesktop.ActivateWindow(windowId))
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

        private SpectacleMode WindowMode => CaptureClientArea ? SpectacleMode.ActiveWindowClientArea :
            CaptureShadow ? SpectacleMode.ActiveWindow : SpectacleMode.ActiveWindowNoShadow;

        internal static Bitmap Crop(Bitmap image, Rectangle rectangle)
        {
            // Not ExtractSubset and Copy: that leaves the crop's height x the whole image's stride for a finalizer to free, each time,
            // which reached 1 GB in 60 crops of a two-monitor desktop before any GC came.
            Bitmap output = new Bitmap(new SKImageInfo(rectangle.Width, rectangle.Height, image.ColorType, image.AlphaType, image.ColorSpace));
            using SKPixmap source = image.PeekPixels();

            if (!source.ReadPixels(output.Info, output.GetPixels(), output.RowBytes, rectangle.X, rectangle.Y))
            {
                output.Dispose();
                return null;
            }

            return output;
        }
    }
}
