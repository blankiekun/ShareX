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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

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

/// <summary>What KWin exposes about one window. Text is empty when KWin has no value.</summary>
public sealed record KWinWindowDetails(string Id, string Caption, string ResourceClass, string ResourceName, string WindowRole,
    string DesktopFileName, int Pid, Rectangle FrameGeometry, Rectangle ClientGeometry, string OutputName,
    string WindowType, double Opacity, bool IsKeepAbove, IReadOnlyList<string> State);

public enum KWinBorderlessResult
{
    NotFound,

    /// <summary>KWin can't do it: the window draws its own title bar, or can't go full screen.</summary>
    NotSupported,

    MadeBorderless,
    Restored
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

    private const string FindWindowScript = """
        function findWindow(id) {
            var stack = workspace.stackingOrder;
            for (var i = 0; i < stack.length; i++) {
                if (String(stack[i].internalId) === id) return stack[i];
            }
            return null;
        }
        function rect(g) { return { x: g.x, y: g.y, width: g.width, height: g.height }; }

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

    /// <summary>
    /// The window a hotkey means: the active one, or the last used when <paramref name="exceptPid"/>'s own window has the focus.
    /// An active kept-above window counts, or the keep-above toggle could never turn itself off.
    /// </summary>
    public KWinWindow? ActiveOrLastUsedWindow(int exceptPid)
    {
        return ActiveWindow is KWinWindow active && active.Pid != exceptPid ? active : LastUsedWindow(exceptPid);
    }

    /// <summary>The window a person typing this title means: an exact match, otherwise the first that contains it, nearest the top of the stack.</summary>
    public KWinWindow? FindWindowByTitle(string title, int exceptPid)
    {
        List<KWinWindow> candidates = Windows.Where(x => x.Pid != exceptPid && x.IsNormal).ToList();

        return candidates.FirstOrDefault(x => x.Caption == title) ??
            candidates.FirstOrDefault(x => x.Caption.Contains(title, StringComparison.CurrentCultureIgnoreCase));
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

    /// <summary>Lets the user click a window with KWin's own picker, which also takes Escape or a right click to cancel.</summary>
    /// <returns>The window's id. Null when the user canceled or clicked something that isn't an ordinary window.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable.</exception>
    public static async Task<string?> PickWindowAsync()
    {
        string? sessionBus = DBusAddress.Session;
        if (string.IsNullOrEmpty(sessionBus))
        {
            throw new KWinScriptException("There is no D-Bus session bus, so ShareX cannot talk to KWin. Is this a KDE Plasma Wayland session?");
        }

        using DBusConnection connection = new(sessionBus);

        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            // KWin answers when the user clicks, so this has no timeout.
            return await CallQueryWindowInfoAsync(connection).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException e) when (e.ErrorName is "org.kde.KWin.Error.UserCancel" or "org.kde.KWin.Error.InvalidWindow")
        {
            return null;
        }
        catch (DBusExceptionBase e)
        {
            throw new KWinScriptException("KWin's window picker is not available: " + e.Message, e);
        }
    }

    private static Task<string?> CallQueryWindowInfoAsync(DBusConnection connection)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.kde.KWin", "/KWin", "org.kde.KWin", "queryWindowInfo");
        return connection.CallMethodAsync(writer.CreateMessage(), static (Message message, object? _) =>
        {
            Dictionary<string, VariantValue> info = message.GetBodyReader().ReadDictionaryOfStringToVariantValue();
            return info.TryGetValue("uuid", out VariantValue uuid) ? uuid.GetString() : null;
        }, null);
    }

    /// <returns>Null when the window no longer exists.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static KWinWindowDetails? GetWindowDetails(string windowId)
    {
        string script = FindWindowScript + "var w = findWindow(" + KWinScript.JsString(windowId) + ");\n" + """
            var types = ["normalWindow", "dialog", "dock", "desktopWindow", "toolbar", "menu", "utility", "splash", "tooltip",
                "notification", "criticalNotification", "onScreenDisplay", "appletPopup", "comboBox", "dndIcon", "popupMenu", "dropdownMenu"];
            var states = ["keepAbove", "keepBelow", "fullScreen", "shade", "noBorder", "minimized", "active", "demandsAttention", "modal",
                "transient", "onAllDesktops", "skipTaskbar", "skipPager", "skipSwitcher", "moveable", "resizeable", "closeable",
                "minimizable", "maximizable", "fullScreenable"];
            function isSet(name) { return w[name] === true; }
            report(JSON.stringify(w && {
                id: String(w.internalId), caption: w.caption, resourceClass: w.resourceClass, resourceName: w.resourceName,
                windowRole: w.windowRole, desktopFileName: w.desktopFileName, pid: w.pid,
                frame: rect(w.frameGeometry), client: rect(w.clientGeometry), output: w.output ? w.output.name : "",
                type: types.filter(isSet)[0] || "", opacity: w.opacity, keepAbove: w.keepAbove, state: states.filter(isSet)
            }));
            """;

        return ParseWindowDetails(KWinScript.Run(script));
    }

    /// <returns>False when the window no longer exists.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static bool SetKeepAbove(string windowId, bool keepAbove)
    {
        return SetWindowProperty(windowId, "keepAbove", keepAbove ? "true" : "false");
    }

    /// <param name="windowId">The window.</param>
    /// <param name="opacity">From 0 to 1.</param>
    /// <returns>False when the window no longer exists.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static bool SetOpacity(string windowId, double opacity)
    {
        return SetWindowProperty(windowId, "opacity", opacity.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Moves one of ShareX's own windows to a place it can't ask for itself: KWin keeps a window that moves itself inside the
    /// work area, so it can't overlap a panel's strip or hang off an edge. A script's move isn't limited that way.
    /// </summary>
    /// <returns>False when the window no longer exists.</returns>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public bool MoveWindow(string windowId, Point x11Position)
    {
        string script = FindWindowScript + "var w = findWindow(" + KWinScript.JsString(windowId) + ");\n" +
            FormattableString.Invariant($"var x = {x11Position.X / Coordinates.X11Scale}, y = {x11Position.Y / Coordinates.X11Scale};\n") + """
            if (w) w.frameGeometry = { x: x, y: y, width: w.frameGeometry.width, height: w.frameGeometry.height };
            report(w ? "1" : "0");
            """;

        return KWinScript.Run(script) == "1";
    }

    private static bool SetWindowProperty(string windowId, string property, string value)
    {
        string script = FindWindowScript + "var w = findWindow(" + KWinScript.JsString(windowId) + ");\n" +
            "if (w) w." + property + " = " + value + ";\nreport(w ? \"1\" : \"0\");\n";

        return KWinScript.Run(script) == "1";
    }

    /// <summary>
    /// Takes the frame off a window and fills the output, or puts the frame back on one that has none. KWin holds the state, so it
    /// outlasts this script and ShareX, and ends when the window closes.
    /// </summary>
    /// <remarks>
    /// Filling all of the output uses KWin's full screen rather than noBorder and frameGeometry, because KWin pushes a window out
    /// from under a panel's strip again as soon as it loses focus. The program can see that it's full screen, and some change their
    /// layout for it.
    /// </remarks>
    /// <param name="windowId">The window.</param>
    /// <param name="useWorkingArea">Fill the output less its panels, rather than all of it.</param>
    /// <param name="restoreGeometry">Where to put a window that gets its frame back. Null leaves it where KWin puts it.</param>
    /// <param name="previousGeometry">Where a window that was just made borderless had been. Empty when KWin remembers it.</param>
    /// <exception cref="KWinScriptException">KWin is unavailable, or did not answer in time.</exception>
    public static KWinBorderlessResult ToggleBorderless(string windowId, bool useWorkingArea, Rectangle? restoreGeometry, out Rectangle previousGeometry)
    {
        string restore = restoreGeometry is Rectangle r ? JsonConvert.SerializeObject(new { x = r.X, y = r.Y, width = r.Width, height = r.Height }) : "null";

        string script = FindWindowScript + "var w = findWindow(" + KWinScript.JsString(windowId) + ");\n" +
            "var restore = " + restore + ";\n" +
            "var useWorkingArea = " + (useWorkingArea ? "true" : "false") + ";\n" + """
            function result() {
                if (!w) return { result: "notFound" };
                if (w.fullScreen || w.noBorder) {
                    w.fullScreen = false;
                    w.noBorder = false;
                    if (restore) w.frameGeometry = restore;
                    return { result: "restored" };
                }
                if (!useWorkingArea) {
                    if (!w.fullScreenable) return { result: "notSupported" };
                    w.fullScreen = true;
                    return { result: "borderless" };
                }
                var frame = w.frameGeometry, client = w.clientGeometry;
                // A frame the same size as the client area means KWin isn't drawing a title bar.
                if (frame.width === client.width && frame.height === client.height) return { result: "notSupported" };
                var previous = rect(frame);
                w.noBorder = true;
                w.frameGeometry = workspace.clientArea(KWin.MaximizeArea, w);
                return { result: "borderless", previous: previous };
            }
            report(JSON.stringify(result()));
            """;

        JObject root = JsonConvert.DeserializeObject<JObject>(KWinScript.Run(script)) ??
            throw new KWinScriptException("KWin's helper script reported nothing.");

        previousGeometry = root["previous"] is JToken previous ? ReadRectangle(previous) : Rectangle.Empty;

        return (string?)root["result"] switch
        {
            "restored" => KWinBorderlessResult.Restored,
            "borderless" => KWinBorderlessResult.MadeBorderless,
            "notSupported" => KWinBorderlessResult.NotSupported,
            _ => KWinBorderlessResult.NotFound
        };
    }

    public static KWinWindowDetails? ParseWindowDetails(string json)
    {
        JObject? root = JsonConvert.DeserializeObject<JObject>(json);

        if (root == null)
        {
            return null;
        }

        return new KWinWindowDetails(
            (string?)root["id"] ?? "", (string?)root["caption"] ?? "", (string?)root["resourceClass"] ?? "", (string?)root["resourceName"] ?? "",
            (string?)root["windowRole"] ?? "", (string?)root["desktopFileName"] ?? "", (int?)root["pid"] ?? 0,
            ReadRectangle(root["frame"]!), ReadRectangle(root["client"]!), (string?)root["output"] ?? "", (string?)root["type"] ?? "",
            (double?)root["opacity"] ?? 1, (bool?)root["keepAbove"] ?? false,
            root["state"]?.Select(x => (string?)x ?? "").ToList() ?? []);
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
