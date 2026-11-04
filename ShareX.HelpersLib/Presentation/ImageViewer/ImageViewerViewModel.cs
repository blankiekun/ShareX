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

using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Imaging;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

public sealed class ImageViewerViewModel : INotifyPropertyChanged, IDisposable
{
    private string[] _images = [];
    private int _currentImageIndex;
    private Bitmap? _currentImage;
    private string? _currentImageFilePath;
    private string _statusText = string.Empty;
    private CancellationTokenSource? _playback;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Another image took over the view. An animation moving to its next frame doesn't count.</summary>
    public event EventHandler? ImageReplaced;

    /// <summary>The pixels of <see cref="CurrentImage"/> changed without the bitmap itself changing.</summary>
    public event EventHandler? FrameChanged;

    public Bitmap? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (ReferenceEquals(_currentImage, value))
            {
                return;
            }

            _currentImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public string? CurrentImageFilePath
    {
        get => _currentImageFilePath;
        private set
        {
            if (_currentImageFilePath == value)
            {
                return;
            }

            _currentImageFilePath = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool SupportWrap { get; set; }
    public bool HasImage => CurrentImage != null;
    public bool CanNavigate => _images.Length > 1;
    public bool CanNavigateLeft => CanNavigate && (SupportWrap || _currentImageIndex > 0);
    public bool CanNavigateRight => CanNavigate && (SupportWrap || _currentImageIndex < _images.Length - 1);

    public bool LoadFile(string filePath)
    {
        if (!File.Exists(filePath) || !FileHelpers.IsImageFile(filePath))
        {
            return false;
        }

        string? folder = Path.GetDirectoryName(filePath);
        _images = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(FileHelpers.IsImageFile).ToArray()
            : [filePath];

        _currentImageIndex = Array.FindIndex(_images,
            path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        if (_currentImageIndex < 0)
        {
            _images = [filePath];
            _currentImageIndex = 0;
        }

        return LoadCurrentImage();
    }

    public bool LoadFiles(IReadOnlyList<string> filePaths, int selectedIndex)
    {
        List<string> images = [];
        int filteredSelectedIndex = -1;
        for (int index = 0; index < filePaths.Count; index++)
        {
            string path = filePaths[index];
            if (!File.Exists(path) || !FileHelpers.IsImageFile(path))
            {
                continue;
            }

            if (index == selectedIndex)
            {
                filteredSelectedIndex = images.Count;
            }
            images.Add(path);
        }

        _images = images.ToArray();
        if (_images.Length == 0)
        {
            return false;
        }

        _currentImageIndex = filteredSelectedIndex >= 0
            ? filteredSelectedIndex
            : Math.Clamp(selectedIndex, 0, _images.Length - 1);

        return LoadCurrentImage();
    }

    public bool LoadEncodedImage(byte[] imageData, string? displayName = null)
    {
        if (imageData.Length == 0)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(imageData, writable: false);
            ReplaceImage(new Bitmap(stream));
            _images = [];
            _currentImageIndex = 0;
            CurrentImageFilePath = displayName;
            UpdateStatus();
            return true;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Failed to load image viewer data.");
            return false;
        }
    }

    public void Navigate(int offset)
    {
        if (!CanNavigate)
        {
            return;
        }

        int nextIndex = _currentImageIndex + offset;
        if (SupportWrap)
        {
            nextIndex = (nextIndex + _images.Length) % _images.Length;
        }
        else
        {
            nextIndex = Math.Clamp(nextIndex, 0, _images.Length - 1);
        }

        if (nextIndex != _currentImageIndex)
        {
            _currentImageIndex = nextIndex;
            LoadCurrentImage();
        }
    }

    private bool LoadCurrentImage()
    {
        if (_images.Length == 0)
        {
            return false;
        }

        try
        {
            CurrentImageFilePath = _images[_currentImageIndex];
            ReplaceImage(new Bitmap(CurrentImageFilePath));
            UpdateStatus();
            StartPlayback(CurrentImageFilePath);
            return true;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception,
                $"Failed to load image '{CurrentImageFilePath}'.");
            return false;
        }
    }

    private void ReplaceImage(Bitmap image)
    {
        StopPlayback();
        Bitmap? previous = CurrentImage;
        CurrentImage = image;
        previous?.Dispose();
        ImageReplaced?.Invoke(this, EventArgs.Empty);
        NotifyNavigationChanged();
    }

    private void StartPlayback(string filePath)
    {
        _playback = new CancellationTokenSource();
        CancellationToken token = _playback.Token;
        _ = Task.Run(() => PlayAnimationAsync(filePath, token), token);
    }

    // Cancelled only on the UI thread, which is also the only place PresentFrame runs, so a frame can't be
    // written to a bitmap after this returns.
    private void StopPlayback()
    {
        _playback?.Cancel();
        _playback = null;
    }

    /// <summary>
    /// Runs off the UI thread and keeps only the frame being decoded plus the one on screen, so memory doesn't grow
    /// with the length of the file. Frame 0 is on screen already as the still from <see cref="LoadCurrentImage"/>.
    /// </summary>
    private async Task PlayAnimationAsync(string filePath, CancellationToken token)
    {
        try
        {
            using SkiaAnimatedImage animation = new(filePath);
            int frameCount = animation.FrameCount;
            if (frameCount < 2)
            {
                return;
            }

            using SKBitmap frame = SkiaImageHelpers.CreateBitmap(animation.Width, animation.Height);
            animation.DecodeFrame(frame, 0, -1);
            int shown = 0;
            long due = Stopwatch.GetTimestamp() + GetFrameDelayTicks(animation, 0);

            while (!token.IsCancellationRequested)
            {
                int next = (shown + 1) % frameCount;
                animation.DecodeFrame(frame, next, shown);

                long wait = due - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), token);
                }

                await Dispatcher.UIThread.InvokeAsync(() => PresentFrame(frame, token), DispatcherPriority.Normal, token);
                shown = next;

                // Keep to the file's timeline through small delays, but if decoding fell behind, carry on from now
                // rather than rushing frames to catch up.
                due = Math.Max(due + GetFrameDelayTicks(animation, next), Stopwatch.GetTimestamp());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to play animation '{filePath}'.");
        }
    }

    private static long GetFrameDelayTicks(SkiaAnimatedImage animation, int frame)
    {
        // Browsers show a frame asking for 10 ms or less for 100 ms. Old files use a delay of 0 to mean "as fast as
        // you can", and they were made to look right at that speed.
        int duration = animation.GetFrameDuration(frame);
        return (duration <= 10 ? 100 : duration) * Stopwatch.Frequency / 1000;
    }

    private void PresentFrame(SKBitmap frame, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        if (CurrentImage is WriteableBitmap buffer)
        {
            using ILockedFramebuffer target = buffer.Lock();
            using SKPixmap source = frame.PeekPixels();
            source.ReadPixels(frame.Info, target.Address, target.RowBytes);
        }
        else
        {
            // First frame of playback: swap the still for a bitmap later frames can be written into, so that
            // frames never have to replace a bitmap the Image control is drawing.
            Bitmap? still = CurrentImage;
            CurrentImage = BitmapConversionHelpers.ToAvaloniBitmap(frame);
            still?.Dispose();
        }

        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateStatus()
    {
        List<string> parts = [];
        if (CanNavigate)
        {
            parts.Add($"{_currentImageIndex + 1} / {_images.Length}");
        }

        if (!string.IsNullOrWhiteSpace(CurrentImageFilePath))
        {
            string fileName = Path.GetFileName(CurrentImageFilePath);
            parts.Add(fileName.Length > 128 ? $"{fileName[..125]}..." : fileName);
        }

        if (CurrentImage != null)
        {
            parts.Add($"{CurrentImage.PixelSize.Width} × {CurrentImage.PixelSize.Height}");
        }

        StatusText = string.Join("  |  ", parts);
        NotifyNavigationChanged();
    }

    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(CanNavigateLeft));
        OnPropertyChanged(nameof(CanNavigateRight));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        StopPlayback();
        Bitmap? image = CurrentImage;
        CurrentImage = null;
        image?.Dispose();
    }
}
