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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShareX.HelpersLib;
using System.Collections.ObjectModel;
using System.Drawing;

namespace ShareX.Tools;

public sealed record InspectWindowProperty(string Name, string Value, bool IsMultiline = false);

public sealed partial class InspectWindowViewModel : ViewModelBase, IDisposable
{
    private string? _selectedWindowId;
    private bool _updating;

    public ObservableCollection<InspectWindowListItem> Windows { get; } = [];

    [ObservableProperty]
    private InspectWindowListItem? _selectedListItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _hasSelection;

    [ObservableProperty]
    private string _selectedTitle = Localization.Strings.InspectWindowViewModel_No_target_selected;

    [ObservableProperty]
    private string _selectedSubtitle = Localization.Strings.InspectWindowViewModel_Pick_a_window;

    [ObservableProperty]
    private IReadOnlyList<InspectWindowProperty> _details = [];

    [ObservableProperty]
    private bool _isTopMost;

    [ObservableProperty]
    private double _opacity = 100;

    public bool CanRefresh => HasSelection;
    public string ClipboardText => string.Join(Environment.NewLine + Environment.NewLine,
        Details.Select(x => $"{x.Name}{Environment.NewLine}{x.Value}"));

    public void SelectWindow(string? windowId)
    {
        if (string.IsNullOrEmpty(windowId))
        {
            return;
        }

        _selectedWindowId = windowId;
        SelectedListItem = null;
        UpdateWindowInfo();
    }

    partial void OnSelectedListItemChanged(InspectWindowListItem? value)
    {
        if (!_updating && value != null)
        {
            _selectedWindowId = value.WindowId;
            UpdateWindowInfo();
        }
    }

    public void ReloadWindowList()
    {
        _updating = true;
        try
        {
            Windows.Clear();
            foreach (InspectWindowListItem window in InspectWindowService.GetVisibleWindows())
            {
                Windows.Add(window);
            }
            SelectedListItem = null;
        }
        finally
        {
            _updating = false;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        UpdateWindowInfo();
    }

    partial void OnIsTopMostChanged(bool value)
    {
        if (_updating || _selectedWindowId == null)
        {
            return;
        }

        try
        {
            KWinDesktop.SetKeepAbove(_selectedWindowId, value);
            UpdateWindowInfo();
        }
        catch (KWinScriptException ex)
        {
            ToolsDiagnostics.ReportWarning(nameof(InspectWindowViewModel), "Failed to change window topmost state.", ex);
        }
    }

    partial void OnOpacityChanged(double value)
    {
        if (_updating || _selectedWindowId == null)
        {
            return;
        }

        try
        {
            KWinDesktop.SetOpacity(_selectedWindowId, Math.Clamp(value, 10, 100) / 100d);
        }
        catch (KWinScriptException ex)
        {
            ToolsDiagnostics.ReportWarning(nameof(InspectWindowViewModel), "Failed to change window opacity.", ex);
        }
    }

    private void UpdateWindowInfo()
    {
        if (_selectedWindowId == null)
        {
            ClearSelection();
            return;
        }

        _updating = true;
        try
        {
            KWinWindowDetails? window = KWinDesktop.GetWindowDetails(_selectedWindowId);

            if (window == null)
            {
                ClearSelection();
                return;
            }

            string processName = ReadFromProc(() => File.ReadAllText($"/proc/{window.Pid}/comm").TrimEnd());
            string processFileName = ReadFromProc(() => File.ResolveLinkTarget($"/proc/{window.Pid}/exe", false)?.FullName);

            SelectedTitle = string.IsNullOrWhiteSpace(window.Caption) ? Localization.Strings.InspectWindowViewModel_Untitled_window : window.Caption;
            SelectedSubtitle = string.IsNullOrWhiteSpace(processName)
                ? window.ResourceClass
                : string.IsNullOrWhiteSpace(window.ResourceClass) ? processName : $"{processName}  |  {window.ResourceClass}";
            Details = BuildDetails(window, processName, processFileName);
            IsTopMost = window.IsKeepAbove;
            Opacity = Math.Round(window.Opacity * 100d);
            HasSelection = true;
        }
        catch (KWinScriptException ex)
        {
            ToolsDiagnostics.ReportWarning(nameof(InspectWindowViewModel), "Failed to update inspected window information.", ex);
            ClearSelection();
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>A row with nothing to say is left out, so a field that doesn't apply to this window isn't shown blank.</summary>
    internal static IReadOnlyList<InspectWindowProperty> BuildDetails(KWinWindowDetails window, string processName, string processFileName)
    {
        InspectWindowProperty[] rows =
        [
            new(Localization.Strings.InspectWindowViewModel_Window_id, window.Id),
            new(Localization.Strings.InspectWindowViewModel_Window_title, window.Caption),
            new(Localization.Strings.InspectWindowViewModel_Class_name, window.ResourceClass),
            new(Localization.Strings.InspectWindowViewModel_Resource_name, window.ResourceName),
            new(Localization.Strings.InspectWindowViewModel_Window_role, window.WindowRole),
            new(Localization.Strings.InspectWindowViewModel_Desktop_file, window.DesktopFileName),
            new(Localization.Strings.InspectWindowViewModel_Process_name, processName),
            new(Localization.Strings.InspectWindowViewModel_Process_file_name, processFileName),
            new(Localization.Strings.InspectWindowViewModel_Process_identifier, window.Pid > 0 ? window.Pid.ToString() : string.Empty),
            new(Localization.Strings.InspectWindowViewModel_Window_rectangle, FormatRectangle(window.FrameGeometry)),
            new(Localization.Strings.InspectWindowViewModel_Client_rectangle, FormatRectangle(window.ClientGeometry)),
            new(Localization.Strings.InspectWindowViewModel_Output, window.OutputName),
            new(Localization.Strings.InspectWindowViewModel_Window_type, window.WindowType),
            new(Localization.Strings.InspectWindowViewModel_Window_state, string.Join(Environment.NewLine, window.State), true)
        ];

        return rows.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToArray();
    }

    private static string ReadFromProc(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private void ClearSelection()
    {
        _selectedWindowId = null;
        HasSelection = false;
        SelectedTitle = Localization.Strings.InspectWindowViewModel_No_target_selected;
        SelectedSubtitle = Localization.Strings.InspectWindowViewModel_Pick_a_window;
        Details = [];
        IsTopMost = false;
        Opacity = 100;
    }

    private static string FormatRectangle(Rectangle rectangle)
    {
        return rectangle.IsEmpty
            ? string.Empty
            : string.Format(Localization.Strings.InspectWindowViewModel_Rectangle_format, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    }

    public void Dispose()
    {
        Windows.Clear();
    }
}
