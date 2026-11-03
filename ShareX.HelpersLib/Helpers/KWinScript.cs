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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib;

public sealed class KWinScriptException : Exception
{
    public KWinScriptException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>
/// Runs a generated KWin script and returns the text it reports. KWin gives no error for a bad script
/// (loadScript returns an id and no callback arrives), so every call has a timeout, and the script and
/// its file are removed on every path.
/// </summary>
public static class KWinScript
{
    private const string KWinService = "org.kde.KWin";
    private const string ScriptingPath = "/Scripting";
    private const string ScriptingInterface = "org.kde.kwin.Scripting";
    private const string ScriptInterface = "org.kde.kwin.Script";
    private const string CallbackInterface = "org.sharex.KWinScriptCallback";
    private const string CallbackMethod = "Report";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(1);

    // KWin numbers a script by how many are loaded, so a script unloading while another loads can make two share
    // an id, and run() then reaches the wrong one. Scripts from other programs can still collide; that times out.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="body"/>, which must call <c>report(text)</c> once. Blocks the calling thread
    /// for at most the timeout plus the time unloading takes, so it is safe from the UI thread.
    /// </summary>
    /// <exception cref="KWinScriptException">KWin is unavailable, or no report arrived in time.</exception>
    public static string Run(string body, TimeSpan? timeout = null)
    {
        return Task.Run(() => RunOneAtATimeAsync(body, timeout ?? DefaultTimeout)).GetAwaiter().GetResult();
    }

    private static async Task<string> RunOneAtATimeAsync(string body, TimeSpan timeout)
    {
        if (!await Gate.WaitAsync(timeout).ConfigureAwait(false))
        {
            throw new KWinScriptException($"KWin did not answer ShareX's helper script within {timeout.TotalSeconds:0.#} s.");
        }

        try
        {
            return await RunAsync(body, timeout).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> RunAsync(string body, TimeSpan timeout)
    {
        string runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
        if (runtimeDirectory.Length == 0 || !Directory.Exists(runtimeDirectory))
        {
            throw new KWinScriptException("XDG_RUNTIME_DIR is not set, so ShareX cannot talk to KWin. Is this a KDE Plasma Wayland session?");
        }

        string? sessionBus = DBusAddress.Session;
        if (string.IsNullOrEmpty(sessionBus))
        {
            throw new KWinScriptException("There is no D-Bus session bus, so ShareX cannot talk to KWin. Is this a KDE Plasma Wayland session?");
        }

        string token = Guid.NewGuid().ToString("N");
        string pluginName = "sharex-" + token;
        string scriptPath = Path.Combine(runtimeDirectory, pluginName + ".js");
        string callbackPath = "/org/sharex/KWinScript/c" + token;
        bool loadRequested = false;

        using DBusConnection connection = new(sessionBus);
        CallbackHandler callback = new(callbackPath);

        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(timeout).ConfigureAwait(false);
            connection.AddMethodHandler(callback);

            string prelude = "function report(text) { callDBus(" +
                JsString(connection.UniqueName!) + ", " + JsString(callbackPath) + ", " +
                JsString(CallbackInterface) + ", " + JsString(CallbackMethod) + ", String(text)); }\n";

            await File.WriteAllTextAsync(scriptPath, prelude + body).ConfigureAwait(false);

            loadRequested = true;
            int scriptId = await CallLoadScriptAsync(connection, scriptPath, pluginName).WaitAsync(timeout).ConfigureAwait(false);
            if (scriptId < 0)
            {
                throw new KWinScriptException($"KWin refused to load its helper script (id {scriptId}).");
            }

            await CallRunAsync(connection, scriptId).WaitAsync(timeout).ConfigureAwait(false);

            return await callback.Result.WaitAsync(timeout).ConfigureAwait(false);
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
            if (loadRequested)
            {
                try
                {
                    await CallUnloadScriptAsync(connection, pluginName).WaitAsync(UnloadTimeout).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, "KWin helper script could not be unloaded: " + pluginName);
                }
            }

            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "KWin helper script file could not be deleted: " + scriptPath);
            }
        }
    }

    internal static string JsString(string value) => Newtonsoft.Json.JsonConvert.ToString(value);

    private static Task<int> CallLoadScriptAsync(DBusConnection connection, string path, string pluginName)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(KWinService, ScriptingPath, ScriptingInterface, "loadScript", "ss");
        writer.WriteString(path);
        writer.WriteString(pluginName);
        return connection.CallMethodAsync(writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadInt32(), null);
    }

    private static Task CallRunAsync(DBusConnection connection, int scriptId)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(KWinService, ScriptingPath + "/Script" + scriptId, ScriptInterface, "run");
        return connection.CallMethodAsync(writer.CreateMessage());
    }

    private static Task CallUnloadScriptAsync(DBusConnection connection, string pluginName)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(KWinService, ScriptingPath, ScriptingInterface, "unloadScript", "s");
        writer.WriteString(pluginName);
        return connection.CallMethodAsync(writer.CreateMessage());
    }

    private sealed class CallbackHandler(string path) : IPathMethodHandler
    {
        private readonly TaskCompletionSource<string> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> Result => _result.Task;

        public string Path { get; } = path;

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

            _result.TrySetResult(request.GetBodyReader().ReadString());

            if (!context.NoReplyExpected)
            {
                using MessageWriter writer = context.CreateReplyWriter(null);
                context.Reply(writer.CreateMessage());
            }

            return default;
        }
    }
}
