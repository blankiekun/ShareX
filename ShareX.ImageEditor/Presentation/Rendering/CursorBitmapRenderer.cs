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

using Avalonia.Media.Imaging;
using ShareX.ImageEditor.Core.Annotations;
using SkiaSharp;
using System.Buffers.Binary;
using ShareX.ImageEditor.Integration;

namespace ShareX.ImageEditor.Presentation.Rendering
{
    internal static class CursorBitmapRenderer
    {
        private const uint XcursorImageType = 0xFFFD0002;
        private const int PreferredCursorSize = 32;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<CursorType, SKBitmap?> AnnotationBitmapCache = new();
        private static readonly Dictionary<(CursorType CursorType, int PreviewSize), Bitmap?> PreviewBitmapCache = new();
        // These cursor images previously came from a runtime WinForms reflection lookup.
        private static readonly IReadOnlyDictionary<CursorType, string> BundledCursors = new Dictionary<CursorType, string>
        {
            [CursorType.HSplit] = "hsplit",
            [CursorType.VSplit] = "vsplit",
            [CursorType.NoMove2D] = "nomove2d",
            [CursorType.NoMoveHoriz] = "nomoveh",
            [CursorType.NoMoveVert] = "nomovev",
            [CursorType.PanEast] = "east",
            [CursorType.PanNE] = "ne",
            [CursorType.PanNorth] = "north",
            [CursorType.PanNW] = "nw",
            [CursorType.PanSE] = "se",
            [CursorType.PanSouth] = "south",
            [CursorType.PanSW] = "sw",
            [CursorType.PanWest] = "west",
        };
        // The stock cursors were Windows' own images, so they come from the desktop's cursor theme, by the names themes use.
        private static readonly IReadOnlyDictionary<CursorType, string[]> ThemeCursors = new Dictionary<CursorType, string[]>
        {
            [CursorType.AppStarting] = ["left_ptr_watch", "progress"],
            [CursorType.Arrow] = ["left_ptr", "default"],
            [CursorType.Cross] = ["crosshair", "cross"],
            [CursorType.Default] = ["left_ptr", "default"],
            [CursorType.Hand] = ["pointing_hand", "hand2", "pointer"],
            [CursorType.Help] = ["question_arrow", "help"],
            [CursorType.IBeam] = ["xterm", "text"],
            [CursorType.No] = ["not-allowed", "forbidden", "crossed_circle"],
            [CursorType.SizeAll] = ["size_all", "fleur", "all-scroll"],
            [CursorType.SizeNESW] = ["size_bdiag", "nesw-resize"],
            [CursorType.SizeNS] = ["size_ver", "ns-resize"],
            [CursorType.SizeNWSE] = ["size_fdiag", "nwse-resize"],
            [CursorType.SizeWE] = ["size_hor", "ew-resize"],
            [CursorType.UpArrow] = ["up_arrow", "center_ptr"],
            [CursorType.WaitCursor] = ["wait", "watch"]
        };

        public static SKBitmap? CreateAnnotationBitmap(CursorType cursorType)
        {
            lock (SyncRoot)
            {
                if (!AnnotationBitmapCache.TryGetValue(cursorType, out SKBitmap? cachedBitmap))
                {
                    cachedBitmap = RenderCursorBitmap(cursorType);
                    AnnotationBitmapCache[cursorType] = cachedBitmap;
                }

                return cachedBitmap?.Copy();
            }
        }

        public static Bitmap? GetPreviewBitmap(CursorType cursorType, int previewSize = 28)
        {
            lock (SyncRoot)
            {
                var cacheKey = (cursorType, previewSize);

                if (!PreviewBitmapCache.TryGetValue(cacheKey, out Bitmap? previewBitmap))
                {
                    previewBitmap = CreatePreviewBitmap(cursorType, previewSize);
                    PreviewBitmapCache[cacheKey] = previewBitmap;
                }

                return previewBitmap;
            }
        }

        private static Bitmap? CreatePreviewBitmap(CursorType cursorType, int previewSize)
        {
            using SKBitmap? annotationBitmap = CreateAnnotationBitmap(cursorType);
            if (annotationBitmap == null)
            {
                return null;
            }

            int canvasSize = Math.Max(16, previewSize);

            using var previewBitmap = new SKBitmap(new SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(previewBitmap);
            using var paint = new SKPaint { IsAntialias = true };

            canvas.Clear(SKColors.Transparent);

            float maxWidth = Math.Max(1, canvasSize - 4f);
            float maxHeight = Math.Max(1, canvasSize - 4f);
            float scale = Math.Min(maxWidth / annotationBitmap.Width, maxHeight / annotationBitmap.Height);

            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0)
            {
                scale = 1f;
            }

            float drawWidth = annotationBitmap.Width * scale;
            float drawHeight = annotationBitmap.Height * scale;
            float left = (canvasSize - drawWidth) / 2f;
            float top = (canvasSize - drawHeight) / 2f;

            canvas.DrawBitmap(annotationBitmap, new SKRect(left, top, left + drawWidth, top + drawHeight), paint);
            canvas.Flush();

            return BitmapConversionHelpers.ToAvaloniBitmap(previewBitmap);
        }

        private static SKBitmap? RenderCursorBitmap(CursorType cursorType)
        {
            if (BundledCursors.TryGetValue(cursorType, out string? asset))
            {
                using Stream? source = typeof(CursorBitmapRenderer).Assembly.GetManifestResourceStream(
                    $"ShareX.ImageEditor.Assets.Cursors.{asset}.cur");
                if (source == null) return null;
                using MemoryStream stream = new();
                source.CopyTo(stream);
                byte[] data = stream.ToArray();
                // CUR and ICO share their DIB payload. Replace the cursor directory's
                // hotspot fields with icon planes/depth, then let Skia decode its mask.
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 1);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), 1);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 14)));
                return SKBitmap.Decode(data);
            }

            return RenderThemeCursor(cursorType);
        }

        private static SKBitmap? RenderThemeCursor(CursorType cursorType)
        {
            if (!ThemeCursors.TryGetValue(cursorType, out string[]? names)) return null;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
            string[] roots = [Path.Combine(dataHome, "icons"), Path.Combine(home, ".icons"), "/usr/share/icons"];
            string?[] themes = [Environment.GetEnvironmentVariable("XCURSOR_THEME"), "breeze_cursors", "Adwaita"];

            foreach (string? theme in themes)
            {
                if (string.IsNullOrEmpty(theme)) continue;
                foreach (string root in roots)
                {
                    foreach (string name in names)
                    {
                        string path = Path.Combine(root, theme, "cursors", name);
                        if (!File.Exists(path)) continue;
                        try
                        {
                            SKBitmap? bitmap = ReadXcursor(File.ReadAllBytes(path));
                            if (bitmap != null) return bitmap;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                        {
                            EditorServices.ReportWarning(nameof(CursorBitmapRenderer), $"Could not read cursor {path}.", e);
                        }
                    }
                }
            }

            EditorServices.ReportWarning(nameof(CursorBitmapRenderer), $"No cursor theme has a cursor for {cursorType}.");
            return null;
        }

        // The Xcursor format: a table of contents, then one chunk per image at each nominal size. Animated
        // cursors have several images at one size, and the first is used. This takes the smallest size at or
        // above the size Windows' cursors had, or else the largest.
        private static SKBitmap? ReadXcursor(byte[] data)
        {
            ReadOnlySpan<byte> file = data;
            if (file.Length < 16 || !file[..4].SequenceEqual("Xcur"u8)) return null;
            int headerSize = BinaryPrimitives.ReadInt32LittleEndian(file[4..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(file[12..]);
            int bestSize = 0, bestPosition = -1;
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = file.Slice(headerSize + i * 12, 12);
                if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != XcursorImageType) continue;
                int size = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
                bool better = bestPosition < 0 || (size >= PreferredCursorSize
                    ? bestSize < PreferredCursorSize || size < bestSize
                    : bestSize < PreferredCursorSize && size > bestSize);
                if (better)
                {
                    bestSize = size;
                    bestPosition = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
                }
            }
            if (bestPosition < 0) return null;

            ReadOnlySpan<byte> chunk = file[bestPosition..];
            int chunkHeader = BinaryPrimitives.ReadInt32LittleEndian(chunk);
            int width = BinaryPrimitives.ReadInt32LittleEndian(chunk[16..]);
            int height = BinaryPrimitives.ReadInt32LittleEndian(chunk[20..]);
            if (width is <= 0 or > 1024 || height is <= 0 or > 1024) return null;
            ReadOnlySpan<byte> pixels = chunk.Slice(chunkHeader, width * height * 4);

            // Stored as premultiplied ARGB words, which on a little-endian machine is Skia's BGRA.
            SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            pixels.CopyTo(bitmap.GetPixelSpan());
            return bitmap;
        }
    }
}