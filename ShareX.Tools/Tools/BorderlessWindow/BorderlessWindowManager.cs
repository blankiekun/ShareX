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
using System.Collections.Concurrent;
using System.Drawing;

namespace ShareX.Tools;

/// <summary>Removes decorations through KWin, the only thing that can touch another program's window on Wayland.</summary>
public static class BorderlessWindowManager
{
    // shortcut: only in memory, so after ShareX restarts, a window filling the working area gets its frame back but keeps its size.
    private static readonly ConcurrentDictionary<string, Rectangle> PreviousGeometry = new();

    /// <returns>False when no window has that title.</returns>
    public static bool ToggleBorderlessWindow(string windowTitle, bool useWorkingArea = false)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        KWinWindow? window = KWinDesktop.Query().FindWindowByTitle(windowTitle, Environment.ProcessId);

        return window != null && Toggle(window.Id, useWorkingArea);
    }

    /// <returns>False when there is no window to toggle.</returns>
    public static bool ToggleActiveWindow(bool useWorkingArea = false)
    {
        KWinWindow? window = KWinDesktop.Query().ActiveOrLastUsedWindow(Environment.ProcessId);

        return window != null && Toggle(window.Id, useWorkingArea);
    }

    private static bool Toggle(string windowId, bool useWorkingArea)
    {
        Rectangle? restore = PreviousGeometry.TryGetValue(windowId, out Rectangle saved) ? saved : null;

        switch (KWinDesktop.ToggleBorderless(windowId, useWorkingArea, restore, out Rectangle previous))
        {
            case KWinBorderlessResult.NotFound:
                return false;
            case KWinBorderlessResult.NotSupported:
                throw new InvalidOperationException(Localization.Strings.BorderlessWindowManager_Not_supported_by_KWin);
            case KWinBorderlessResult.MadeBorderless:
                if (!previous.IsEmpty)
                {
                    PreviousGeometry[windowId] = previous;
                }

                return true;
            default:
                PreviousGeometry.TryRemove(windowId, out _);
                return true;
        }
    }
}
