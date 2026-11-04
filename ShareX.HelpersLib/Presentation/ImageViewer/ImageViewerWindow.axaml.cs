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
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

public partial class ImageViewerWindow : Window
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10;
    private const double ZoomFactor = 1.2;

    private readonly ImageViewerViewModel _viewModel = new();
    private readonly MatrixTransform _previewTransform = new() { Matrix = Matrix.Identity };
    private bool _closeOnDeactivate;
    private long _openedAt;
    private bool _acceptRelease;

    public ImageViewerWindow()
    {
        Initialize();
        Opened += OnOpened;
    }

    public ImageViewerWindow(string filePath)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadFile(filePath);
    }

    public ImageViewerWindow(IReadOnlyList<string> filePaths, int selectedIndex)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadFiles(filePaths, selectedIndex);
    }

    public ImageViewerWindow(byte[] imageData, string? displayName = null)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadEncodedImage(imageData, displayName);
    }

    private void Initialize()
    {
        DataContext = _viewModel;
        AvaloniaXamlLoader.Load(this);
        Image previewImage = this.FindControl<Image>("PreviewImage")!;
        previewImage.RenderTransform = _previewTransform;
        _viewModel.ImageReplaced += (_, _) => _previewTransform.Matrix = Matrix.Identity;
        _viewModel.FrameChanged += (_, _) => previewImage.InvalidateVisual();
        System.Drawing.Rectangle activeScreen = CaptureHelpers.GetActiveScreenBounds();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(activeScreen.X, activeScreen.Y);
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        Title = Localization.Strings.ImageViewerWindow_Title;
        Opened += (_, _) => _openedAt = Environment.TickCount64;
        KeyDown += OnKeyDown;
        Deactivated += OnDeactivated;
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>True while a click could still be the second half of the double-click that opened this window.</summary>
    internal bool IsWithinOpeningDoubleClick
    {
        get
        {
            TimeSpan doubleClickTime = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);
            return IsVisible && Environment.TickCount64 - _openedAt < doubleClickTime.TotalMilliseconds;
        }
    }

    /// <summary>Takes the focus back from the window behind. App focus is separate from KWin's, so Esc would otherwise reach that window.</summary>
    internal void TakeFocus()
    {
        Activate();
        Focus();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (!await SelectImageAsync())
        {
            Close();
        }
    }

    private async Task<bool> SelectImageAsync()
    {
        _closeOnDeactivate = false;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.Strings.ImageViewerWindow_Open_image,
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        string? filePath = files.FirstOrDefault()?.Path.LocalPath;
        bool loaded = !string.IsNullOrWhiteSpace(filePath) && _viewModel.LoadFile(filePath);
        _closeOnDeactivate = _viewModel.HasImage;
        Activate();
        Focus();
        return loaded;
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => _viewModel.Navigate(-1);
    private void OnNextClick(object? sender, RoutedEventArgs e) => _viewModel.Navigate(1);

    // The window is up within 50 ms of the click that opens it, so a double-click's second press lands here.
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e) => _acceptRelease = !IsWithinOpeningDoubleClick;

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_acceptRelease)
        {
            return;
        }

        _acceptRelease = false;

        if (e.InitialPressMouseButton is MouseButton.Left or MouseButton.Right)
        {
            Close();
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            _previewTransform.Matrix = Matrix.Identity;
            e.Handled = true;
        }
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_viewModel.HasImage && e.Delta.Y != 0 && sender is Border { Child: Image previewImage })
            {
                Matrix matrix = _previewTransform.Matrix;
                double zoom = Math.Clamp(matrix.M11 * Math.Pow(ZoomFactor, e.Delta.Y), MinZoom, MaxZoom);
                // Account for the image's layout offset and current transform when finding the anchor.
                Point pointerPosition = e.GetPosition(previewImage);
                _previewTransform.Matrix = new Matrix(zoom, 0, 0, zoom,
                    matrix.M31 + pointerPosition.X * (matrix.M11 - zoom),
                    matrix.M32 + pointerPosition.Y * (matrix.M22 - zoom));
            }

            e.Handled = true;
            return;
        }

        if (e.Delta.Y > 0 && _viewModel.CanNavigateLeft)
        {
            _viewModel.Navigate(-1);
            e.Handled = true;
        }
        else if (e.Delta.Y < 0 && _viewModel.CanNavigateRight)
        {
            _viewModel.Navigate(1);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                _viewModel.Navigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
                _viewModel.Navigate(1);
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Enter:
            case Key.Space:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_closeOnDeactivate)
        {
            return;
        }

        // A double-click's second press can reach the window behind just as this one comes up, and KWin then activates that window.
        if (IsWithinOpeningDoubleClick)
        {
            TakeFocus();
        }
        else
        {
            Close();
        }
    }
}
