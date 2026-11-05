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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Linq;
using DrawingPoint = System.Drawing.Point;

namespace ShareX;

public partial class ActionsToolbarWindow : Window
{
    private bool _positionReady;
    private PixelPoint? _requestedPosition;
    private bool _adjustingPosition;
    private bool _closing;
    private PixelPoint? _dragStartPosition;

    public ActionsToolbarWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        Topmost = ApplicationState.Settings.ActionsToolbarStayTopMost;
        ApplicationState.Settings.ActionsToolbarList ??= [];

        ToolTip.SetTip(TitleHandle, Strings.ActionsToolbarWindow_Tip);
        ToolTip.SetPlacement(TitleHandle, PlacementMode.Top);
        ToolTip.SetVerticalOffset(TitleHandle, -4);
        ToolTip.SetShowDelay(TitleHandle, 400);
        ToolTip.SetBetweenShowDelay(TitleHandle, 100);
        TitleHandle.ContextMenu = CreateToolbarMenu();
        UpdateTitleCursor();
        RefreshToolbar();

        Opened += OnOpened;
        PositionChanged += OnPositionChanged;
        Closed += (_, _) => _closing = true;
    }

    internal void RefreshToolbar()
    {
        while (ToolbarItems.Children.Count > 1)
        {
            ToolbarItems.Children.RemoveAt(1);
        }

        foreach (HotkeyType action in ApplicationState.Settings.ActionsToolbarList)
        {
            if (action == HotkeyType.None)
            {
                ToolbarItems.Children.Add(new Border
                {
                    Width = 1,
                    Height = 22,
                    Margin = new Thickness(3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = this.FindResource("ShareX.Brush.Border") as Avalonia.Media.IBrush
                });
                continue;
            }

            TextBlock icon = new()
            {
                Text = TaskHelpers.FindMenuLucideIcon(action),
                FontSize = 17,
                TextAlignment = Avalonia.Media.TextAlignment.Center,
                Foreground = this.FindResource("ShareX.Brush.Accent") as Avalonia.Media.IBrush,
                IsHitTestVisible = false
            };
            icon.Classes.Add("icon");

            Button button = new()
            {
                Content = icon,
                Tag = action
            };
            button.Classes.Add("toolbar-action");
            ToolTip.SetTip(button, action.GetLocalizedDescription());
            ToolTip.SetPlacement(button, PlacementMode.Top);
            ToolTip.SetVerticalOffset(button, -4);
            ToolTip.SetShowDelay(button, 400);
            ToolTip.SetBetweenShowDelay(button, 100);
            button.Click += OnActionClick;
            ToolbarItems.Children.Add(button);
        }

        Dispatcher.UIThread.Post(ClampAndSavePosition, DispatcherPriority.Loaded);
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HotkeyType action })
        {
            return;
        }

        bool restoreTopmost = ApplicationState.Settings.ActionsToolbarStayTopMost;
        if (restoreTopmost)
        {
            Topmost = false;
        }

        try
        {
            await TaskHelpers.ExecuteJob(action);
        }
        finally
        {
            if (!_closing)
            {
                Topmost = ApplicationState.Settings.ActionsToolbarStayTopMost;
            }
        }
    }

    private ContextMenu CreateToolbarMenu()
    {
        ContextMenu menu = new()
        {
            Cursor = new Cursor(StandardCursorType.Arrow)
        };

        MenuItem close = new() { Header = Strings.ActionsToolbarWindow_Close };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
        menu.Items.Add(new Separator());

        MenuItem lockPosition = new()
        {
            Header = Strings.ActionsToolbarWindow_LockPosition,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = ApplicationState.Settings.ActionsToolbarLockPosition
        };
        lockPosition.Click += (_, _) =>
        {
            ApplicationState.Settings.ActionsToolbarLockPosition = lockPosition.IsChecked;
            UpdateTitleCursor();
            SaveSettings();
        };
        menu.Items.Add(lockPosition);

        MenuItem stayTopmost = new()
        {
            Header = Strings.ActionsToolbarWindow_StayOnTop,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = ApplicationState.Settings.ActionsToolbarStayTopMost
        };
        stayTopmost.Click += (_, _) =>
        {
            ApplicationState.Settings.ActionsToolbarStayTopMost = stayTopmost.IsChecked;
            Topmost = stayTopmost.IsChecked;
            SaveSettings();
        };
        menu.Items.Add(stayTopmost);

        MenuItem runAtStartup = new()
        {
            Header = Strings.ActionsToolbarWindow_OpenAtStartup,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = ApplicationState.Settings.ActionsToolbarRunAtStartup
        };
        runAtStartup.Click += (_, _) =>
        {
            ApplicationState.Settings.ActionsToolbarRunAtStartup = runAtStartup.IsChecked;
            SaveSettings();
        };
        menu.Items.Add(runAtStartup);
        menu.Items.Add(new Separator());

        MenuItem edit = new() { Header = Strings.ActionsToolbarWindow_Edit };
        edit.Click += async (_, _) => await ShowEditorAsync();
        menu.Items.Add(edit);

        return menu;
    }

    private async System.Threading.Tasks.Task ShowEditorAsync()
    {
        bool restoreTopmost = ApplicationState.Settings.ActionsToolbarStayTopMost;
        Topmost = false;

        try
        {
            ActionsToolbarEditorWindow editor = new(RefreshToolbar);
            await editor.ShowDialog(this);
        }
        finally
        {
            if (!_closing)
            {
                Topmost = restoreTopmost;
                RefreshToolbar();
            }
        }
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerUpdateKind kind = e.GetCurrentPoint(TitleHandle).Properties.PointerUpdateKind;
        if (kind == PointerUpdateKind.LeftButtonPressed && !ApplicationState.Settings.ActionsToolbarLockPosition)
        {
            _positionReady = true;
            _dragStartPosition = Position;
            BeginMoveDrag(e);
            e.Handled = true;
        }
        else if (kind == PointerUpdateKind.MiddleButtonPressed)
        {
            Close();
            e.Handled = true;
        }
    }

    private void UpdateTitleCursor()
    {
        TitleHandle.Cursor = new Cursor(ApplicationState.Settings.ActionsToolbarLockPosition
            ? StandardCursorType.Arrow
            : StandardCursorType.SizeAll);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RestorePosition();
        Activate();
    }

    private void RestorePosition()
    {
        DrawingPoint saved = ApplicationState.Settings.ActionsToolbarPosition;
        if (!saved.IsEmpty)
        {
            PixelPoint point = new(saved.X, saved.Y);
            if (Screens.All.Any(screen => screen.WorkingArea.Contains(point)))
            {
                RequestPosition(point);
                return;
            }
        }

        KWinDesktop? desktop = QueryDesktop();
        DrawingPoint cursor = desktop == null ? DrawingPoint.Empty : desktop.Coordinates.LogicalToX11(desktop.Cursor);
        Screen? screen = Screens.ScreenFromPoint(new PixelPoint(cursor.X, cursor.Y)) ?? Screens.Primary;
        if (screen == null)
        {
            _positionReady = true;
            return;
        }

        PixelRect area = WorkArea(desktop, cursor) ?? screen.WorkingArea;
        PixelSize size = PixelSize.FromSize(ClientSize, screen.Scaling);
        RequestPosition(new PixelPoint(area.Right - size.Width, area.Bottom - size.Height));
    }

    /// <returns>Null when KWin can't be asked, which is logged.</returns>
    private static KWinDesktop? QueryDesktop()
    {
        try
        {
            return KWinDesktop.Query();
        }
        catch (KWinScriptException e)
        {
            DebugHelper.WriteException(e);
            return null;
        }
    }

    /// <summary>KWin's work area of the output under an X11 point, which unlike Avalonia's leaves out a panel on an edge between two monitors.</summary>
    private static PixelRect? WorkArea(KWinDesktop? desktop, DrawingPoint x11Point)
    {
        return desktop?.X11WorkAreaAt(x11Point) is System.Drawing.Rectangle r ? new PixelRect(r.X, r.Y, r.Width, r.Height) : null;
    }

    /// <summary>
    /// KWin holds the pointer while it moves the window and gives it back when the drag ends, so the first pointer event after the window
    /// has moved means the drag is over, and the toolbar is clamped to KWin's work area. Only then is KWin asked, once, as each ask loads
    /// a KWin script and blocks for a few ms. A pointer event that comes before KWin takes the pointer finds the window not moved yet.
    /// The button state can't tell them apart: XWayland still reports the button down after KWin's drag.
    /// </summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragStartPosition is PixelPoint start && Position != start)
        {
            _dragStartPosition = null;
            ClampAndSavePosition(QueryDesktop());
        }
    }

    /// <summary>
    /// Until KWin answers, Position still reads 0,0 and then where KWin first placed the window. Under a top or left panel those are outside the
    /// work area, and clamping them would replace this request and save the wrong place. So nothing is clamped or saved until the answer comes.
    /// </summary>
    private void RequestPosition(PixelPoint point)
    {
        _requestedPosition = point;
        Position = point;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        _positionReady |= e.Point == _requestedPosition;

        if (_positionReady)
        {
            ClampAndSavePosition();
        }
    }

    private void ClampAndSavePosition() => ClampAndSavePosition(null);

    /// <param name="desktop">When given, clamps to KWin's work area of the output under the toolbar's middle instead of Avalonia's.</param>
    private void ClampAndSavePosition(KWinDesktop? desktop)
    {
        if (!_positionReady || _adjustingPosition)
        {
            return;
        }

        Screen? screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen == null)
        {
            return;
        }

        PixelSize size = PixelSize.FromSize(ClientSize, screen.Scaling);
        PixelRect area = WorkArea(desktop, new DrawingPoint(Position.X + size.Width / 2, Position.Y + size.Height / 2)) ?? screen.WorkingArea;
        int maxX = Math.Max(area.X, area.Right - size.Width);
        int maxY = Math.Max(area.Y, area.Bottom - size.Height);
        PixelPoint adjusted = new(
            Math.Clamp(Position.X, area.X, maxX),
            Math.Clamp(Position.Y, area.Y, maxY));

        if (adjusted != Position)
        {
            _adjustingPosition = true;
            Position = adjusted;
            _adjustingPosition = false;
        }

        ApplicationState.Settings.ActionsToolbarPosition = new DrawingPoint(adjusted.X, adjusted.Y);
    }

    private void OnDragEnter(object? sender, DragEventArgs e) => UpdateDragState(e);

    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDragState(e);

    private void UpdateDragState(DragEventArgs e)
    {
        bool supported = e.DataTransfer.TryGetFiles()?.Any() == true ||
            !string.IsNullOrEmpty(e.DataTransfer.TryGetText());
        e.DragEffects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = supported;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;

        UploadManager.DragDropUpload(e.DataTransfer);
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static void SaveSettings() => SettingManager.SaveApplicationConfigAsync();
}
