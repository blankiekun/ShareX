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

#nullable enable

using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX.ScreenCaptureLib;

public static class RegionCaptureTasks
{
    public static async Task<Bitmap?> GetRegionImageAsync(RegionCaptureOptions? options = null)
    {
        AvaloniaRegionCaptureResult? result = await CaptureAsync(options);
        if (result == null)
        {
            return null;
        }

        using (result.Image)
        {
            return result.Image.Copy();
        }
    }

    public static async Task<(Bitmap Image, Rectangle Rectangle)?> GetRegionImageWithRectangleAsync(
        RegionCaptureOptions? options = null)
    {
        AvaloniaRegionCaptureResult? result = await CaptureAsync(options);
        if (result == null)
        {
            return null;
        }

        using (result.Image)
        {
            return (result.Image.Copy(), result.ScreenRectangle);
        }
    }

    public static async Task<(Rectangle Rectangle, KWinWindow? WindowInfo)?> GetRectangleRegionAsync(
        RegionCaptureOptions? options = null)
    {
        AvaloniaRegionCaptureResult? result = await CaptureAsync(options);
        if (result == null)
        {
            return null;
        }

        result.Image.Dispose();
        return (result.ScreenRectangle, result.WindowInfo);
    }

    private static async Task<AvaloniaRegionCaptureResult?> CaptureAsync(RegionCaptureOptions? options)
    {
        options ??= new RegionCaptureOptions();

        AvaloniaRegionCaptureRequest request;

        try
        {
            request = await CreateRequestAsync(options, new ImageEditorOptions(), false, false);
        }
        catch (Exception e) when (e is ScreenCaptureException or KWinScriptException)
        {
            DebugHelper.WriteException(e);
            e.ShowError(false);
            return null;
        }

        return await RegionCaptureIntegration.CaptureAsync(request);
    }

    /// <summary>Freezes the desktop for region capture. Windows are listed first, so ShareX's overlay is not among them.</summary>
    /// <exception cref="ScreenCaptureException">Spectacle failed.</exception>
    /// <exception cref="KWinScriptException">KWin did not answer.</exception>
    public static Task<AvaloniaRegionCaptureRequest> CreateRequestAsync(RegionCaptureOptions options, ImageEditorOptions imageEditorOptions,
        bool enableAnnotations, bool includeCursor)
    {
        return Task.Run(() =>
        {
            KWinDesktop desktop = KWinDesktop.Query();
            SpectacleImage image = SpectacleCapture.Capture(
                options.ActiveMonitorMode ? SpectacleMode.ActiveMonitor : SpectacleMode.AllMonitors, includeCursor);

            IReadOnlyList<KWinOutput> outputs = options.ActiveMonitorMode
                ? [desktop.Outputs.FirstOrDefault(x => x.Name == image.ScreenName) ??
                    desktop.Outputs.FirstOrDefault(x => x.Name == desktop.ActiveOutputName) ??
                    desktop.Outputs[0]]
                : desktop.Outputs;

            return new AvaloniaRegionCaptureRequest
            {
                Screenshot = image.Bitmap,
                Coordinates = desktop.Coordinates.ForImage(outputs, new Size(image.Bitmap.Width, image.Bitmap.Height)),
                Outputs = outputs,
                Windows = desktop.Windows,
                Cursor = desktop.Cursor,
                RegionCaptureOptions = options,
                ImageEditorOptions = imageEditorOptions,
                EnableAnnotations = enableAnnotations
            };
        });
    }
}
