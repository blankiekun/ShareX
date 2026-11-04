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

using Avalonia.Controls;
using ShareX.AvaloniaUI.Integration;
using SkiaSharp;
using System;
using System.Drawing;
using System.IO;
using Image = SkiaSharp.SKBitmap;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX.HelpersLib
{
    public class PrintHelper
    {
        // Sizes are in hundredths of an inch; a PDF page is measured in points, 72 to the inch.
        private const float PointsPerUnit = 0.72f;

        public Image Image { get; private set; }
        public PrintSettings Settings { get; set; }

        private bool Printable => Settings != null && Image != null;

        public PrintHelper(Image image)
        {
            Image = image;
        }

        public void ShowPreview()
        {
            if (Printable) PrintPreviewWindow.ShowPreview(RenderPreviewPage, Print);
        }

        // The preview runs before the print dialog, so the paper chosen there isn't known yet.
        internal (SKBitmap Bitmap, bool HasMore) RenderPreviewPage(int pageIndex) =>
            (RenderImagePage(Image, PortalPrint.DefaultPageSize, Settings), false);

        /// <summary>Shows the desktop's print dialog, then prints on the paper chosen there. Returns false if cancelled or failed.</summary>
        public bool Print()
        {
            if (!Printable) return false;

            try
            {
                return DesktopServices.Run(() => PortalPrint.PrintAsync(ParentWindow(), "ShareX", pageSize => WritePdfFile(Image, pageSize, Settings)));
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Printing failed");
                MessageBox.Show(string.Format(Localization.Strings.PrintHelper_Print_failed, e.Message),
                    Localization.Strings.PrintForm_LoadSettings_Print, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // The portal's form for a parent window. ShareX runs under XWayland, so an X11 window ID is what it has.
        private static string ParentWindow()
        {
            Window window = DesktopServices.GetWindow();
            return window.IsVisible && window.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle ? $"x11:{handle.Handle:x}" : "";
        }

        // The image goes in at its own resolution, so what prints isn't limited by a rasterized page.
        internal static void WritePdf(Stream stream, SKBitmap image, Size pageSize, PrintSettings settings)
        {
            using SKDocument document = SKDocument.CreatePdf(stream) ?? throw new NotSupportedException("This SkiaSharp build can't write PDF files.");
            SKCanvas canvas = document.BeginPage(pageSize.Width * PointsPerUnit, pageSize.Height * PointsPerUnit);
            canvas.Scale(PointsPerUnit);
            DrawImagePage(canvas, image, pageSize, settings);
            document.EndPage();
            document.Close();
        }

        private static string WritePdfFile(SKBitmap image, Size pageSize, PrintSettings settings)
        {
            string path = Path.Combine(Path.GetTempPath(), $"sharex-print-{Guid.NewGuid():N}.pdf");
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            };

            try
            {
                using FileStream stream = new(path, options);
                WritePdf(stream, image, pageSize, settings);
                return path;
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }

        internal static SKBitmap RenderImagePage(SKBitmap source, Size pageSize, PrintSettings settings)
        {
            SKBitmap page = SkiaImageHelpers.CreateBitmap(pageSize.Width * 3, pageSize.Height * 3);
            try
            {
                using SKCanvas canvas = new(page);
                canvas.Clear(SKColors.White);
                canvas.Scale(3, 3);
                DrawImagePage(canvas, source, pageSize, settings);
                return page;
            }
            catch { page.Dispose(); throw; }
        }

        private static void DrawImagePage(SKCanvas canvas, SKBitmap source, Size pageSize, PrintSettings settings)
        {
            SKBitmap rotated = null;
            SKBitmap image = source;
            Rectangle rectangle = new(0, 0, pageSize.Width, pageSize.Height);
            rectangle.Inflate(-settings.Margin, -settings.Margin);
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            try
            {
                if (settings.AutoRotateImage && ((rectangle.Width > rectangle.Height && source.Width < source.Height) ||
                    (rectangle.Width < rectangle.Height && source.Width > source.Height)))
                {
                    rotated = source.Copy();
                    SkiaImageHelpers.RotateFlipInPlace(rotated, 1);
                    image = rotated;
                }
                canvas.ClipRect(rectangle.ToSKRect());
                if (settings.AutoScaleImage)
                {
                    float scale = Math.Min(rectangle.Width / (float)image.Width, rectangle.Height / (float)image.Height);
                    if (!settings.AllowEnlargeImage) scale = Math.Min(1, scale);
                    float width = image.Width * scale, height = image.Height * scale;
                    float x = rectangle.X, y = rectangle.Y;
                    if (settings.CenterImage)
                    {
                        x += (rectangle.Width - width) / 2;
                        y += (rectangle.Height - height) / 2;
                    }
                    canvas.DrawImage(image, x, y, width, height);
                }
                else
                {
                    canvas.DrawBitmap(image, rectangle.X, rectangle.Y);
                }
            }
            finally { rotated?.Dispose(); }
        }
    }
}
