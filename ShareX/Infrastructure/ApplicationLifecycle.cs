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
using ShareX.AvaloniaUI;
using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

internal static class ApplicationLifecycle
{
    // shortcut: past this the file may still be saving. gsr gets SIGINT when ShareX exits and the next start recovers it. 60 s stays
    // under systemd's usual 90 s before SIGKILL at logout.
    private static readonly TimeSpan SignalRecordingLimit = TimeSpan.FromSeconds(60);
    private static readonly List<PosixSignalRegistration> _signalRegistrations = [];
    private static MainForm? _hotkeyHost;
    private static int _signalExitStarted;
    private static int _exitStarted;
    private static int _hostClosed;
    private static int _closeSequenceStarted;
    private static bool _restartRequested;

    internal static bool IsClosing => Volatile.Read(ref _exitStarted) != 0 || Volatile.Read(ref _closeSequenceStarted) != 0;

    internal static void RegisterSignalHandlers()
    {
        foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            _signalRegistrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
        }
    }

    private static void OnSignal(PosixSignalContext context)
    {
        // With no recording running, the signal keeps its default action.
        if (!ScreenRecordManager.IsRecording && Volatile.Read(ref _signalExitStarted) == 0)
        {
            // But an active global shortcut would go on taking its key from everything else, firing at nothing.
            _hotkeyHost?.ReleaseHotkeysForExit();
            HostSoundPlayer.StopForExit();
            return;
        }

        context.Cancel = true;
        if (Interlocked.Exchange(ref _signalExitStarted, 1) != 0)
        {
            return;
        }

        DebugHelper.WriteLine($"{context.Signal} received while recording: finishing the recording before exit.");
        _ = Task.Run(async () =>
        {
            await ScreenRecordManager.FinishForExitAsync(SignalRecordingLimit);
            Exit();
            // A stuck UI thread must not keep the process alive after the system asked it to go.
            await Task.Delay(TimeSpan.FromSeconds(5));
            Environment.Exit(1);
        });
    }

    internal static void AttachHost(MainForm hotkeyHost)
    {
        ArgumentNullException.ThrowIfNull(hotkeyHost);
        _hotkeyHost = hotkeyHost;
    }

    internal static void OnAvaloniaStopped()
    {
        MainForm? hotkeyHost = _hotkeyHost;
        if (Volatile.Read(ref _hostClosed) == 0 && hotkeyHost is { IsDisposed: false })
        {
            hotkeyHost.ExitApplication();
        }
    }

    internal static void OnHotkeyHostClosed()
    {
        if (Interlocked.Exchange(ref _hostClosed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _exitStarted, 1);
        ShareX.Tools.MouseHighlighterManager.Shutdown();
        MainWindowIntegration.Close();
        TaskManager.StopAllTasks();
        AvaloniaBootstrapper.Shutdown();
    }

    internal static void CloseSequence()
    {
        if (Interlocked.Exchange(ref _closeSequenceStarted, 1) != 0)
        {
            return;
        }

        DebugHelper.WriteLine("ShareX closing.");
        ApplicationState.WatchFolderManager?.Dispose();
        ApplicationState.WatchFolderManager = null;
        SettingManager.HistoryClose();
        SettingManager.SaveAllSettings();
        DebugHelper.WriteLine("ShareX closed.");
    }

    internal static void Restart()
    {
        _restartRequested = true;
        Exit();
    }

    internal static void RestartIfRequested()
    {
        if (!_restartRequested)
        {
            return;
        }

        DebugHelper.WriteLine("ShareX restarting.");
        string executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException("Unable to determine the ShareX executable path.");

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    internal static void Exit()
    {
        void ExitCore()
        {
            if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
            {
                return;
            }

            if (_hotkeyHost is { IsDisposed: false } hotkeyHost)
            {
                hotkeyHost.ExitApplication();
            }
            else
            {
                AvaloniaBootstrapper.Shutdown();
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ExitCore();
        }
        else
        {
            Dispatcher.UIThread.Post(ExitCore);
        }
    }

    internal static void ForceClose()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ForceClose);
            return;
        }

        if (ScreenRecordManager.IsRecording)
        {
            if (MessageBox.Show(Strings.ShareXCannotBeClosedWhileScreenRecordingIsActive, ApplicationInfo.Name,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                ScreenRecordManager.FinishRecording();
            }

            return;
        }

        Exit();
    }
}
