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
using ShareX.ImageEditor.Integration;
using SkiaSharp;
using System.Runtime.InteropServices;
using HarfBuzzBlob = HarfBuzzSharp.Blob;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;
using HarfBuzzFace = HarfBuzzSharp.Face;
using HarfBuzzFont = HarfBuzzSharp.Font;

namespace ShareX.ImageEditor.Presentation.Emoji;

public static class EmojiBitmapRenderer
{
    private const int GrinningFace = 0x1F600;
    private const int PreviewPadding = 6;
    private const int StickerPadding = 14;
    private const int MaxStickerCacheEntries = 256;
    private const float RawCanvasScale = 2.0f;
    private const float RawFontScale = 1.32f;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, WeakReference<Bitmap>> PreviewCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SKBitmap> StickerCache = new(StringComparer.Ordinal);
    private static readonly Lazy<SKTypeface?> EmojiTypeface = new(FindEmojiTypeface);
    private static readonly Lazy<(HarfBuzzFont Font, int UnitsPerEm)?> Shaper = new(CreateShaper);

    /// <summary>No installed font has emoji. The emoji font is a host dependency: noto-fonts-emoji on Arch.</summary>
    public static bool FontIsMissing => EmojiTypeface.Value == null;

    // "emoji" is fontconfig's generic family, so this is the font `fc-match emoji` names.
    private static SKTypeface? FindEmojiTypeface()
    {
        SKTypeface? typeface = SKFontManager.Default.MatchFamily("emoji");
        if (typeface?.ContainsGlyph(GrinningFace) == true) return typeface;
        typeface?.Dispose();
        typeface = SKFontManager.Default.MatchCharacter(GrinningFace);
        if (typeface?.ContainsGlyph(GrinningFace) == true) return typeface;
        typeface?.Dispose();
        return null;
    }

    // Skia's DrawText maps one code point to one glyph, which splits flags and joined sequences such as families.
    // The font's own substitution rules, through HarfBuzz, turn those into their single glyph.
    private static (HarfBuzzFont Font, int UnitsPerEm)? CreateShaper()
    {
        if (EmojiTypeface.Value is not SKTypeface typeface) return null;
        using SKStreamAsset stream = typeface.OpenStream(out int index);
        int length = stream.Length;
        // In native memory for the life of the process. A blob from Blob.FromStream crashed the shaper
        // with SIGSEGV as it read the font's tables.
        IntPtr data = Marshal.AllocHGlobal(length);
        stream.Read(data, length);
        using HarfBuzzBlob blob = new(data, length, HarfBuzzSharp.MemoryMode.ReadOnly);
        using HarfBuzzFace face = new(blob, index);
        HarfBuzzFont font = new(face);
        font.SetFunctionsOpenType();
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
        return (font, face.UnitsPerEm);
    }

    public static Bitmap? RenderPreviewBitmap(string unicodeSequence, int size)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        string cacheKey = $"{unicodeSequence}:{size}";

        lock (SyncRoot)
        {
            if (PreviewCache.TryGetValue(cacheKey, out var reference) && reference.TryGetTarget(out Bitmap? cached))
            {
                return cached;
            }

            using SKBitmap? bitmap = RenderSquareBitmap(unicodeSequence, size, PreviewPadding);
            if (bitmap == null)
            {
                return null;
            }

            Bitmap preview = BitmapConversionHelpers.ToAvaloniBitmap(bitmap);
            // Controls borrow previews; evict references without disposing images still in use.
            if (PreviewCache.Count >= 256) PreviewCache.Clear();
            PreviewCache[cacheKey] = new WeakReference<Bitmap>(preview);
            return preview;
        }
    }

    public static SKBitmap? RenderStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, size);
    }

    public static SKBitmap? RenderInteractiveStickerBitmap(string unicodeSequence, int size = 160)
    {
        if (string.IsNullOrWhiteSpace(unicodeSequence) || size <= 0)
        {
            return null;
        }

        return RenderStickerBitmapCore(unicodeSequence, GetInteractiveStickerSize(size));
    }

    public static int GetInteractiveStickerSize(int size)
    {
        size = Math.Max(1, size);

        if (size <= 64)
        {
            return size;
        }

        int step = size <= 128 ? 4 : size <= 256 ? 8 : 12;
        return Math.Max(64, (int)Math.Round(size / (double)step) * step);
    }

    private static SKBitmap? RenderSquareBitmap(string unicodeSequence, int size, int padding)
    {
        string glyph = EmojiCatalogService.ToGlyph(unicodeSequence);
        if (string.IsNullOrEmpty(glyph))
        {
            return null;
        }

        try
        {
            int rawCanvasSize = Math.Max(64, (int)Math.Ceiling(size * RawCanvasScale));
            float rawFontSize = Math.Max(24, size * RawFontScale);

            using SKBitmap? rawBitmap = RenderGlyph(glyph, rawCanvasSize, rawFontSize);
            if (rawBitmap == null)
            {
                return null;
            }

            using SKBitmap trimmedBitmap = TrimTransparentBounds(rawBitmap) ?? rawBitmap.Copy();
            return FitIntoSquare(trimmedBitmap, size, padding);
        }
        catch (Exception e)
        {
            EditorServices.ReportWarning(nameof(EmojiBitmapRenderer), "Emoji rendering failed.", e);
            return null;
        }
    }

    private static SKBitmap? RenderGlyph(string glyph, int canvasSize, float fontSize)
    {
        if (EmojiTypeface.Value is not SKTypeface typeface || Shaper.Value is not (HarfBuzzFont shaper, int unitsPerEm))
        {
            return null;
        }

        using HarfBuzzBuffer buffer = new();
        buffer.AddUtf16(glyph);
        buffer.GuessSegmentProperties();
        shaper.Shape(buffer);

        HarfBuzzSharp.GlyphInfo[] infos = buffer.GlyphInfos;
        HarfBuzzSharp.GlyphPosition[] positions = buffer.GlyphPositions;

        // Glyph 0 is the font's missing-glyph box: an emoji newer than the font gets nothing rather than a box.
        if (infos.Length == 0 || infos.Any(info => info.Codepoint == 0))
        {
            return null;
        }

        using SKFont font = new(typeface, fontSize) { EmbeddedBitmaps = true, Edging = SKFontEdging.Antialias };
        float scale = fontSize / unitsPerEm;
        using SKTextBlobBuilder builder = new();
        SKPositionedRunBuffer run = builder.AllocatePositionedRun(font, infos.Length);
        Span<ushort> runGlyphs = run.GetGlyphSpan();
        Span<SKPoint> runPositions = run.GetPositionSpan();
        float advance = 0;
        for (int i = 0; i < infos.Length; i++)
        {
            runGlyphs[i] = (ushort)infos[i].Codepoint;
            runPositions[i] = new SKPoint(advance + positions[i].XOffset * scale, -positions[i].YOffset * scale);
            advance += positions[i].XAdvance * scale;
        }

        using SKTextBlob? blob = builder.Build();
        if (blob == null)
        {
            return null;
        }

        var bitmap = new SKBitmap(new SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.Clear(SKColors.Transparent);

        // The caller trims to what was drawn, so this only has to keep it all on the canvas.
        float fit = Math.Min(1, canvasSize / Math.Max(advance, 1));
        canvas.Translate(canvasSize / 2f, canvasSize / 2f);
        canvas.Scale(fit);
        canvas.DrawText(blob, -advance / 2f, fontSize * 0.35f, paint);
        canvas.Flush();
        return bitmap;
    }

    private static SKBitmap? TrimTransparentBounds(SKBitmap source)
    {
        int minX = source.Width;
        int minY = source.Height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.GetPixel(x, y).Alpha == 0)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return null;
        }

        int width = Math.Max(1, maxX - minX + 1);
        int height = Math.Max(1, maxY - minY + 1);
        var trimmed = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using var canvas = new SKCanvas(trimmed);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(
            source,
            new SKRectI(minX, minY, maxX + 1, maxY + 1),
            new SKRect(0, 0, width, height));

        return trimmed;
    }

    private static SKBitmap FitIntoSquare(SKBitmap source, int size, int padding)
    {
        var output = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        using var paint = new SKPaint
        {
            IsAntialias = true
        };

        canvas.Clear(SKColors.Transparent);

        float availableWidth = Math.Max(1, size - (padding * 2));
        float availableHeight = Math.Max(1, size - (padding * 2));
        float scale = Math.Min(availableWidth / source.Width, availableHeight / source.Height);
        float drawWidth = Math.Max(1, source.Width * scale);
        float drawHeight = Math.Max(1, source.Height * scale);
        float left = (size - drawWidth) / 2f;
        float top = (size - drawHeight) / 2f;

        using SKImage sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, new SKRect(left, top, left + drawWidth, top + drawHeight), new SKSamplingOptions(SKCubicResampler.CatmullRom), paint);
        return output;
    }

    private static SKBitmap? RenderStickerBitmapCore(string unicodeSequence, int size)
    {
        string cacheKey = $"{unicodeSequence}:{size}";

        lock (SyncRoot)
        {
            if (StickerCache.TryGetValue(cacheKey, out SKBitmap? cachedBitmap))
            {
                return cachedBitmap.Copy();
            }

            SKBitmap? renderedBitmap = RenderSquareBitmap(unicodeSequence, size, StickerPadding);
            if (renderedBitmap == null)
            {
                return null;
            }

            if (StickerCache.Count >= MaxStickerCacheEntries)
            {
                ClearStickerCache();
            }

            StickerCache[cacheKey] = renderedBitmap.Copy();
            return renderedBitmap;
        }
    }

    private static void ClearStickerCache()
    {
        foreach (SKBitmap bitmap in StickerCache.Values)
        {
            bitmap.Dispose();
        }

        StickerCache.Clear();
    }
}