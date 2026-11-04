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

using ShareX.HelpersLib;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ShareX;

/// <summary>
/// Coordinates the hotkey host and desktop notification-area icon.
/// Avalonia owns the application lifetime and all visible windows.
/// </summary>
internal sealed class MainForm
{
    private readonly IHotkeyHost _hotkeyHost;
    internal bool IsDisposed => _hotkeyHost.IsDisposed;
    internal ITrayIconService TrayIconService { get; }

    public MainForm()
    {
        // A -multi instance gets a component of its own, so it can't remove or fire the first instance's hotkeys;
        // its keys are then the first instance's and show as failed.
        _hotkeyHost = StartupOptions.MultiInstance
            ? new KGlobalAccelHotkeyHost("sharex-" + Environment.ProcessId, true)
            : new KGlobalAccelHotkeyHost("sharex", false);
        _hotkeyHost.Closed += OnHostClosed;

        ApplyIconSetting();
        TrayIconService = new DesktopTrayIconService(ShareXResources.IconBytes, ApplicationInfo.TitleShort, ApplicationState.Settings.ShowTray);
    }

    internal void Initialize() => _hotkeyHost.Initialize();

    internal void ApplyHotkeySettings()
    {
        _hotkeyHost.HotkeyRepeatLimit = ApplicationState.Settings.HotkeyRepeatLimit;
    }

    internal void UpdateTrayIcon()
    {
        ApplyIconSetting();
        TrayIconService.SetIcon(ShareXResources.IconBytes);
    }

    private static void ApplyIconSetting()
    {
        ShareXResources.UseWhiteIcon = ApplicationState.Settings.UseWhiteShareXIcon;
        using MemoryStream stream = new(ShareXResources.IconBytes, writable: false);
        Avalonia.Application.Current!.Resources["ShareX.WindowIcon"] = new Avalonia.Controls.WindowIcon(stream);
    }

    internal async Task UpdateHotkeysAsync()
    {
        await Task.Run(SettingManager.WaitHotkeysConfig);

        HotkeyManager? hotkeyManager = ApplicationState.HotkeyManager;

        if (hotkeyManager == null)
        {
            hotkeyManager = new HotkeyManager(_hotkeyHost);
            hotkeyManager.HotkeyTrigger += HandleHotkeys;
            ApplicationState.HotkeyManager = hotkeyManager;
        }

        hotkeyManager.UpdateHotkeys(ApplicationState.HotkeysConfig.Hotkeys, !StartupOptions.IgnoreHotkeyWarning);
        DebugHelper.WriteLine("HotkeyManager started.");
    }

    private async void HandleHotkeys(HotkeySettings hotkeySetting)
    {
        DebugHelper.WriteLine("Hotkey triggered. " + hotkeySetting);
        await TaskHelpers.ExecuteJob(hotkeySetting.TaskSettings);
    }

    internal void ExitApplication() => _hotkeyHost.Close();

    internal void ReleaseHotkeysForExit() => _hotkeyHost.ReleaseForExit();

    private void OnHostClosed(object? sender, EventArgs e)
    {
        TrayIconService.Dispose();
        ApplicationLifecycle.OnHotkeyHostClosed();
    }
}
