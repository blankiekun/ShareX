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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ShareX.HelpersLib;

/// <summary>
/// Converts between the three coordinate spaces of a KDE Wayland desktop, which differ when an output's
/// scale is not 1:
/// <list type="bullet">
/// <item>KWin logical: what KWin scripts report, and what window data is kept in.</item>
/// <item>X11: what ShareX's own windows, Avalonia's Screens and DesktopScreen use under XWayland.
/// logical × the largest output scale, or logical × 1 when XwaylandClientsScale is off.</item>
/// <item>Image: pixels of a screenshot, measured from its top left corner.</item>
/// </list>
/// In mixed scale, KWin's all-monitors image is logical × the largest scale on every monitor, so a
/// low-DPI monitor is upscaled and can't be captured at native resolution that way.
/// </summary>
public sealed class DesktopCoordinates
{
    /// <summary>Logical area the image covers.</summary>
    public Rectangle LogicalBounds { get; }

    public double X11Scale { get; }

    /// <summary>Image pixels per logical pixel.</summary>
    public double ImageScale { get; }

    private DesktopCoordinates(Rectangle logicalBounds, double x11Scale, double imageScale)
    {
        LogicalBounds = logicalBounds;
        X11Scale = x11Scale;
        ImageScale = imageScale;
    }

    public static DesktopCoordinates ForDesktop(IReadOnlyCollection<KWinOutput> outputs, bool xwaylandClientsScale)
    {
        if (outputs.Count == 0)
        {
            throw new ArgumentException("KWin reported no outputs.", nameof(outputs));
        }

        double largestScale = outputs.Max(x => x.Scale);
        return new DesktopCoordinates(outputs.Select(x => x.Geometry).Combine(), xwaylandClientsScale ? largestScale : 1, largestScale);
    }

    /// <summary>
    /// The same desktop, for a captured image of <paramref name="imageSize"/> covering <paramref name="outputs"/>.
    /// The scale is measured from the image rather than predicted from the output scales.
    /// </summary>
    public DesktopCoordinates ForImage(IReadOnlyCollection<KWinOutput> outputs, Size imageSize)
    {
        Rectangle bounds = outputs.Select(x => x.Geometry).Combine();
        return new DesktopCoordinates(bounds, X11Scale, (double)imageSize.Width / bounds.Width);
    }

    public Rectangle LogicalToImage(Rectangle logical)
    {
        return Scale(logical, LogicalBounds.X, LogicalBounds.Y, ImageScale);
    }

    public Rectangle LogicalToX11(Rectangle logical)
    {
        return Scale(logical, 0, 0, X11Scale);
    }

    public Point LogicalToX11(Point logical)
    {
        return new Point((int)Math.Round(logical.X * X11Scale), (int)Math.Round(logical.Y * X11Scale));
    }

    public Rectangle X11ToImage(Rectangle x11)
    {
        return FromEdges(
            (x11.Left / X11Scale - LogicalBounds.X) * ImageScale,
            (x11.Top / X11Scale - LogicalBounds.Y) * ImageScale,
            (x11.Right / X11Scale - LogicalBounds.X) * ImageScale,
            (x11.Bottom / X11Scale - LogicalBounds.Y) * ImageScale);
    }

    public Rectangle ImageToX11(Rectangle image)
    {
        return FromEdges(
            (image.Left / ImageScale + LogicalBounds.X) * X11Scale,
            (image.Top / ImageScale + LogicalBounds.Y) * X11Scale,
            (image.Right / ImageScale + LogicalBounds.X) * X11Scale,
            (image.Bottom / ImageScale + LogicalBounds.Y) * X11Scale);
    }

    /// <summary>Reads [KScreen] XwaylandClientsScale from the text of kdeglobals. KDE's default is true.</summary>
    public static bool ParseXwaylandClientsScale(string? kdeglobals)
    {
        if (kdeglobals == null)
        {
            return true;
        }

        bool inSection = false;

        foreach (string rawLine in kdeglobals.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.StartsWith('['))
            {
                inSection = line == "[KScreen]";
            }
            else if (inSection && line.StartsWith("XwaylandClientsScale", StringComparison.Ordinal))
            {
                int separator = line.IndexOf('=');
                return separator < 0 || !line[(separator + 1)..].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    // Rounds edges rather than position and size, so neighbouring monitors stay exactly adjacent.
    private static Rectangle Scale(Rectangle rectangle, int originX, int originY, double scale)
    {
        return FromEdges(
            (rectangle.Left - originX) * scale,
            (rectangle.Top - originY) * scale,
            (rectangle.Right - originX) * scale,
            (rectangle.Bottom - originY) * scale);
    }

    private static Rectangle FromEdges(double left, double top, double right, double bottom)
    {
        int x = (int)Math.Round(left);
        int y = (int)Math.Round(top);
        return new Rectangle(x, y, (int)Math.Round(right) - x, (int)Math.Round(bottom) - y);
    }
}
