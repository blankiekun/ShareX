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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace ShareX.HelpersLib;

public sealed record KWinOutput(string Name, Rectangle Geometry, double Scale);

/// <summary>A window as KWin sees it. Geometry is in KWin logical coordinates.</summary>
public sealed record KWinWindow(string Id, string Caption, string ResourceClass, int Pid, Rectangle FrameGeometry,
    Rectangle ClientGeometry, bool IsNormal, bool IsFullscreen, bool IsKeepAbove)
{
    public string? ProcessName
    {
        get
        {
            try
            {
                return File.ReadAllText($"/proc/{Pid}/comm").TrimEnd();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}

/// <summary>A snapshot of KWin's outputs, windows and cursor, read through a generated KWin script.</summary>
public sealed class KWinDesktop
{
    // workspace.stackingOrder also lists minimized windows, windows on other virtual desktops, windows still
    // animating closed, and KWin's own on-screen messages. Those have pid -1 and a normal windowType, so windowType
    // can't filter them out.
    private const string QueryScript = """
        function rect(g) { return { x: g.x, y: g.y, width: g.width, height: g.height }; }
        var windows = [];
        var stack = workspace.stackingOrder;
        for (var i = 0; i < stack.length; i++) {
            var w = stack[i];
            if (w.minimized || w.deleted || w.pid === -1 || w.desktopWindow) continue;
            if (!w.onAllDesktops && w.desktops.indexOf(workspace.currentDesktop) < 0) continue;
            windows.push({
                id: String(w.internalId), caption: w.caption, resourceClass: w.resourceClass, pid: w.pid,
                frame: rect(w.frameGeometry), client: rect(w.clientGeometry),
                normal: (w.normalWindow || w.dialog) && !w.skipTaskbar, fullscreen: w.fullScreen, keepAbove: w.keepAbove
            });
        }
        report(JSON.stringify({
            outputs: workspace.screens.map(function (s) { return { name: s.name, geometry: rect(s.geometry), scale: s.devicePixelRatio }; }),
            activeOutput: workspace.activeScreen ? workspace.activeScreen.name : null,
            activeWindow: workspace.activeWindow ? String(workspace.activeWindow.internalId) : null,
            cursor: { x: workspace.cursorPos.x, y: workspace.cursorPos.y },
            windows: windows
        }));
        """;

    public required IReadOnlyList<KWinOutput> Outputs { get; init; }

    /// <summary>Top of the stack first.</summary>
    public required IReadOnlyList<KWinWindow> Windows { get; init; }

    public required Point Cursor { get; init; }

    public string? ActiveOutputName { get; init; }

    public string? ActiveWindowId { get; init; }

    public required DesktopCoordinates Coordinates { get; init; }

    public KWinWindow? ActiveWindow => Windows.FirstOrDefault(x => x.Id == ActiveWindowId);

    /// <summary>
    /// The window most recently in use that doesn't belong to <paramref name="exceptPid"/>. KWin scripts can't read the focus
    /// chain, but activating a window raises it, so the stack gives the same order. A kept-above window sits on top whether it
    /// was used or not, so one only counts when there is nothing else. Minimized windows aren't in <see cref="Windows"/>.
    /// </summary>
    public KWinWindow? LastUsedWindow(int exceptPid)
    {
        List<KWinWindow> candidates = Windows.Where(x => x.Pid != exceptPid && x.IsNormal && !string.IsNullOrEmpty(x.Caption)).ToList();

        return candidates.FirstOrDefault(x => !x.IsKeepAbove) ?? candidates.FirstOrDefault();
    }

    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static KWinDesktop Query()
    {
        return Parse(KWinScript.Run(QueryScript), DesktopCoordinates.ParseXwaylandClientsScale(ReadKdeGlobals()));
    }

    /// <summary>Makes a window active. It must be visible on the current virtual desktop.</summary>
    /// <returns>False when the window no longer exists.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static bool ActivateWindow(string windowId)
    {
        string script = "var id = " + KWinScript.JsString(windowId) + ";\n" + """
            var found = false;
            var stack = workspace.stackingOrder;
            for (var i = 0; i < stack.length; i++) {
                if (String(stack[i].internalId) === id) { workspace.activeWindow = stack[i]; found = true; break; }
            }
            report(found ? "1" : "0");
            """;

        return KWinScript.Run(script) == "1";
    }

    internal static KWinDesktop Parse(string json, bool xwaylandClientsScale)
    {
        JObject root = JsonConvert.DeserializeObject<JObject>(json) ??
            throw new KWinScriptException("KWin's helper script reported nothing.");

        List<KWinOutput> outputs = root["outputs"]!.Select(x => new KWinOutput(
            (string?)x["name"] ?? "", ReadRectangle(x["geometry"]!), (double)x["scale"]!)).ToList();

        // KWin lists the bottom of the stack first.
        List<KWinWindow> windows = root["windows"]!.Reverse().Select(x => new KWinWindow(
            (string?)x["id"] ?? "", (string?)x["caption"] ?? "", (string?)x["resourceClass"] ?? "", (int)x["pid"]!,
            ReadRectangle(x["frame"]!), ReadRectangle(x["client"]!), (bool)x["normal"]!, (bool)x["fullscreen"]!, (bool)x["keepAbove"]!)).ToList();

        return new KWinDesktop
        {
            Outputs = outputs,
            Windows = windows,
            Cursor = new Point((int)Math.Round((double)root["cursor"]!["x"]!), (int)Math.Round((double)root["cursor"]!["y"]!)),
            ActiveOutputName = (string?)root["activeOutput"],
            ActiveWindowId = (string?)root["activeWindow"],
            Coordinates = DesktopCoordinates.ForDesktop(outputs, xwaylandClientsScale)
        };
    }

    private static Rectangle ReadRectangle(JToken token)
    {
        return new Rectangle(
            (int)Math.Round((double)token["x"]!),
            (int)Math.Round((double)token["y"]!),
            (int)Math.Round((double)token["width"]!),
            (int)Math.Round((double)token["height"]!));
    }

    private static string? ReadKdeGlobals()
    {
        string? configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        try
        {
            return File.ReadAllText(Path.Combine(configHome, "kdeglobals"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
