using ShareX.ScreenCaptureLib;
using SkiaSharp;
using System.Drawing;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class KWinScreenShotTests
{
    private const uint Rgbx8888 = 16;
    private const uint Rgba8888Premultiplied = 18;

    private static ScreenShotLayout Layout(uint width, uint height, uint stride, string? screen = null)
    {
        return ScreenShotLayout.Create("raw", width, height, stride, Rgba8888Premultiplied, screen)!.Value;
    }

    private static SKBitmap Read(Stream source, ScreenShotLayout layout)
    {
        SKBitmap bitmap = new(new SKImageInfo(layout.Width, layout.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        KWinScreenShot.ReadPixels(source, layout, bitmap);
        return bitmap;
    }

    // Catches: red and blue left swapped, which would turn every capture's colours wrong without any error.
    [Fact]
    public void ReadsRgbaBytesAsTheirOwnColours()
    {
        using SKBitmap bitmap = Read(new MemoryStream([200, 40, 10, 255]), Layout(1, 1, 4));

        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(0, 0));
    }

    // Catches: rows taken as width x 4 apart. With a stride larger than that, each row would start further off and the image would slant.
    [Fact]
    public void SkipsThePaddingBetweenRows()
    {
        byte[] data =
        [
            1, 2, 3, 255, 4, 5, 6, 255, 99, 99, 99, 99,
            7, 8, 9, 255, 10, 11, 12, 255, 99, 99, 99, 99
        ];

        using SKBitmap bitmap = Read(new MemoryStream(data), Layout(2, 2, 12));

        Assert.Equal(new SKColor(4, 5, 6, 255), bitmap.GetPixel(1, 0));
        Assert.Equal(new SKColor(7, 8, 9, 255), bitmap.GetPixel(0, 1));
        Assert.Equal(new SKColor(10, 11, 12, 255), bitmap.GetPixel(1, 1));
    }

    // Catches: a read taken to fill the row. A pipe hands over whatever it holds, which can stop in the middle of a pixel.
    [Fact]
    public void AcceptsReadsThatStopInsideAPixel()
    {
        byte[] data = new byte[4 * 3 * 2];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = i % 4 == 3 ? (byte)255 : (byte)(i * 5);
        }

        using SKBitmap bitmap = Read(new OneByteAtATimeStream(data), Layout(3, 2, 12));

        Assert.Equal(new SKColor(60, 65, 70, 255), bitmap.GetPixel(0, 1));
        Assert.Equal(new SKColor(100, 105, 110, 255), bitmap.GetPixel(2, 1));
    }

    // Catches: KWin stopping early being returned as a picture with its lower part empty, instead of falling back on spectacle.
    [Fact]
    public void AnImageThatStopsEarlyIsAnError()
    {
        Assert.Throws<EndOfStreamException>(() => Read(new MemoryStream(new byte[4 * 2 + 3]), Layout(2, 2, 8)));
    }

    // Catches: forcing every "RGBX" pixel opaque. KWin leaves the space between monitors of different sizes as 0, and an all-monitors capture would
    // turn it black, where the region overlay and saved images have it transparent.
    [Fact]
    public void AnAllMonitorsImageKeepsTheSpaceBetweenMonitorsTransparent()
    {
        ScreenShotLayout layout = ScreenShotLayout.Create("raw", 2, 1, 8, Rgbx8888, null)!.Value;

        using SKBitmap bitmap = Read(new MemoryStream([200, 40, 10, 255, 0, 0, 0, 0]), layout);

        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(0, 0));
        Assert.Equal((byte)0, bitmap.GetPixel(1, 0).Alpha);
    }

    // Catches: the opposite, on one monitor: KWin doesn't promise that unused byte, and spectacle's PNG of one output has no alpha at all.
    [Fact]
    public void AOneMonitorImageIsOpaque()
    {
        ScreenShotLayout layout = ScreenShotLayout.Create("raw", 1, 1, 4, Rgbx8888, "DP-1")!.Value;

        using SKBitmap bitmap = Read(new MemoryStream([200, 40, 10, 0]), layout);

        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(0, 0));
    }

    // Catches: a window hanging off a monitor showing in the space beside it, which KWin draws and nobody can see on screen.
    [Fact]
    public void ClearsWhatIsOutsideTheOutputs()
    {
        using SKBitmap bitmap = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 40, 10, 255));

        // An output 2x2 at the left, and one 2x1 at the top right: the bottom right corner is on neither.
        KWinScreenShot.ClearOutside(bitmap, [new Rectangle(0, 0, 2, 2), new Rectangle(2, 0, 2, 1)]);

        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(1, 1));
        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(3, 0));
        Assert.Equal(new SKColor(0, 0, 0, 0), bitmap.GetPixel(2, 1));
        Assert.Equal(new SKColor(0, 0, 0, 0), bitmap.GetPixel(3, 1));
    }

    // Catches: clearing on a desktop with no gaps, or two outputs mirrored on one rectangle, which would blank part of an ordinary capture.
    [Fact]
    public void LeavesAFullDesktopAlone()
    {
        using SKBitmap bitmap = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(200, 40, 10, 255));

        KWinScreenShot.ClearOutside(bitmap, [new Rectangle(0, 0, 2, 2), new Rectangle(2, 0, 2, 2), new Rectangle(2, 0, 2, 2)]);

        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(200, 40, 10, 255), bitmap.GetPixel(3, 1));
    }

    // Catches: a crop off by its position, which every rectangle capture and AutoCapture shot goes through.
    [Fact]
    public void CropsTheRectangleAskedFor()
    {
        using SKBitmap image = new(new SKImageInfo(5, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
        image.Erase(new SKColor(10, 10, 10, 255));
        image.SetPixel(3, 2, new SKColor(200, 40, 10, 255));

        using SKBitmap crop = Screenshot.Crop(image, new Rectangle(2, 1, 3, 2));

        Assert.Equal((3, 2), (crop.Width, crop.Height));
        Assert.Equal(new SKColor(200, 40, 10, 255), crop.GetPixel(1, 1));
        Assert.Equal(new SKColor(10, 10, 10, 255), crop.GetPixel(0, 0));
    }

    // Catches: reading a format or layout that isn't understood, which would give garbage or run off the end of the bitmap rather than falling back.
    [Theory]
    [InlineData("raw", 0u, 1u, 4u, Rgbx8888)]
    [InlineData("raw", 1u, 0u, 4u, Rgbx8888)]
    [InlineData("raw", 2u, 1u, 4u, Rgbx8888)] // Stride too small for the width.
    [InlineData("raw", 1u, 1u, 4u, 6u)] // ARGB32_Premultiplied is BGRA in memory.
    [InlineData("raw", 1u, 1u, 16u, 31u)] // RGBA16FPx4, as an HDR capture might be.
    [InlineData("png", 1u, 1u, 4u, Rgbx8888)]
    [InlineData(null, 1u, 1u, 4u, Rgbx8888)]
    [InlineData("raw", uint.MaxValue, 1u, uint.MaxValue, Rgbx8888)] // Width x 4 overflows.
    public void RefusesLayoutsItCannotRead(string? type, uint width, uint height, uint stride, uint format)
    {
        Assert.Null(ScreenShotLayout.Create(type, width, height, stride, format, null));
    }

    private sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}
