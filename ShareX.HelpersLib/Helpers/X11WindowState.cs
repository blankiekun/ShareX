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
using System;
using System.Runtime.InteropServices;

namespace ShareX.HelpersLib;

/// <summary>States of an X11 window that Avalonia has no property for.</summary>
public static class X11WindowState
{
    private const int PropModeAppend = 2;
    private const int FormatAtom = 32;
    private const int XaAtom = 4;

    /// <summary>
    /// Keeps a window out of Alt+Tab. Avalonia's ShowInTaskbar only covers the taskbar. Call it before the window is shown: the window manager
    /// reads this once, when it takes the window on. Without it the window just stays in the list, so a failure is only logged.
    /// </summary>
    public static void SkipSwitcher(Window window)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return;
        try
        {
            // Avalonia keeps its own connection private. XCloseDisplay flushes this one's single request.
            IntPtr display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            // Format 32 properties are arrays of C longs. Avalonia keeps what's already in this property when it adds its own states.
            long[] state = [(long)XInternAtom(display, "_KDE_NET_WM_STATE_SKIP_SWITCHER", false)];
            XChangeProperty(display, handle.Handle, XInternAtom(display, "_NET_WM_STATE", false), XaAtom, FormatAtom, PropModeAppend, state, state.Length);
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
