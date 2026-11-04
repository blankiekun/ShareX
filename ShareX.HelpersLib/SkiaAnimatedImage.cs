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

using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace ShareX.HelpersLib;

/// <summary>Decodes animation frames together with the prior frames required by their disposal rules.</summary>
public sealed class SkiaAnimatedImage : IDisposable
{
    private readonly SKCodec codec;
    private readonly SKCodecFrameInfo[] frameInfo;
    public int FrameCount => Math.Max(1, codec.FrameCount);
    public int Width => codec.Info.Width;
    public int Height => codec.Info.Height;
    public SKEncodedImageFormat Format => codec.EncodedFormat;

    public SkiaAnimatedImage(string filePath)
    {
        // The codec reads the file as it goes, so a long recording stays on disk rather than in memory.
        codec = SKCodec.Create(filePath) ?? throw new InvalidDataException("The image format is not supported.");
        frameInfo = codec.FrameInfo;
    }

    /// <summary>How long the file asks for this frame to stay on screen, in milliseconds.</summary>
    public int GetFrameDuration(int index) => frameInfo.Length > index ? frameInfo[index].Duration : 0;

    /// <summary>
    /// Decodes a frame over a bitmap that holds <paramref name="shownFrame"/>, or -1 when it holds nothing useful.
    /// Going through frames in order this way decodes each one once, where <see cref="GetFrame"/> replays its required frames.
    /// </summary>
    public void DecodeFrame(SKBitmap bitmap, int index, int shownFrame)
    {
        int required = frameInfo.Length > index ? frameInfo[index].RequiredFrame : -1;
        if (required < 0)
        {
            bitmap.Erase(SKColors.Transparent);
            Decode(bitmap, index, -1);
        }
        else if (shownFrame >= required && shownFrame < index &&
            frameInfo[shownFrame].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
        {
            Decode(bitmap, index, shownFrame);
        }
        else
        {
            // The bitmap can't be built on, so the codec decodes the required frames itself first.
            Decode(bitmap, index, -1);
        }
    }

    private void Decode(SKBitmap bitmap, int frame, int priorFrame)
    {
        SKCodecResult result = codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(frame, priorFrame));
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            throw new InvalidDataException($"Frame {frame} decoding failed: {result}.");
    }

    public SKBitmap GetFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FrameCount);
        SKBitmap bitmap = SkiaImageHelpers.CreateBitmap(Width, Height);
        try
        {
            Stack<int> frames = new();
            int required = index;
            while (required >= 0)
            {
                frames.Push(required);
                required = frameInfo.Length > required ? frameInfo[required].RequiredFrame : -1;
            }
            int previous = -1;
            while (frames.Count > 0)
            {
                int frame = frames.Pop();
                Decode(bitmap, frame, previous);
                previous = frame;
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        codec.Dispose();
    }
}
