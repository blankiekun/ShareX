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

using Avalonia.Controls;
using ShareX.HelpersLib;
using System;
using System.Runtime.InteropServices;

namespace ShareX.HelpersLib;

/// <summary>Limits where an X11 window takes pointer input. It still draws everywhere: this is the input shape, not the bounding shape.</summary>
public static class X11InputShape
{
    private const int ShapeSet = 0;
    private const int ShapeInput = 2;
    private const int Unsorted = 0;

    /// <summary>Only this rectangle, in window pixels, takes input. Without it the window just keeps taking clicks everywhere, so a failure is only logged.</summary>
    public static void Set(Window window, int x, int y, int width, int height)
    {
        Apply(window, [new((short)x, (short)y, (ushort)width, (ushort)height)]);
    }

    /// <summary>The window takes no input at all, so every click and hover reaches what is under it. A failure leaves it taking clicks, and is only logged too.</summary>
    public static void SetEmpty(Window window)
    {
        Apply(window, []);
    }

    /// <summary>The window takes input everywhere again, also after a later resize. A failure leaves the shape as it was, and is only logged too.</summary>
    public static void Reset(Window window)
    {
        Apply(window, null);
    }

    private static void Apply(Window window, XRectangle[]? rectangles)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        try
        {
            // Avalonia keeps its own connection private. XCloseDisplay flushes this one's single request.
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            if (rectangles == null)
            {
                // Setting no mask removes the shape, which is different from an empty one.
                XShapeCombineMask(display, handle.Handle, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
            }
            else
            {
                XShapeCombineRectangles(display, handle.Handle, ShapeInput, 0, 0, rectangles, rectangles.Length, ShapeSet, Unsorted);
            }
            XCloseDisplay(display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DebugHelper.WriteException(ex);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct XRectangle(short X, short Y, ushort Width, ushort Height);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int xOffset, int yOffset, XRectangle[] rectangles, int count, int operation, int ordering);

    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineMask(IntPtr display, IntPtr window, int kind, int xOffset, int yOffset, IntPtr pixmap, int operation);
}
