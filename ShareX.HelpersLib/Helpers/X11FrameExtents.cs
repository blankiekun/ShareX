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
using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace ShareX.HelpersLib;

/// <summary>
/// Tells the window manager how much of an X11 window is margin the program draws around itself. Avalonia draws its shadow inside
/// the window and doesn't say so, which makes KWin's frame geometry, and so every capture of the window, include that margin.
/// </summary>
public static class X11FrameExtents
{
    private const int PropModeReplace = 0;
    private const int FormatCardinal = 32;
    private const int XaCardinal = 6;

    /// <summary>Without it KWin just goes on counting the margin as the window, so a failure is only logged.</summary>
    public static void Set(Window window, Thickness margin)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        double scale = window.RenderScaling;
        // Format 32 properties are arrays of C longs. The order is left, right, top, bottom.
        long[] extents = [(long)Math.Round(margin.Left * scale), (long)Math.Round(margin.Right * scale), (long)Math.Round(margin.Top * scale), (long)Math.Round(margin.Bottom * scale)];
        try
        {
            // Avalonia keeps its own connection private. XCloseDisplay flushes this one's single request.
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            XChangeProperty(display, handle.Handle, XInternAtom(display, "_GTK_FRAME_EXTENTS", false), XaCardinal, FormatCardinal, PropModeReplace, extents, extents.Length);
            XCloseDisplay(display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DebugHelper.WriteException(ex);
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, long[] data, int count);
}
