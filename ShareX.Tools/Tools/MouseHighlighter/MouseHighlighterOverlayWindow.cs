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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using ShareX.HelpersLib;
using System.Runtime.InteropServices;
using DrawingPoint = System.Drawing.Point;

namespace ShareX.Tools;

// One small window that moves with the pointer. Nothing repaints as it moves, and a window at a monitor's edge just shows on both.
// A full-monitor overlay per screen would have KWin blend a screen-sized surface for as long as the tool is on.
internal sealed class MouseHighlighterOverlayWindow : Window
{
    private readonly Ellipse _circle = new();
    private int _radius;
    private DrawingPoint? _pointer;

    public MouseHighlighterOverlayWindow()
    {
        Title = "ShareX - Mouse highlighter overlay";
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        IsHitTestVisible = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Content = _circle;
        ScalingChanged += (_, _) => ApplySize();
        MakeUnmanaged();
        X11InputShape.SetEmpty(this);
    }

    public void Apply(MouseHighlighterOptions options)
    {
        _circle.Fill = new SolidColorBrush(Color.FromArgb(options.AlwaysColor.A, options.AlwaysColor.R, options.AlwaysColor.G, options.AlwaysColor.B));
        _radius = options.Radius;
        ApplySize();
        if (_pointer is DrawingPoint pointer) MoveTo(pointer);
    }

    /// <param name="pointer">Where the pointer is on the X11 desktop.</param>
    public void MoveTo(DrawingPoint pointer)
    {
        _pointer = pointer;
        Position = new PixelPoint(pointer.X - _radius, pointer.Y - _radius);
        if (!IsVisible) Show();
    }

    private void ApplySize()
    {
        Width = Height = _radius * 2 / RenderScaling;
    }

    // KWin keeps a managed window out of a panel's reserved strip and stacks it under panels and on-screen displays. It leaves an
    // override-redirect window exactly where it's put, on top of everything, and out of the taskbar and the window switcher.
    private void MakeUnmanaged()
    {
        if (TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        try
        {
            // Avalonia keeps its own connection private. XCloseDisplay flushes this one before Avalonia maps the window.
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            XSetWindowAttributes attributes = new() { OverrideRedirect = 1 };
            XChangeWindowAttributes(display, handle.Handle, CWOverrideRedirect, ref attributes);
            XCloseDisplay(display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DebugHelper.WriteException(ex);
        }
    }

    private const ulong CWOverrideRedirect = 1 << 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public IntPtr BackgroundPixmap;
        public IntPtr BackgroundPixel;
        public IntPtr BorderPixmap;
        public IntPtr BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public IntPtr BackingPlanes;
        public IntPtr BackingPixel;
        public int SaveUnder;
        public IntPtr EventMask;
        public IntPtr DoNotPropagateMask;
        public int OverrideRedirect;
        public IntPtr Colormap;
        public IntPtr Cursor;
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XChangeWindowAttributes(IntPtr display, IntPtr window, ulong valueMask, ref XSetWindowAttributes attributes);
}
