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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ShareX.AvaloniaUI.Input;
using ShareX.AvaloniaUI.Theming;
using SkiaSharp;

namespace ShareX.AvaloniaUI.Windows
{
    public partial class ScreenColorPickerWindow : Window
    {
        private const double PreviewOffset = 18;
        private const int MagnifierPixelCount = 15;

        private readonly ScreenColorPickerOptions _options;
        private readonly TaskCompletionSource<ScreenColorPickerResult?> _completionSource = new();
        private Border _pickerPreview = null!;
        private Border _colorPreview = null!;
        private Grid _magnifierPanel = null!;
        private Image _magnifierImage = null!;
        private TextBlock _infoText = null!;
        private WriteableBitmap? _magnifierBitmap;
        private SKBitmap? _screen;
        private PixelRect _screenBounds;
        private Color _currentColor;
        private PixelPoint _currentPosition;
        private PointerAction _pointerAction;
        private Color _pressedColor;
        private PixelPoint _pressedPosition;

        /// <summary>
        /// Takes a screenshot of the whole desktop, or returns null after telling the user why it can't. A client can't read screen
        /// pixels on Wayland, so colors come from this image. This project can't reach the capture code, so the app sets it at startup.
        /// </summary>
        public static Func<Task<SKBitmap?>>? CaptureScreenshotAsync { get; set; }

        public ScreenColorPickerWindow() : this(new ScreenColorPickerOptions())
        {
        }

        public ScreenColorPickerWindow(ScreenColorPickerOptions options)
        {
            _options = options ?? new ScreenColorPickerOptions();
            RequestedThemeVariant = ThemeManager.GetCurrentTheme();
            AvaloniaXamlLoader.Load(this);

            _pickerPreview = this.FindControl<Border>("PickerPreview")!;
            _colorPreview = this.FindControl<Border>("ColorPreview")!;
            _magnifierPanel = this.FindControl<Grid>("MagnifierPanel")!;
            _magnifierImage = this.FindControl<Image>("MagnifierImage")!;
            _infoText = this.FindControl<TextBlock>("InfoText")!;

            _magnifierPanel.IsVisible = _options.ShowMagnifier;

            if (_options.ShowMagnifier)
            {
                _magnifierBitmap = new WriteableBitmap(
                    new PixelSize(MagnifierPixelCount, MagnifierPixelCount),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                _magnifierImage.Source = _magnifierBitmap;
            }

            Opened += OnOpened;
        }

        public async Task<ScreenColorPickerResult?> PickAsync(Window? owner = null)
        {
            using SKBitmap? screenshot = CaptureScreenshotAsync == null ? null : await CaptureScreenshotAsync();

            if (screenshot == null)
            {
                return null;
            }

            ConfigureOverlayBounds(screenshot);

            if (owner != null)
            {
                Show(owner);
            }
            else
            {
                Show();
            }

            return await _completionSource.Task;
        }

        protected override void OnClosed(EventArgs e)
        {
            _completionSource.TrySetResult(null);
            _screen?.Dispose();
            base.OnClosed(e);
        }

        private void OnOpened(object? sender, EventArgs e)
        {
            // KWin places a new window out from under a panel's reserved strip, and this one spans every monitor, so FullScreen
            // can't help. A move after mapping is left alone.
            Position = _screenBounds.Position;
            Activate();
            Focus();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            Point clientPoint = e.GetPosition(this);
            UpdatePicker(this.PointToScreen(clientPoint), clientPoint);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            PointerPoint pointerPoint = e.GetCurrentPoint(this);
            UpdatePicker(this.PointToScreen(pointerPoint.Position), pointerPoint.Position);

            if (pointerPoint.Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                _pressedColor = _currentColor;
                _pressedPosition = _currentPosition;
                _pointerAction = PointerAction.Select;
                e.Pointer.Capture(this);
                return;
            }

            if (pointerPoint.Properties.IsRightButtonPressed)
            {
                e.Handled = true;
                _pointerAction = PointerAction.Cancel;
                e.Pointer.Capture(this);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            if (_pointerAction == PointerAction.None)
            {
                return;
            }

            e.Handled = true;
            e.Pointer.Capture(null);

            PointerAction action = _pointerAction;
            _pointerAction = PointerAction.None;
            Complete(action == PointerAction.Select
                ? new ScreenColorPickerResult(_pressedColor, _pressedPosition,
                    e.KeyModifiers.HasFlag(KeyModifiers.Control))
                : null);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Complete(null);
            }
        }

        private void ConfigureOverlayBounds(SKBitmap screenshot)
        {
            var screens = Screens.All;
            if (screens.Count == 0)
            {
                return;
            }

            int left = screens.Min(screen => screen.Bounds.X);
            int top = screens.Min(screen => screen.Bounds.Y);
            int right = screens.Max(screen => screen.Bounds.X + screen.Bounds.Width);
            int bottom = screens.Max(screen => screen.Bounds.Y + screen.Bounds.Height);
            var topLeft = new PixelPoint(left, top);
            double scaling = Screens.ScreenFromPoint(topLeft)?.Scaling ?? screens[0].Scaling;

            // A scale can make the screenshot's size differ from the desktop's in X11 pixels, which is what a pointer position is in.
            _screenBounds = new PixelRect(left, top, right - left, bottom - top);
            _screen = new SKBitmap(new SKImageInfo(_screenBounds.Width, _screenBounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            screenshot.ScalePixels(_screen, new SKSamplingOptions(SKFilterMode.Nearest));

            Position = topLeft;
            Width = (right - left) / scaling;
            Height = (bottom - top) / scaling;
            Cursor = CursorAssetLoader.GetCrosshairCursor(scaling);
        }

        private void UpdatePicker(PixelPoint screenPoint, Point clientPoint)
        {
            if (_screen != null && _screenBounds.Contains(screenPoint))
            {
                Color color = ReadPixel(screenPoint);

                if (_magnifierBitmap != null)
                {
                    UpdateMagnifier(screenPoint, _magnifierBitmap);
                }

                _currentColor = color;
                _currentPosition = screenPoint;
                _colorPreview.Background = new SolidColorBrush(color);
                _infoText.Text = ScreenColorPickerTextFormatter.Format(_options.InfoText, color, screenPoint);
            }

            MovePreviewNextToCursor(clientPoint);
            _pickerPreview.Opacity = 1;
        }

        private void MovePreviewNextToCursor(Point cursorPosition)
        {
            double previewWidth = Math.Max(1, _pickerPreview.Bounds.Width);
            double previewHeight = Math.Max(1, _pickerPreview.Bounds.Height);
            double x = cursorPosition.X + PreviewOffset;
            double y = cursorPosition.Y + PreviewOffset;

            if (x + previewWidth > Bounds.Width)
            {
                x = cursorPosition.X - previewWidth - PreviewOffset;
            }

            if (y + previewHeight > Bounds.Height)
            {
                y = cursorPosition.Y - previewHeight - PreviewOffset;
            }

            Canvas.SetLeft(_pickerPreview, Math.Clamp(x, 0, Math.Max(0, Bounds.Width - previewWidth)));
            Canvas.SetTop(_pickerPreview, Math.Clamp(y, 0, Math.Max(0, Bounds.Height - previewHeight)));
        }

        private void Complete(ScreenColorPickerResult? result)
        {
            if (_completionSource.TrySetResult(result))
            {
                Close();
            }
        }

        private Color ReadPixel(PixelPoint point)
        {
            SKColor pixel = _screen!.GetPixel(point.X - _screenBounds.X, point.Y - _screenBounds.Y);
            return Color.FromRgb(pixel.Red, pixel.Green, pixel.Blue);
        }

        private unsafe void UpdateMagnifier(PixelPoint center, WriteableBitmap bitmap)
        {
            int radius = MagnifierPixelCount / 2;

            using (ILockedFramebuffer framebuffer = bitmap.Lock())
            {
                for (int y = 0; y < MagnifierPixelCount; y++)
                {
                    uint* row = (uint*)((byte*)framebuffer.Address + y * framebuffer.RowBytes);

                    for (int x = 0; x < MagnifierPixelCount; x++)
                    {
                        PixelPoint point = new(center.X - radius + x, center.Y - radius + y);
                        Color color = _screenBounds.Contains(point) ? ReadPixel(point) : Colors.Black;
                        row[x] = 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                    }
                }
            }

            _magnifierImage.InvalidateVisual();
        }

        private enum PointerAction
        {
            None,
            Select,
            Cancel
        }
    }
}
