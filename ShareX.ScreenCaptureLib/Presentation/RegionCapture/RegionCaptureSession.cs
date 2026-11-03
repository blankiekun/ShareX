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

using Avalonia;
using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ShareX.ScreenCaptureLib.Presentation.RegionCapture;

/// <summary>
/// One region capture across all monitors: a window per monitor, each showing its crop of the frozen
/// screenshot, sharing a selection held in image coordinates. Each monitor's annotations live in its own
/// window, so a shape can't cross monitors, and the tool, undo history and toolbar are per monitor.
/// </summary>
internal sealed class RegionCaptureSession
{
    private readonly TaskCompletionSource<AvaloniaRegionCaptureResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<RegionCaptureWindow> _windows = [];
    private AvaloniaRegionCaptureResult? _result;
    private Exception? _failure;
    private int _openWindows;
    private bool _closing;

    public RegionCaptureSession(AvaloniaRegionCaptureRequest request)
    {
        Request = request;
        ImageSize = new Size(request.Screenshot.Width, request.Screenshot.Height);
        Candidates = request.RegionCaptureOptions.DetectWindows
            // ShareX's own menus can still be closing, and are not in the frozen screenshot.
            ? request.Windows.Where(x => x.IsNormal || x.Pid != Environment.ProcessId)
                .Select(x => new SimpleWindowInfo(x, request.Coordinates.LogicalToImage(x.FrameGeometry))).ToList()
            : [];

        DrawingRectangle cursor = request.Coordinates.LogicalToImage(new DrawingRectangle(request.Cursor, new System.Drawing.Size(1, 1)));
        PointerPoint = new Point(
            Math.Clamp(cursor.X, 0, request.Screenshot.Width - 1),
            Math.Clamp(cursor.Y, 0, request.Screenshot.Height - 1));
    }

    public AvaloniaRegionCaptureRequest Request { get; }

    public Size ImageSize { get; }

    /// <summary>Windows that can be picked by hovering, top of the stack first.</summary>
    public IReadOnlyList<SimpleWindowInfo> Candidates { get; }

    public Rect Selection { get; private set; }

    public SimpleWindowInfo? SelectedCandidate { get; private set; }

    public Rect Hover { get; private set; }

    public SimpleWindowInfo? HoverCandidate { get; private set; }

    public bool IsSelectionPending { get; private set; }

    public bool ResizeNodesVisible { get; private set; }

    public Point PointerPoint { get; set; }

    public Task<AvaloniaRegionCaptureResult?> RunAsync()
    {
        foreach (KWinOutput output in Request.Outputs)
        {
            DrawingRectangle imageRectangle = DrawingRectangle.Intersect(
                Request.Coordinates.LogicalToImage(output.Geometry),
                new DrawingRectangle(0, 0, Request.Screenshot.Width, Request.Screenshot.Height));

            if (!imageRectangle.IsEmpty)
            {
                SKBitmap crop = Screenshot.Crop(Request.Screenshot, imageRectangle);
                _windows.Add(new RegionCaptureWindow(this, crop, imageRectangle, Request.Coordinates.LogicalToX11(output.Geometry),
                    imageRectangle.Contains((int)PointerPoint.X, (int)PointerPoint.Y)));
            }
        }

        if (_windows.Count == 0)
        {
            throw new ScreenCaptureException("The screenshot does not cover any monitor KWin reported.");
        }

        _openWindows = _windows.Count;

        // The window with the cursor goes last, so that it is the one that ends up with keyboard focus.
        foreach (RegionCaptureWindow window in _windows.OrderBy(x => x.ImageRectangle.Contains((int)PointerPoint.X, (int)PointerPoint.Y)))
        {
            window.ShowOnMonitor();
        }

        // Every window has its own crop now.
        Request.Screenshot.Dispose();

        return _completion.Task;
    }

    public void SetSelection(Rect selection, SimpleWindowInfo? candidate)
    {
        Selection = selection;
        SelectedCandidate = candidate;
        ApplySharedState();
    }

    public void SetHover(Rect hover, SimpleWindowInfo? candidate)
    {
        Hover = hover;
        HoverCandidate = candidate;
        ApplySharedState();
    }

    public void SetSelectionPending(bool pending)
    {
        IsSelectionPending = pending;
        ApplySharedState();
    }

    public void SetResizeNodesVisible(bool visible)
    {
        ResizeNodesVisible = visible;
        ApplySharedState();
    }

    public void UpdateHud()
    {
        foreach (RegionCaptureWindow window in _windows)
        {
            window.UpdateHud(PointerPoint);
        }
    }

    public SKColor GetPixel(int x, int y)
    {
        return _windows.FirstOrDefault(window => window.ImageRectangle.Contains(x, y))?.GetPixel(x, y) ?? SKColors.Transparent;
    }

    public DrawingRectangle? GetMonitorImageRectangle(Point imagePoint)
    {
        return _windows.FirstOrDefault(window => window.ImageRectangle.Contains((int)imagePoint.X, (int)imagePoint.Y))?.ImageRectangle;
    }

    /// <summary>The window showing the selection's size: below it, or above it if that is off the monitor.</summary>
    public RegionCaptureWindow? GetSelectionInfoWindow()
    {
        int left = (int)Selection.Left;
        return _windows.FirstOrDefault(window => window.ImageRectangle.Contains(left, (int)Selection.Bottom - 1)) ??
            _windows.FirstOrDefault(window => window.ImageRectangle.Contains(left, (int)Selection.Top)) ??
            _windows.FirstOrDefault(window => window.ImageRectangle.IntersectsWith(ToDrawing(Selection)));
    }

    public void Complete(Rect selection, bool includeWindowInfo)
    {
        if (_closing || !RegionSelectionOverlay.IsValid(selection))
        {
            return;
        }

        DrawingRectangle rectangle = ToDrawing(selection);
        rectangle.Intersect(new DrawingRectangle(0, 0, (int)ImageSize.Width, (int)ImageSize.Height));

        SKBitmap? output = rectangle.IsEmpty ? null : ComposeImage(rectangle);
        if (output == null)
        {
            Cancel();
            return;
        }

        KWinWindow? window = includeWindowInfo ? FindWindow(rectangle) : null;
        DrawingRectangle x11Rectangle = Request.Coordinates.ImageToX11(rectangle);
        _result = new AvaloniaRegionCaptureResult(output, x11Rectangle, window, _windows.Any(x => x.IsDirty));
        RegionCaptureIntegration.LastRegionRectangle = x11Rectangle;
        CloseAll();
    }

    public void Cancel()
    {
        _result = null;
        CloseAll();
    }

    public void Fail(Exception exception)
    {
        _failure ??= exception;
        Cancel();
    }

    public void OnWindowClosed(RegionCaptureWindow window, Exception? closeException)
    {
        _failure ??= closeException;
        _openWindows--;

        // A window closed from outside, for example with Alt+F4, ends the whole capture.
        if (!_closing)
        {
            Cancel();
        }

        if (_openWindows > 0)
        {
            return;
        }

        if (_failure != null)
        {
            _result?.Image.Dispose();
            _completion.TrySetException(_failure);
        }
        else
        {
            _completion.TrySetResult(_result);
        }
    }

    private void CloseAll()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;

        foreach (RegionCaptureWindow window in _windows)
        {
            window.CloseForSession();
        }
    }

    private void ApplySharedState()
    {
        foreach (RegionCaptureWindow window in _windows)
        {
            window.ApplySharedState();
        }
    }

    private SKBitmap? ComposeImage(DrawingRectangle rectangle)
    {
        List<(DrawingRectangle Part, SKBitmap Image)> parts = [];

        try
        {
            foreach (RegionCaptureWindow window in _windows)
            {
                DrawingRectangle part = DrawingRectangle.Intersect(rectangle, window.ImageRectangle);
                SKBitmap? image = part.IsEmpty ? null : window.GetSnapshot(part);
                if (image != null)
                {
                    parts.Add((part, image));
                }
            }

            if (parts.Count == 0)
            {
                return null;
            }

            if (parts.Count == 1 && parts[0].Part == rectangle)
            {
                SKBitmap only = parts[0].Image;
                parts.Clear();
                return only;
            }

            // Space between monitors of different sizes stays transparent, as it does in spectacle's own image.
            SKBitmap output = new(new SKImageInfo(rectangle.Width, rectangle.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using SKCanvas canvas = new(output);
            canvas.Clear(SKColors.Transparent);

            foreach ((DrawingRectangle part, SKBitmap image) in parts)
            {
                canvas.DrawBitmap(image, part.X - rectangle.X, part.Y - rectangle.Y);
            }

            return output;
        }
        finally
        {
            foreach ((_, SKBitmap image) in parts)
            {
                image.Dispose();
            }
        }
    }

    private KWinWindow? FindWindow(DrawingRectangle selection)
    {
        if (SelectedCandidate != null)
        {
            return SelectedCandidate.Window;
        }

        // Prefer the window under the cursor (the clicked window), because the selection center can be covered by another window.
        DrawingPoint point = new((int)PointerPoint.X, (int)PointerPoint.Y);

        if (!selection.Contains(point))
        {
            point = new DrawingPoint(selection.Left + selection.Width / 2, selection.Top + selection.Height / 2);
        }

        return Candidates.FirstOrDefault(candidate => candidate.Rectangle.Contains(point))?.Window;
    }

    private static DrawingRectangle ToDrawing(Rect rectangle)
    {
        int left = (int)Math.Floor(rectangle.Left);
        int top = (int)Math.Floor(rectangle.Top);
        return new DrawingRectangle(left, top, (int)Math.Ceiling(rectangle.Right) - left, (int)Math.Ceiling(rectangle.Bottom) - top);
    }
}
