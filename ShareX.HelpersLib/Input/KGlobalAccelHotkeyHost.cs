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

using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib;

/// <summary>A key that some action holds according to KGlobalAccel.</summary>
public readonly record struct KeyHolder(string Component, string Action);

/// <summary>
/// Global hotkeys through KGlobalAccel, which KWin hosts on the session bus. ShareX's settings are the source of truth:
/// each run removes what the last run left, then registers what the settings say.
/// Action names only live for one run, so nothing has to be saved with the hotkey.
/// </summary>
public sealed class KGlobalAccelHotkeyHost : IHotkeyHost
{
    private const string Service = "org.kde.kglobalaccel";
    private const string AccelPath = "/kglobalaccel";
    private const string AccelInterface = "org.kde.KGlobalAccel";
    private const string ComponentInterface = "org.kde.kglobalaccel.Component";
    private const string ComponentFriendlyName = "ShareX";
    private const uint SetPresent = 2;
    private const uint NoAutoloading = 4; // Without this KGlobalAccel keeps the key it has and ignores the one sent.
    private const int MatchEqual = 0;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly string _component;
    private readonly bool _forgetOnExit;
    private readonly Dictionary<string, HotkeyInfo> _actions = [];
    private readonly HotkeyRepeatLimiter _repeatLimiter = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task<DBusConnection>? _connecting;
    private ushort _lastId;
    private bool _keyCaptureActive;

    public event HotkeyEventHandler? HotkeyPress;
    public event EventHandler? Closed;

    public bool IsDisposed { get; private set; }
    public bool IsServiceAvailable { get; private set; }
    public int HotkeyRepeatLimit { get; set; } = 500;

    /// <param name="component">KGlobalAccel's name for this program.</param>
    /// <param name="forgetOnExit">Remove the actions on exit instead of keeping them inactive.</param>
    public KGlobalAccelHotkeyHost(string component, bool forgetOnExit)
    {
        _component = component;
        _forgetOnExit = forgetOnExit;
    }

    /// <summary>
    /// Removes the last run's actions, so a hotkey deleted since then can't keep a key.
    /// Also lifts blockGlobalShortcuts: a run killed during key capture leaves every desktop shortcut off until someone does.
    /// </summary>
    public void Initialize()
    {
        IsServiceAvailable = Try("KGlobalAccel start-up cleanup failed", async connection =>
        {
            await BlockGlobalShortcutsAsync(connection, false).ConfigureAwait(false);
            string[] leftovers = await GetActionNamesAsync(connection).ConfigureAwait(false);
            await Task.WhenAll(leftovers.Select(action => UnregisterAsync(connection, action))).ConfigureAwait(false);
        });
    }

    public void RegisterHotkey(HotkeyInfo hotkeyInfo, string description)
    {
        // A settings window closing during exit still finishes its key capture, so this isn't an error.
        if (IsDisposed || hotkeyInfo == null || hotkeyInfo.Status == HotkeyStatus.Registered) return;

        if (!hotkeyInfo.IsValidHotkey)
        {
            hotkeyInfo.Status = HotkeyStatus.NotConfigured;
            return;
        }

        // A hotkey that went Failed because its key was changed elsewhere still has its action.
        if (hotkeyInfo.ID != 0) UnregisterHotkey(hotkeyInfo);

        hotkeyInfo.Status = HotkeyStatus.Failed;

        int key = QtKeys.FromHotkey(hotkeyInfo.Hotkey, hotkeyInfo.Win);
        if (key == 0)
        {
            DebugHelper.WriteLine($"Hotkey has no Qt key: {hotkeyInfo}");
            return;
        }

        ushort id = ++_lastId;
        string action = ActionName(id);
        string? problem = null;
        bool called = Try($"Hotkey registration failed: {hotkeyInfo}", async connection =>
        {
            problem = await AssignKeyAsync(connection, action, description, key).ConfigureAwait(false);
        });

        if (!called) return;

        if (problem != null)
        {
            DebugHelper.WriteLine($"Hotkey {hotkeyInfo} not registered: {problem}");
            return;
        }

        hotkeyInfo.ID = id;
        hotkeyInfo.Status = HotkeyStatus.Registered;
        _actions[action] = hotkeyInfo;
    }

    public bool UnregisterHotkey(HotkeyInfo hotkeyInfo)
    {
        if (IsDisposed || hotkeyInfo == null) return false;

        string action = ActionName(hotkeyInfo.ID);
        if (hotkeyInfo.ID != 0 && !Try($"Hotkey unregistration failed: {hotkeyInfo}", connection => UnregisterAsync(connection, action)))
        {
            hotkeyInfo.Status = HotkeyStatus.Failed;
            return false;
        }

        _actions.Remove(action);
        hotkeyInfo.ID = 0;
        hotkeyInfo.Status = HotkeyStatus.NotConfigured;
        return true;
    }

    public void SetKeyCaptureActive(bool active)
    {
        if (IsDisposed || active == _keyCaptureActive) return;

        if (Try("blockGlobalShortcuts failed", connection => BlockGlobalShortcutsAsync(connection, active)))
        {
            _keyCaptureActive = active;
        }
    }

    public void Close()
    {
        if (IsDisposed) return;

        // First, so HotkeyManager.OnHostClosed doesn't unregister what is being kept.
        IsDisposed = true;

        ReleaseForExit();

        if (_connecting is { IsCompletedSuccessfully: true }) _connecting.Result.Dispose();

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Keeps the component and its keys for the next run, inactive: an inactive action holds its key against other
    /// programs but doesn't fire. A killed process can't do this, so its keys go nowhere until the next start.
    /// </summary>
    public void ReleaseForExit()
    {
        string[] actions = _actions.Keys.ToArray();
        Try("KGlobalAccel exit cleanup failed", async connection =>
        {
            List<Task> calls = actions.Select(action => _forgetOnExit ? UnregisterAsync(connection, action) : SetInactiveAsync(connection, action)).ToList();
            if (_keyCaptureActive) calls.Add(BlockGlobalShortcutsAsync(connection, false));
            await Task.WhenAll(calls).ConfigureAwait(false);
        });

        foreach (HotkeyInfo hotkeyInfo in _actions.Values)
        {
            hotkeyInfo.ID = 0;
            hotkeyInfo.Status = HotkeyStatus.NotConfigured;
        }
        _actions.Clear();
        _keyCaptureActive = false;
    }

    public void Dispose() => Close();

    private static string ActionName(ushort id) => "hotkey-" + id;

    private string[] ActionId(string action, string description = "") => [_component, action, ComponentFriendlyName, description];

    /// <summary>Returns why the key wasn't assigned, or null when this action alone holds it.</summary>
    private async Task<string?> AssignKeyAsync(DBusConnection connection, string action, string description, int key)
    {
        // setShortcutKeys doesn't refuse a clash: KGlobalAccel 6.8 returns and stores the requested key even when another
        // action holds it, and that action goes on firing. So the only way to not take a key is to ask first.
        KeyHolder[] holders = await GetHoldersAsync(connection, key).ConfigureAwait(false);
        if (holders.Length > 0) return "key held by " + DescribeHolders(holders);

        await DoRegisterAsync(connection, action, description).ConfigureAwait(false);
        int[] assigned = await SetShortcutKeysAsync(connection, action, key).ConfigureAwait(false);
        holders = await GetHoldersAsync(connection, key).ConfigureAwait(false);

        if (HoldsKeyAlone(assigned, key, holders, _component, action)) return null;

        await UnregisterAsync(connection, action).ConfigureAwait(false);
        return $"read back keys [{string.Join(", ", assigned)}], held by {DescribeHolders(holders)}";
    }

    /// <summary>True only if the read-back has the key and no other action holds it too.</summary>
    public static bool HoldsKeyAlone(IEnumerable<int> assigned, int key, IReadOnlyCollection<KeyHolder> holders, string component, string action)
    {
        return assigned.Contains(key) && holders.Count == 1 && holders.Contains(new KeyHolder(component, action));
    }

    private static string DescribeHolders(IEnumerable<KeyHolder> holders) => string.Join(", ", holders.Select(x => x.Component + "/" + x.Action));

    private void OnPressed(string action)
    {
        if (IsDisposed || !_actions.TryGetValue(action, out HotkeyInfo? hotkeyInfo)) return;
        if (!_repeatLimiter.Allow(_clock.ElapsedMilliseconds, HotkeyRepeatLimit)) return;

        HotkeyPress?.Invoke(hotkeyInfo.ID, hotkeyInfo.KeyCode, hotkeyInfo.ModifiersEnum);
    }

    // System Settings can move an action's key. The next registration puts ShareX's key back; until then, say what's true.
    private void OnShortcutsChanged(string action, int[] keys)
    {
        if (IsDisposed || !_actions.TryGetValue(action, out HotkeyInfo? hotkeyInfo)) return;

        if (!keys.Contains(QtKeys.FromHotkey(hotkeyInfo.Hotkey, hotkeyInfo.Win)))
        {
            DebugHelper.WriteLine($"Hotkey {hotkeyInfo} was changed outside ShareX.");
            hotkeyInfo.Status = HotkeyStatus.Failed;
        }
    }

    /// <summary>Runs on a pool thread and blocks at most the timeout, so it's safe from the UI thread. False when it failed.</summary>
    private bool Try(string failure, Func<DBusConnection, Task> call)
    {
        try
        {
            Task.Run(async () => await call(await GetConnectionAsync().ConfigureAwait(false)).ConfigureAwait(false))
                .WaitAsync(CallTimeout).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, failure);
            return false;
        }
    }

    private Task<DBusConnection> GetConnectionAsync()
    {
        if (_connecting == null || _connecting.IsFaulted || _connecting.IsCanceled)
        {
            _connecting = ConnectAsync();
        }

        return _connecting;
    }

    // shortcut: nothing registers again if the connection or KGlobalAccel goes away. KWin hosts it and ShareX is an XWayland
    // client, so a KWin restart takes ShareX down too.
    private async Task<DBusConnection> ConnectAsync()
    {
        string sessionBus = DBusAddress.Session ?? throw new InvalidOperationException("There is no D-Bus session bus.");
        DBusConnection connection = new(sessionBus);

        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);

            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Interface = ComponentInterface, Member = "globalShortcutPressed", Arg0 = _component },
                static (Message message, object? _) =>
                {
                    Reader reader = message.GetBodyReader();
                    reader.ReadString();
                    return reader.ReadString();
                },
                (Notification<string> notification) =>
                {
                    if (notification.HasValue)
                    {
                        string action = notification.Value;
                        Dispatcher.UIThread.Post(() => OnPressed(action));
                    }
                },
                false).ConfigureAwait(false);

            await connection.AddMatchAsync(
                new MatchRule { Type = MessageType.Signal, Sender = Service, Path = AccelPath, Interface = AccelInterface, Member = "yourShortcutsChanged" },
                static (Message message, object? _) =>
                {
                    Reader reader = message.GetBodyReader();
                    string[] actionId = reader.ReadArrayOfString();
                    return (Component: actionId[0], Action: actionId[1], Keys: ReadKeySequences(ref reader));
                },
                (Notification<(string Component, string Action, int[] Keys)> notification) =>
                {
                    if (notification.HasValue && notification.Value.Component == _component)
                    {
                        (_, string action, int[] keys) = notification.Value;
                        Dispatcher.UIThread.Post(() => OnShortcutsChanged(action, keys));
                    }
                },
                false).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Reads a(ai): QKeySequence is four ints, unused ones 0. Single-key sequences are all ShareX uses, so this keeps the first.</summary>
    private static int[] ReadKeySequences(ref Reader reader)
    {
        List<int> keys = [];
        ArrayEnd sequences = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(sequences))
        {
            reader.AlignStruct();
            int[] sequence = reader.ReadArrayOfInt32();
            if (sequence.Length > 0 && sequence[0] != 0) keys.Add(sequence[0]);
        }

        return keys.ToArray();
    }

    private delegate void BodyWriter(ref MessageWriter writer);

    private static MessageBuffer CreateCall(DBusConnection connection, string member, string signature, BodyWriter writeBody)
    {
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(Service, AccelPath, AccelInterface, member, signature);
            writeBody(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void WriteKeySequence(ref MessageWriter writer, int key)
    {
        writer.WriteStructureStart();
        writer.WriteArray(new[] { key, 0, 0, 0 });
    }

    private Task DoRegisterAsync(DBusConnection connection, string action, string description)
    {
        return connection.CallMethodAsync(CreateCall(connection, "doRegister", "as", (ref MessageWriter w) => w.WriteArray(ActionId(action, description))));
    }

    private Task<int[]> SetShortcutKeysAsync(DBusConnection connection, string action, int key)
    {
        return connection.CallMethodAsync(
            CreateCall(connection, "setShortcutKeys", "asa(ai)u", (ref MessageWriter w) =>
            {
                w.WriteArray(ActionId(action));
                ArrayStart sequences = w.WriteArrayStart(DBusType.Struct);
                WriteKeySequence(ref w, key);
                w.WriteArrayEnd(sequences);
                w.WriteUInt32(SetPresent | NoAutoloading);
            }),
            static (Message message, object? _) =>
            {
                Reader reader = message.GetBodyReader();
                return ReadKeySequences(ref reader);
            }, null);
    }

    private static Task<KeyHolder[]> GetHoldersAsync(DBusConnection connection, int key)
    {
        return connection.CallMethodAsync(
            CreateCall(connection, "globalShortcutsByKey", "(ai)(i)", (ref MessageWriter w) =>
            {
                WriteKeySequence(ref w, key);
                w.WriteStructureStart();
                w.WriteInt32(MatchEqual);
            }),
            static (Message message, object? _) =>
            {
                // a(ssssssaiai): action, its friendly name, component, its friendly name, context, its friendly name, keys, default keys.
                List<KeyHolder> holders = [];
                Reader reader = message.GetBodyReader();
                ArrayEnd infos = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(infos))
                {
                    reader.AlignStruct();
                    string action = reader.ReadString();
                    reader.ReadString();
                    string component = reader.ReadString();
                    for (int i = 0; i < 3; i++) reader.ReadString();
                    reader.ReadArrayOfInt32();
                    reader.ReadArrayOfInt32();
                    holders.Add(new KeyHolder(component, action));
                }

                return holders.ToArray();
            }, null);
    }

    private Task<string[]> GetActionNamesAsync(DBusConnection connection)
    {
        return connection.CallMethodAsync(
            CreateCall(connection, "allActionsForComponent", "as", (ref MessageWriter w) => w.WriteArray(ActionId(""))),
            static (Message message, object? _) =>
            {
                List<string> names = [];
                Reader reader = message.GetBodyReader();
                ArrayEnd actionIds = reader.ReadArrayStart(DBusType.Array);
                while (reader.HasNext(actionIds)) names.Add(reader.ReadArrayOfString()[1]);
                return names.ToArray();
            }, null);
    }

    private Task UnregisterAsync(DBusConnection connection, string action)
    {
        return connection.CallMethodAsync(CreateCall(connection, "unregister", "ss", (ref MessageWriter w) =>
        {
            w.WriteString(_component);
            w.WriteString(action);
        }));
    }

    private Task SetInactiveAsync(DBusConnection connection, string action)
    {
        return connection.CallMethodAsync(CreateCall(connection, "setInactive", "as", (ref MessageWriter w) => w.WriteArray(ActionId(action))));
    }

    private static Task BlockGlobalShortcutsAsync(DBusConnection connection, bool block)
    {
        return connection.CallMethodAsync(CreateCall(connection, "blockGlobalShortcuts", "b", (ref MessageWriter w) => w.WriteBool(block)));
    }
}

/// <summary>Lets one press through per limit, across all hotkeys. KGlobalAccel has no limit of its own.</summary>
public sealed class HotkeyRepeatLimiter
{
    private long? _lastAllowed;

    public bool Allow(long nowMilliseconds, int limitMilliseconds)
    {
        if (limitMilliseconds > 0 && _lastAllowed is long last && nowMilliseconds - last < limitMilliseconds) return false;

        _lastAllowed = nowMilliseconds;
        return true;
    }
}
