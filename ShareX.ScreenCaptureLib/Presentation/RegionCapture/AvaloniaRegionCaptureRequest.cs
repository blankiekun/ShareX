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
using SkiaSharp;
using System.Collections.Generic;
using System.Drawing;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

public sealed class AvaloniaRegionCaptureRequest
{
    /// <summary>Frozen screenshot. Ownership transfers to the capture session.</summary>
    public required SKBitmap Screenshot { get; init; }

    /// <summary>Converts <see cref="Screenshot"/>'s pixels to and from the desktop.</summary>
    public required DesktopCoordinates Coordinates { get; init; }

    /// <summary>The monitors <see cref="Screenshot"/> covers. Each gets an overlay window.</summary>
    public required IReadOnlyList<KWinOutput> Outputs { get; init; }

    /// <summary>Windows to snap to, top of the stack first.</summary>
    public required IReadOnlyList<KWinWindow> Windows { get; init; }

    /// <summary>Cursor position in KWin logical coordinates.</summary>
    public required Point Cursor { get; init; }

    public required RegionCaptureOptions RegionCaptureOptions { get; init; }

    public required ImageEditorOptions ImageEditorOptions { get; init; }

    public bool EnableAnnotations { get; init; } = true;
}
