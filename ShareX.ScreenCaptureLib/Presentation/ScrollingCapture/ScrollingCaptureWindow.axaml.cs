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
using Avalonia.Media;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using System;
using System.IO;
using System.Threading.Tasks;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingBitmap = SkiaSharp.SKBitmap;
using ImageFormat = SkiaSharp.SKEncodedImageFormat;

namespace ShareX.ScreenCaptureLib;

public partial class ScrollingCaptureWindow : Window
{
    private const double CompactWidth = 480;

    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);

    private readonly ScrollingCaptureService _service;
    private readonly Action<DrawingBitmap>? _uploadRequested;
    private readonly Action? _playNotificationSound;
    private AvaloniaBitmap? _previewBitmap;
    private bool _captureOperation;
    private bool _areaSelected;
    private bool _closeRequested;
    private bool _isPanning;
    private Size _normalSize;
    private PixelPoint _normalPosition;
    private Point _panStart;
    private Vector _panStartOffset;

    public ScrollingCaptureWindow()
        : this(new ScrollingCaptureOptions(), null, null)
    {
    }

    public ScrollingCaptureWindow(
        ScrollingCaptureOptions options,
        Action<DrawingBitmap>? uploadRequested,
        Action? playNotificationSound)
    {
        _service = new ScrollingCaptureService(options);
        _uploadRequested = uploadRequested;
        _playNotificationSound = playNotificationSound;

        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        WindowState = Avalonia.Controls.WindowState.Minimized;

        Opened += OnOpened;
        Closing += OnClosing;
        Resized += OnResized;
        _service.OverlapChanged += OnOverlapChanged;
        Closed += OnClosed;
    }

    public async Task StartStopAsync()
    {
        if (_service.IsCapturing)
        {
            StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Stopping_capture;
            _service.StopCapture();
            return;
        }

        if (_captureOperation)
        {
            return;
        }

        if (_areaSelected)
        {
            await CaptureSelectedWindowAsync();
        }
        else
        {
            await SelectWindowAsync();
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await StartStopAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_service.IsCapturing)
        {
            _closeRequested = true;
            _service.StopCapture();
            e.Cancel = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewBitmap?.Dispose();
        _service.Dispose();
    }

    private async Task SelectWindowAsync()
    {
        _captureOperation = true;
        OptionsOverlay.IsVisible = false;
        WindowState = Avalonia.Controls.WindowState.Minimized;

        try
        {
            await Task.Delay(250);

            if (await _service.SelectWindowAsync())
            {
                _areaSelected = true;
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Press_start;
                CaptureButtonText.Text = Localization.Strings.ScreenRecordWindow_Start;
                SetCompact(true);
                RestoreAndActivate();
                // The size of the title bar is only known once KWin has the window back on screen.
                await Task.Delay(200);
                PlaceBesideArea();
            }
            else
            {
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Selection_cancelled;
                RestoreAndActivate();
            }
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            StatusText.Text = ex.Message;
            SetStatus(ScrollingCaptureStatus.Failed);
            RestoreAndActivate();
            ex.ShowError();
        }
        finally
        {
            _captureOperation = false;
        }
    }

    private async Task CaptureSelectedWindowAsync()
    {
        _areaSelected = false;
        SetCaptureControlsEnabled(false);
        ResultSizeText.Text = string.Empty;
        ResetPreview();
        StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Scroll_now;
        StatusIcon.Text = LucideIcons.loader_circle;
        StatusIcon.Foreground = Brushes.DodgerBlue;
        CaptureButtonText.Text = Localization.Strings.ScrollingCaptureWindow_Stop;

        try
        {
            ScrollingCaptureStatus status = await _service.StartCaptureAsync();
            SetStatus(status);
            _playNotificationSound?.Invoke();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex);
            StatusText.Text = ex.Message;
            SetStatus(ScrollingCaptureStatus.Failed);
            ex.ShowError();
        }
        finally
        {
            CaptureButtonText.Text = Localization.Strings.ScrollingCaptureWindow_Capture;
            SetCompact(false);
            SetCaptureControlsEnabled(true);
            LoadImage(_service.Result);
            RestoreAndActivate();
        }

        if (_service.Options.AutoUpload)
        {
            UploadResult();
        }

        if (_closeRequested)
        {
            Close();
        }
    }

    private void OnOverlapChanged(bool overlaps)
    {
        StatusText.Text = overlaps ? Localization.Strings.ScrollingCaptureWindow_Scroll_now : Localization.Strings.ScrollingCaptureWindow_Scroll_back;
    }

    /// <summary>Only the button and the message, on top and beside the area, so the wheel keeps reaching what is scrolled.</summary>
    private void SetCompact(bool compact)
    {
        UploadButton.IsVisible = !compact;
        CopyButton.IsVisible = !compact;
        OptionsButton.IsVisible = !compact;
        HelpButton.IsVisible = !compact;
        PreviewPanel.IsVisible = !compact;
        ResultSizeText.IsVisible = !compact;
        Topmost = compact;

        if (compact)
        {
            _normalSize = new Size(Width, Height);
            _normalPosition = Position;
            MinWidth = 0;
            MinHeight = 0;
            Width = CompactWidth;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            MinWidth = 620;
            MinHeight = 440;
            Width = _normalSize.Width;
            Height = _normalSize.Height;
            Position = _normalPosition;
        }
    }

    // The new size arrives some time after asking for it, and the place depends on it.
    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        if (SizeToContent != SizeToContent.Manual)
        {
            PlaceBesideArea();
        }
    }

    private void PlaceBesideArea()
    {
        System.Drawing.Rectangle area = _service.SelectedRectangle;
        var screen = Screens.ScreenFromBounds(new PixelRect(area.X, area.Y, area.Width, area.Height)) ?? Screens.Primary;

        if (screen == null)
        {
            return;
        }

        PixelRect work = screen.WorkingArea;
        Size frame = FrameSize ?? ClientSize;
        int width = (int)Math.Ceiling(frame.Width * screen.Scaling);
        int height = (int)Math.Ceiling(frame.Height * screen.Scaling);
        int gap = (int)Math.Ceiling(8 * screen.Scaling);

        // Above, otherwise below. An area that leaves room for neither gets the bar over its top, which a capture doesn't show.
        int top = area.Top - height - gap >= work.Y ? area.Top - height - gap :
            area.Bottom + gap + height <= work.Bottom ? area.Bottom + gap : work.Y;

        Position = new PixelPoint(Math.Clamp(area.Left, work.X, Math.Max(work.X, work.Right - width)), top);
    }

    private void SetCaptureControlsEnabled(bool enabled)
    {
        OptionsButton.IsEnabled = enabled;
        UploadButton.IsEnabled = enabled && _service.Result != null;
        CopyButton.IsEnabled = enabled && _service.Result != null;
    }

    private void SetStatus(ScrollingCaptureStatus status)
    {
        switch (status)
        {
            case ScrollingCaptureStatus.Failed:
                StatusIcon.Text = LucideIcons.circle_x;
                StatusIcon.Foreground = Brushes.IndianRed;
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Capture_failed;
                break;
            case ScrollingCaptureStatus.PartiallySuccessful:
                StatusIcon.Text = LucideIcons.triangle_alert;
                StatusIcon.Foreground = Brushes.Goldenrod;
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Capture_partially_successful;
                break;
            case ScrollingCaptureStatus.Successful:
                StatusIcon.Text = LucideIcons.circle_check;
                StatusIcon.Foreground = Brushes.MediumSeaGreen;
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Capture_successful;
                break;
            case ScrollingCaptureStatus.HeightLimitReached:
                StatusIcon.Text = LucideIcons.triangle_alert;
                StatusIcon.Foreground = Brushes.Goldenrod;
                StatusText.Text = Localization.Strings.ScrollingCaptureWindow_Height_limit_reached;
                break;
        }
    }

    private void LoadImage(DrawingBitmap? bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        using MemoryStream stream = new();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        AvaloniaBitmap preview = new(stream);
        _previewBitmap?.Dispose();
        _previewBitmap = preview;
        PreviewImage.Source = preview;
        PreviewScrollViewer.Offset = default;
        EmptyState.IsVisible = false;
        ResultSizeText.Text = $"{bitmap.Width}x{bitmap.Height}";
        UploadButton.IsEnabled = true;
        CopyButton.IsEnabled = true;
    }

    private void ResetPreview()
    {
        PreviewImage.Source = null;
        _previewBitmap?.Dispose();
        _previewBitmap = null;
        EmptyState.IsVisible = true;
        UploadButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
    }

    private void RestoreAndActivate()
    {
        WindowState = Avalonia.Controls.WindowState.Normal;

        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    private void UploadResult()
    {
        if (_service.Result != null)
        {
            _uploadRequested?.Invoke((DrawingBitmap)_service.Result.Copy());
        }
    }

    private void CopyResult()
    {
        if (_service.Result != null)
        {
            ClipboardHelpers.CopyImage(_service.Result);
        }
    }

    private async void OnCaptureClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await StartStopAsync();

    private void OnUploadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => UploadResult();

    private void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CopyResult();

    private void OnHelpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => URLHelpers.OpenURL(Links.DocsScrollingScreenshot);

    private void OnOptionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LoadOptions();
        OptionsOverlay.IsVisible = true;
    }

    private void OnCancelOptionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OptionsOverlay.IsVisible = false;
    }

    private void OnSaveOptionsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ScrollingCaptureOptions options = _service.Options;
        options.StartDelay = Value(StartDelayInput);
        options.AutoUpload = AutoUploadInput.IsChecked == true;
        options.ShowRegion = ShowRegionInput.IsChecked == true;
        options.AutoIgnoreBottomEdge = AutoIgnoreBottomEdgeInput.IsChecked == true;
        OptionsOverlay.IsVisible = false;
    }

    private void LoadOptions()
    {
        ScrollingCaptureOptions options = _service.Options;
        StartDelayInput.Value = options.StartDelay;
        AutoUploadInput.IsChecked = options.AutoUpload;
        ShowRegionInput.IsChecked = options.ShowRegion;
        AutoIgnoreBottomEdgeInput.IsChecked = options.AutoIgnoreBottomEdge;
    }

    private static int Value(NumericUpDown input) => (int)(input.Value ?? 0);

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PreviewImage.Source == null || !e.GetCurrentPoint(PreviewImage).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStart = e.GetPosition(PreviewScrollViewer);
        _panStartOffset = PreviewScrollViewer.Offset;
        PreviewImage.Cursor = PanCursor;
        e.Pointer.Capture(PreviewImage);
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || e.Pointer.Captured != PreviewImage)
        {
            return;
        }

        Point position = e.GetPosition(PreviewScrollViewer);
        Vector delta = position - _panStart;
        PreviewScrollViewer.Offset = new Vector(
            _panStartOffset.X - delta.X,
            _panStartOffset.Y - delta.Y);
        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Captured == PreviewImage)
        {
            e.Pointer.Capture(null);
        }

        EndPanning();
    }

    private void OnPreviewPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndPanning();

    private void EndPanning()
    {
        _isPanning = false;
        PreviewImage.Cursor = null;
    }
}
