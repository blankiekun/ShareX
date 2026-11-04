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

using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using System;
using System.IO;
using System.Reflection;

namespace ShareX;

internal interface ITrayIconService : IDisposable
{
    bool Visible { get; set; }
    string ToolTipText { get; set; }

    void SetIcon(byte[] iconBytes);

    void RefreshMenu();
}

/// <summary>Desktop tray adapter over Avalonia's tray icon.</summary>
internal sealed class DesktopTrayIconService : ITrayIconService
{
    private readonly Avalonia.Controls.TrayIcon _avaloniaIcon;
    private readonly IDisposable _avaloniaIconRegistration;
    private readonly Avalonia.Threading.DispatcherTimer _singleClickTimer;
    // Long enough for Plasma to have created the level above, short enough that a hover rarely lands first.
    private const int PrefetchLevelDelayMs = 50;
    private int _leftClickCount;
    private bool _disposed;
    private int _prefetchGeneration;
    private bool _prefetchWaitingForExport;
    private bool _prefetchFailed;

    public bool Visible
    {
        get => _avaloniaIcon.IsVisible;
        set => _avaloniaIcon.IsVisible = value;
    }
    public string ToolTipText
    {
        get => _avaloniaIcon.ToolTipText ?? string.Empty;
        set => _avaloniaIcon.ToolTipText = value;
    }

    public DesktopTrayIconService(byte[] icon, string text, bool visible)
    {
        _singleClickTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = Avalonia.Application.Current?.PlatformSettings?.GetDoubleTapTime(Avalonia.Input.PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500)
        };
        _singleClickTimer.Tick += OnSingleClickTimerTick;
        _avaloniaIcon = new Avalonia.Controls.TrayIcon { Menu = new Avalonia.Controls.NativeMenu(), IsVisible = false };
        _avaloniaIcon.Clicked += OnAvaloniaClick;
        ToolTipText = text;
        SetIcon(icon);
        _avaloniaIconRegistration = DesktopServices.RegisterTrayIcon(_avaloniaIcon);
        Visible = visible;
    }

    public void SetIcon(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        _avaloniaIcon.Icon = new Avalonia.Controls.WindowIcon(stream);
    }

    private async void OnAvaloniaClick(object? sender, EventArgs e)
    {
        if (ApplicationState.Settings.TrayLeftDoubleClickAction == HotkeyType.None)
        {
            await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftClickAction);
        }
        else
        {
            _leftClickCount++;

            if (_leftClickCount == 1)
            {
                _singleClickTimer.Start();
            }
            else
            {
                _leftClickCount = 0;
                _singleClickTimer.Stop();
                await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftDoubleClickAction);
            }
        }
    }

    private async void OnSingleClickTimerTick(object? sender, EventArgs e)
    {
        _singleClickTimer.Stop();

        if (_leftClickCount == 1)
        {
            _leftClickCount = 0;
            await TaskHelpers.ExecuteJob(ApplicationState.Settings.TrayLeftClickAction);
        }
    }

    // Avalonia's DBusMenu exporter never raises Menu.Opening (AboutToShow only answers false) and greys out a submenu with no items,
    // so the whole tree is built ahead of time, and again whenever what it shows changes.
    public void RefreshMenu()
    {
        if (_disposed) return;
        BuildNativeMenu(_avaloniaIcon.Menu!, new MainMenuBuilder(true, nativeTrayMenu: true).BuildTrayMenu());
        PrefetchSubmenus();
    }

    // Plasma creates each submenu empty and fills it on AboutToShow, too late for many first hovers, and Avalonia's exporter renumbers every id on
    // any menu change, which empties them all again. LayoutUpdated(revision, submenu id) makes Plasma fill each one ahead of the hover, a level at a
    // time because it drops the reply for a submenu whose parent it hasn't created yet.
    // shortcut: reflects into private members of Avalonia 12.1.4's DBusMenu exporter (TrayMenuPrefetchTests lists them), replace when Avalonia keeps ids across a rebuild or fills submenus without AboutToShow.
    private async void PrefetchSubmenus()
    {
        int generation = ++_prefetchGeneration;
        try
        {
            if (_prefetchFailed || _avaloniaIcon.NativeMenuExporter is not Avalonia.Controls.Platform.ITopLevelNativeMenuExporter exporter) return;
            if (!exporter.IsNativeMenuExported)
            {
                // Nothing has read the menu yet, so a signal now would be lost. Waits on the event: with the icon hidden it may never come.
                if (_prefetchWaitingForExport) return;
                _prefetchWaitingForExport = true;
                void OnExported(object? sender, EventArgs e)
                {
                    exporter.OnIsNativeMenuExportedChanged -= OnExported;
                    _prefetchWaitingForExport = false;
                    PrefetchSubmenus();
                }
                exporter.OnIsNativeMenuExportedChanged += OnExported;
                return;
            }

            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = exporter.GetType();
            MethodInfo getId = type.GetMethod("GetId", Any) ?? throw new MissingMethodException(type.FullName, "GetId");
            FieldInfo revision = type.GetField("_revision", Any) ?? throw new MissingFieldException(type.FullName, "_revision");
            object connection = type.BaseType?.GetProperty("Connection", Any)?.GetValue(exporter) ?? throw new MissingMemberException(type.FullName, "Connection");
            string path = type.BaseType?.GetProperty("Path", Any)?.GetValue(exporter) as string ?? throw new MissingMemberException(type.FullName, "Path");
            MethodInfo emit = type.Assembly.GetType("Avalonia.FreeDesktop.DBus.dbusmenuSignal")?.GetMethod("EmitLayoutUpdated") ?? throw new MissingMethodException("dbusmenuSignal", "EmitLayoutUpdated");
            object objectPath = new Tmds.DBus.Protocol.ObjectPath(path);

            // The exporter queues its id reset at this priority, so this runs after it and the ids below are the new ones.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);

            System.Collections.Generic.List<Avalonia.Controls.NativeMenu> level = [_avaloniaIcon.Menu!];
            while (level.Count > 0)
            {
                await System.Threading.Tasks.Task.Delay(PrefetchLevelDelayMs);
                if (generation != _prefetchGeneration || _disposed) return;

                System.Collections.Generic.List<Avalonia.Controls.NativeMenu> next = [];
                foreach (Avalonia.Controls.NativeMenu menu in level)
                {
                    foreach (Avalonia.Controls.NativeMenuItemBase item in menu.Items)
                    {
                        if (item is not Avalonia.Controls.NativeMenuItem { Menu: not null } parent) continue;
                        emit.Invoke(null, [connection, objectPath, revision.GetValue(exporter), getId.Invoke(exporter, [parent])]);
                        next.Add(parent.Menu);
                    }
                }
                level = next;
            }
        }
        catch (Exception e)
        {
            // Every submenu still fills on AboutToShow, so the tray keeps working as it did without this.
            _prefetchFailed = true;
            DebugHelper.WriteException(e, "Tray submenu prefetch is off");
        }
    }

    private void BuildNativeMenu(Avalonia.Controls.NativeMenu menu, System.Collections.Generic.IReadOnlyList<MainMenuEntry> entries)
    {
        menu.Items.Clear();
        foreach (MainMenuEntry entry in entries)
        {
            if (!entry.IsVisible) continue;
            if (entry.IsSeparator) { menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator()); continue; }
            Avalonia.Controls.NativeMenuItem item = new(EscapeLabel(entry.Header))
            {
                IsEnabled = entry.IsEnabled,
                IsChecked = entry.IsChecked,
                ToggleType = entry.ToggleType switch
                {
                    MainMenuToggleType.CheckBox => Avalonia.Controls.MenuItemToggleType.CheckBox,
                    MainMenuToggleType.Radio => Avalonia.Controls.MenuItemToggleType.Radio,
                    _ => Avalonia.Controls.MenuItemToggleType.None
                }
            };
            if (entry.CreateChildren != null)
            {
                item.Menu = new Avalonia.Controls.NativeMenu();
                BuildNativeMenu(item.Menu, entry.CreateChildren());
            }
            else if (entry.CreateCategories != null)
            {
                item.Menu = new Avalonia.Controls.NativeMenu();
                foreach (MainMenuCategory category in entry.CreateCategories())
                {
                    Avalonia.Controls.NativeMenuItem categoryItem = new(EscapeLabel(category.Header)) { Menu = new Avalonia.Controls.NativeMenu() };
                    BuildNativeMenu(categoryItem.Menu, category.Entries);
                    item.Menu.Items.Add(categoryItem);
                }
            }
            else item.Click += async (_, _) =>
            {
                try
                {
                    if (entry.ExecuteAsync != null) await entry.ExecuteAsync();
                    RefreshMenu();
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            };
            menu.Items.Add(item);
        }
    }

    // A single underscore in a DBusMenu label marks a mnemonic and is not drawn, which would eat it from a file name.
    private static string EscapeLabel(string label) => label.Replace("_", "__");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _singleClickTimer.Stop();
        _singleClickTimer.Tick -= OnSingleClickTimerTick;
        _avaloniaIcon.Clicked -= OnAvaloniaClick;
        _avaloniaIconRegistration.Dispose();
    }
}
