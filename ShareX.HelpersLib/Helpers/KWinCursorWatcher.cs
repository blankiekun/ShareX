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
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib;

/// <summary>
/// Follows the pointer through one long-lived KWin script, in KWin logical coordinates. The script only wakes when the pointer
/// moves, so an idle pointer costs nothing. <see cref="KWinScript.Run"/> loads a script per call and must not be polled.
/// Nothing can run after a kill, so the script watches for that itself: a report that gets no answer for
/// <see cref="DeadAfterMilliseconds"/> means ShareX is gone, and the script unloads itself on the next move.
/// </summary>
public sealed class KWinCursorWatcher : IDisposable
{
    private const string PluginName = "sharex-cursor-watcher";
    private const string CallbackPath = "/org/sharex/KWinCursorWatcher";
    private const string CallbackInterface = "org.sharex.KWinCursorWatcher";
    private const string CallbackMethod = "Moved";
    private const int MinimumReportMilliseconds = 16;
    private const int DeadAfterMilliseconds = 5000;

    private readonly DBusConnection _connection;
    private int _disposed;

    private KWinCursorWatcher(DBusConnection connection)
    {
        _connection = connection;
    }

    /// <summary>Returns once the script is running and has reported where the pointer is. Blocks like <see cref="KWinScript.Run"/>.</summary>
    /// <param name="moved">Called on a D-Bus thread, and must not block. Reports wait for each other, so a slow one holds back the next.</param>
    /// <exception cref="KWinScriptException">KWin is unavailable, or no report arrived in time.</exception>
    public static KWinCursorWatcher Start(Action<Point> moved)
    {
        return Task.Run(() => StartAsync(moved, KWinScript.DefaultTimeout)).GetAwaiter().GetResult();
    }

    private static async Task<KWinCursorWatcher> StartAsync(Action<Point> moved, TimeSpan timeout)
    {
        string runtimeDirectory = KWinScript.RequireRuntimeDirectory();
        string sessionBus = KWinScript.RequireSessionBus();
        string scriptPath = Path.Combine(runtimeDirectory, PluginName + ".js");
        bool loadRequested = false;
        bool started = false;

        if (!await KWinScript.Gate.WaitAsync(timeout).ConfigureAwait(false))
        {
            throw new KWinScriptException($"KWin did not answer ShareX's helper script within {timeout.TotalSeconds:0.#} s.");
        }

        DBusConnection connection = new(sessionBus);
        CallbackHandler callback = new(moved);

        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
            connection.AddMethodHandler(callback);

            // One left by a ShareX that was killed before the pointer moved again. KWin refuses a second script of the same name.
            await KWinScript.CallUnloadScriptAsync(connection, PluginName).WaitAsync(timeout).ConfigureAwait(false);

            await File.WriteAllTextAsync(scriptPath, BuildScript(connection.UniqueName!)).ConfigureAwait(false);

            loadRequested = true;
            int scriptId = await KWinScript.CallLoadScriptAsync(connection, scriptPath, PluginName).WaitAsync(timeout).ConfigureAwait(false);
            if (scriptId < 0)
            {
                throw new KWinScriptException($"KWin refused to load its helper script (id {scriptId}).");
            }

            await KWinScript.CallRunAsync(connection, scriptId).WaitAsync(timeout).ConfigureAwait(false);

            // KWin reads the file on another thread after run() returns, so it has to stay until the script has reported.
            await callback.FirstReport.WaitAsync(timeout).ConfigureAwait(false);

            started = true;
            return new KWinCursorWatcher(connection);
        }
        catch (TimeoutException e)
        {
            throw new KWinScriptException($"KWin did not answer ShareX's helper script within {timeout.TotalSeconds:0.#} s.", e);
        }
        catch (DBusExceptionBase e)
        {
            throw new KWinScriptException("KWin's scripting service is not available: " + e.Message, e);
        }
        catch (Exception e) when (e is not KWinScriptException)
        {
            throw new KWinScriptException("Could not run ShareX's helper script in KWin: " + e.Message, e);
        }
        finally
        {
            if (!started)
            {
                if (loadRequested) await UnloadAsync(connection).ConfigureAwait(false);
                connection.Dispose();
            }

            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "KWin helper script file could not be deleted: " + scriptPath);
            }

            KWinScript.Gate.Release();
        }
    }

    private static string BuildScript(string uniqueName)
    {
        return $$"""
            var waitingSince = 0, movedWhileWaiting = false;
            var timer = new QTimer();
            timer.interval = {{MinimumReportMilliseconds}};
            timer.singleShot = true;
            function report() {
                if (waitingSince) {
                    movedWhileWaiting = true;
                    if (Date.now() - waitingSince > {{DeadAfterMilliseconds}}) {
                        callDBus("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "unloadScript", {{KWinScript.JsString(PluginName)}});
                    }
                    return;
                }
                movedWhileWaiting = false;
                waitingSince = Date.now();
                var p = workspace.cursorPos;
                callDBus({{KWinScript.JsString(uniqueName)}}, {{KWinScript.JsString(CallbackPath)}}, {{KWinScript.JsString(CallbackInterface)}}, {{KWinScript.JsString(CallbackMethod)}}, p.x + "," + p.y, function () {
                    waitingSince = 0;
                    if (movedWhileWaiting && !timer.active) timer.start();
                });
            }
            timer.timeout.connect(report);
            workspace.cursorPosChanged.connect(function () { if (!timer.active) timer.start(); });
            report();
            """;
    }

    /// <summary>Reads a report of "x,y". KWin's pointer position is fractional, and JavaScript always writes it with a dot.</summary>
    public static bool TryParsePosition(string text, out Point position)
    {
        position = default;
        string[] parts = text.Split(',');

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            position = new Point((int)Math.Round(x), (int)Math.Round(y));
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task.Run(async () =>
        {
            bool entered = await KWinScript.Gate.WaitAsync(KWinScript.DefaultTimeout).ConfigureAwait(false);

            try
            {
                await UnloadAsync(_connection).ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                {
                    KWinScript.Gate.Release();
                }

                _connection.Dispose();
            }
        }).GetAwaiter().GetResult();
    }

    private static async Task UnloadAsync(DBusConnection connection)
    {
        try
        {
            await KWinScript.CallUnloadScriptAsync(connection, PluginName).WaitAsync(KWinScript.UnloadTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "KWin helper script could not be unloaded: " + PluginName);
        }
    }

    private sealed class CallbackHandler(Action<Point> moved) : IPathMethodHandler
    {
        private readonly TaskCompletionSource _firstReport = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstReport => _firstReport.Task;

        public string Path => CallbackPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            Message request = context.Request;
            if (request.InterfaceAsString != CallbackInterface || request.MemberAsString != CallbackMethod ||
                request.SignatureAsString != "s")
            {
                context.ReplyUnknownMethodError();
                return default;
            }

            if (TryParsePosition(request.GetBodyReader().ReadString(), out Point position))
            {
                moved(position);
                _firstReport.TrySetResult();
            }

            if (!context.NoReplyExpected)
            {
                using MessageWriter writer = context.CreateReplyWriter(null);
                context.Reply(writer.CreateMessage());
            }

            return default;
        }
    }
}
