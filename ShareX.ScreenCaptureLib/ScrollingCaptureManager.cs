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

using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX.ScreenCaptureLib
{
    internal enum ScrollingFrameResult
    {
        Added,

        /// <summary>Nothing below what is already stitched, for example after scrolling back up.</summary>
        NoNewRows,

        /// <summary>The frame doesn't overlap the end of the stitched image, so there is no telling where it belongs.</summary>
        NoOverlap,

        HeightLimitReached
    }

    /// <summary>Captures a window or an area on a timer while the user scrolls it by hand, and stitches the frames.</summary>
    internal class ScrollingCaptureManager : IDisposable
    {
        // Short, because hand scrolling only stitches while frames overlap. Frames with no change cost little.
        private const int CaptureInterval = 100;

        public ScrollingCaptureOptions Options { get; private set; }
        public Bitmap Result { get; private set; }
        public bool IsCapturing { get; private set; }
        public Rectangle SelectedRectangle { get; private set; }

        /// <summary>True when frames overlap the stitched image again, false when they stopped. Raised on the thread that started the capture.</summary>
        public event Action<bool> OverlapChanged;

        private bool stopRequested;
        private bool overlapLost;
        private int bestMatchCount, bestIgnoreBottomOffset, frameHeight;
        private int? lastFrameHash;
        private KWinWindow selectedWindow;
        private bool wholeWindowSelected;
        private Rectangle selectedLogicalArea;

        public ScrollingCaptureManager(ScrollingCaptureOptions options)
        {
            Options = options;
        }

        public void Dispose()
        {
            Result?.Dispose();
            Result = null;
        }

        public async Task<ScrollingCaptureStatus> StartCapture()
        {
            if (IsCapturing || SelectedRectangle.IsEmpty)
            {
                return ScrollingCaptureStatus.Failed;
            }

            IsCapturing = true;
            stopRequested = false;
            overlapLost = false;
            bestMatchCount = 0;
            bestIgnoreBottomOffset = 0;
            lastFrameHash = null;
            Dispose();

            ScrollingCaptureRegionWindow regionWindow = null;

            if (Options.ShowRegion)
            {
                regionWindow = new ScrollingCaptureRegionWindow(SelectedRectangle);
                X11WindowState.SkipSwitcher(regionWindow);
                regionWindow.Show();
                // The outline only marks the area: the wheel has to reach what is under it.
                X11InputShape.SetEmpty(regionWindow);
            }

            try
            {
                // Pressing Start made ShareX active, and scrolling with the keyboard goes to the active window.
                if (selectedWindow != null && selectedWindow.Pid != Environment.ProcessId)
                {
                    await Task.Run(() => KWinDesktop.ActivateWindow(selectedWindow.Id));
                }

                await Task.Delay(Options.StartDelay);

                ScrollingCaptureStatus? ended = null;

                while (!stopRequested && ended == null)
                {
                    Stopwatch timer = Stopwatch.StartNew();

                    using (Bitmap frame = await Task.Run(CaptureFrame))
                    {
                        if (frame == null)
                        {
                            ended = ScrollingCaptureStatus.PartiallySuccessful;
                        }
                        else
                        {
                            bool wasLost = overlapLost;

                            if (await Task.Run(() => AddChangedFrame(frame)) == ScrollingFrameResult.HeightLimitReached)
                            {
                                ended = ScrollingCaptureStatus.HeightLimitReached;
                            }
                            else if (overlapLost != wasLost)
                            {
                                OverlapChanged?.Invoke(!overlapLost);
                            }
                        }
                    }

                    int delay = CaptureInterval - (int)timer.ElapsedMilliseconds;

                    if (delay > 0 && ended == null && !stopRequested)
                    {
                        await Task.Delay(delay);
                    }
                }

                if (Result == null)
                {
                    return ScrollingCaptureStatus.Failed;
                }

                return ended ?? (overlapLost ? ScrollingCaptureStatus.PartiallySuccessful : ScrollingCaptureStatus.Successful);
            }
            finally
            {
                regionWindow?.Close();
                IsCapturing = false;
            }
        }

        public void StopCapture()
        {
            if (IsCapturing)
            {
                stopRequested = true;
            }
        }

        public async Task<bool> SelectWindowAsync()
        {
            var selection = await RegionCaptureTasks.GetRectangleRegionAsync(new RegionCaptureOptions());
            if (selection == null)
            {
                return false;
            }

            DesktopCoordinates coordinates = (await Task.Run(KWinDesktop.Query)).Coordinates;

            SelectedRectangle = selection.Value.Rectangle;
            selectedWindow = selection.Value.WindowInfo;
            selectedLogicalArea = coordinates.X11ToLogical(SelectedRectangle);
            // The region overlay names the window under any selection. Only one that fills the selection was picked as a window.
            wholeWindowSelected = selectedWindow != null && coordinates.LogicalToX11(selectedWindow.FrameGeometry) == SelectedRectangle;
            return true;
        }

        /// <returns>Null when the window is gone or KWin refused, which is where the capture ends. Neither fallback fits here: spectacle
        /// is far too slow for a timer, and <see cref="Screenshot.CaptureWindow"/> would activate the window each time.</returns>
        private Bitmap CaptureFrame()
        {
            SpectacleImage image = wholeWindowSelected
                ? KWinScreenShot.TryCaptureWindow(selectedWindow.Id, SpectacleMode.ActiveWindowClientArea, false, out _)
                : KWinScreenShot.TryCaptureArea(selectedLogicalArea, false);

            return image?.Bitmap;
        }

        private ScrollingFrameResult AddChangedFrame(Bitmap frame)
        {
            // A hash, so only one frame is in memory at a time. A collision only skips this frame, and the next one has the same rows.
            HashCode hash = new();
            hash.AddBytes(frame.GetPixelSpan());
            int frameHash = hash.ToHashCode();

            if (frameHash == lastFrameHash)
            {
                return ScrollingFrameResult.NoNewRows;
            }

            lastFrameHash = frameHash;

            ScrollingFrameResult result = AddFrame(frame);

            if (result != ScrollingFrameResult.HeightLimitReached)
            {
                overlapLost = result == ScrollingFrameResult.NoOverlap;
            }

            return result;
        }

        /// <summary>Stitches one frame onto <see cref="Result"/>.</summary>
        internal ScrollingFrameResult AddFrame(Bitmap frame)
        {
            if (Result == null)
            {
                Result = frame.Copy();
                frameHeight = frame.Height;
                return ScrollingFrameResult.Added;
            }

            // A window that was resized no longer lines up.
            if (frame.Width != Result.Width || frame.Height != frameHeight)
            {
                return ScrollingFrameResult.NoOverlap;
            }

            ScrollingFrameResult result = CombineImages(Result, frame, out Bitmap newResult);

            if (newResult != null)
            {
                Result.Dispose();
                Result = newResult;
            }

            return result;
        }

        private static unsafe bool RowsEqual(byte* first, byte* second, int count)
            => new ReadOnlySpan<byte>(first, count).SequenceEqual(new ReadOnlySpan<byte>(second, count));

        // One colour from side to side. Such rows look alike all over a page, so they say nothing about where a frame belongs.
        private static unsafe bool IsPlainRow(byte* row, int count)
            => RowsEqual(row, row + 4, count - 4);

        private unsafe ScrollingFrameResult CombineImages(Bitmap result, Bitmap currentImage, out Bitmap newResult)
        {
            newResult = null;

            int ignoreSideOffset = Math.Max(50, currentImage.Width / 20);
            ignoreSideOffset = Math.Min(ignoreSideOffset, currentImage.Width / 3);

            // The bitmaps' own pixels: a copy of the stitched image for each frame would take a lot of memory.
            int stride = result.RowBytes;
            byte* resultScan0 = (byte*)result.GetPixels() + 4 * ignoreSideOffset;
            byte* currentImageScan0 = (byte*)currentImage.GetPixels() + 4 * ignoreSideOffset;
            int compareLength = 4 * (currentImage.Width - ignoreSideOffset * 2);

            int ignoreBottomOffsetMax = currentImage.Height / 3;
            int ignoreBottomOffset = Math.Max(50, currentImage.Height / 10);

            if (Options.AutoIgnoreBottomEdge)
            {
                byte* resultScan0Last = resultScan0 + (result.Height - 1) * stride;
                byte* currentImageScan0Last = currentImageScan0 + (currentImage.Height - 1) * stride;

                for (int i = 0; i <= ignoreBottomOffsetMax; i++)
                {
                    if (!RowsEqual(resultScan0Last - i * stride, currentImageScan0Last - i * stride, compareLength))
                    {
                        ignoreBottomOffset += i;
                        break;
                    }
                }

                ignoreBottomOffset = Math.Max(ignoreBottomOffset, bestIgnoreBottomOffset);
            }

            ignoreBottomOffset = Math.Min(ignoreBottomOffset, ignoreBottomOffsetMax);

            int resultBottom = result.Height - ignoreBottomOffset - 1;

            // usefulRows[n]: how many of the n rows going up from resultBottom have something on them.
            int[] usefulRows = new int[currentImage.Height + 1];

            for (int y = 0; y < currentImage.Height; y++)
            {
                usefulRows[y + 1] = usefulRows[y] + (resultBottom - y >= 0 && !IsPlainRow(resultScan0 + (resultBottom - y) * stride, compareLength) ? 1 : 0);
            }

            // A smaller overlap could allow bigger steps, but content that repeats would then match in the wrong place.
            int matchMinimum = Math.Max(24, currentImage.Height / 16);
            int matchCount = 0;
            int matchIndex = 0;

            if (usefulRows[currentImage.Height] >= matchMinimum)
            {
                for (int currentImageY = currentImage.Height - 1; currentImageY >= 0; currentImageY--)
                {
                    int currentMatchCount = 0;

                    while (currentImageY - currentMatchCount >= 0 && resultBottom - currentMatchCount >= 0 &&
                        RowsEqual(resultScan0 + (resultBottom - currentMatchCount) * stride, currentImageScan0 + (currentImageY - currentMatchCount) * stride, compareLength))
                    {
                        currentMatchCount++;
                    }

                    if (usefulRows[currentMatchCount] > matchCount)
                    {
                        matchCount = usefulRows[currentMatchCount];
                        matchIndex = currentImageY;
                    }
                }
            }

            if (matchCount < matchMinimum)
            {
                return ScrollingFrameResult.NoOverlap;
            }

            int matchHeight = currentImage.Height - matchIndex - 1;

            if (matchHeight <= ignoreBottomOffset)
            {
                return ScrollingFrameResult.NoNewRows;
            }

            int newHeight = result.Height - ignoreBottomOffset + matchHeight;

            if (newHeight > Options.MaxStitchedHeight)
            {
                return ScrollingFrameResult.HeightLimitReached;
            }

            if (matchCount > bestMatchCount)
            {
                bestMatchCount = matchCount;
                bestIgnoreBottomOffset = ignoreBottomOffset;
            }

            newResult = SkiaImageHelpers.CreateBitmap(result.Width, newHeight);

            using (SKCanvas g = new(newResult))
            using (SKPaint paint = new() { BlendMode = SKBlendMode.Src })
            {
                g.DrawBitmap(result, SKRect.Create(0, 0, result.Width, result.Height - ignoreBottomOffset),
                    SKRect.Create(0, 0, result.Width, result.Height - ignoreBottomOffset), paint);
                g.DrawBitmap(currentImage, SKRect.Create(0, matchIndex + 1, currentImage.Width, matchHeight),
                    SKRect.Create(0, result.Height - ignoreBottomOffset, currentImage.Width, matchHeight), paint);
            }

            return ScrollingFrameResult.Added;
        }
    }
}
