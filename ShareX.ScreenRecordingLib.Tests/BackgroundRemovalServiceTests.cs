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

using ShareX.Tools;
using SkiaSharp;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>Runs the real ONNX Runtime on tiny models written here byte by byte, so no real model is needed.</summary>
public sealed class BackgroundRemovalServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-bg-test").FullName;
    private readonly BackgroundRemovalService _service = new();

    public void Dispose()
    {
        _service.Dispose();
        Directory.Delete(_folder, true);
    }

    // Catches a colour image or a row of class scores being read as a mask: the result would look processed, with nonsense alpha.
    [Theory]
    [InlineData(new[] { 32, 64 }, true)]
    [InlineData(new[] { 1, 32, 64 }, true)]
    [InlineData(new[] { 1, 1, 32, 64 }, true)]
    [InlineData(new[] { 1, 32, 64, 1 }, true)]
    [InlineData(new[] { 1, 1000 }, false)]
    [InlineData(new[] { 3, 32, 64 }, false)]
    [InlineData(new[] { 1, 3, 32, 64 }, false)]
    [InlineData(new[] { 1, 32, 64, 3 }, false)]
    [InlineData(new[] { 1, 1, 1, 1 }, false)]
    [InlineData(new[] { 64 }, false)]
    public void TryResolveMaskSize_OnlyAcceptsASingleChannelImage(int[] dimensions, bool expected)
    {
        bool actual = BackgroundRemovalService.TryResolveMaskSize(dimensions, out int width, out int height);

        Assert.Equal(expected, actual);
        if (expected)
        {
            Assert.Equal((64, 32), (width, height));
        }
    }

    // Catches the mask not reaching the alpha channel, or being stretched wrongly on its way back to the source size.
    [Fact]
    public void RemoveBackground_PutsTheMaskInTheAlphaChannel()
    {
        using SKBitmap source = LeftWhiteRightBlack();

        BackgroundRemovalResult result = _service.RemoveBackground(source, Model("mean.onnx", MeanMaskModel([1, 3, 8, 8], [1])));

        using SKBitmap image = result.Image;
        Assert.Equal((source.Width, source.Height), (image.Width, image.Height));
        Assert.Equal(255, image.GetPixel(1, 4).Alpha);
        Assert.Equal(0, image.GetPixel(14, 4).Alpha);
    }

    // Catches a damaged file reaching the user as a raw runtime error, or taking the process down.
    [Fact]
    public void RemoveBackground_DamagedFile_SaysWhichFileCouldNotBeLoaded()
    {
        using SKBitmap source = LeftWhiteRightBlack();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _service.RemoveBackground(source, Model("damaged.onnx", [1, 2, 3, 4, 5])));

        Assert.Contains("damaged.onnx", ex.Message);
    }

    // Catches the old session being disposed but still matching the cache key, which failed every later run with the good model.
    [Fact]
    public void RemoveBackground_GoodModelStillWorks_AfterADamagedOneFailed()
    {
        using SKBitmap source = LeftWhiteRightBlack();
        BackgroundRemovalModel good = Model("good.onnx", MeanMaskModel([1, 3, 8, 8], [1]));

        _service.RemoveBackground(source, good).Image.Dispose();
        Assert.Throws<InvalidOperationException>(() => _service.RemoveBackground(source, Model("damaged.onnx", [1, 2, 3, 4, 5])));

        using SKBitmap image = _service.RemoveBackground(source, good).Image;
        Assert.Equal(255, image.GetPixel(1, 4).Alpha);
    }

    // Catches models that load but aren't background removers: one takes 4 channels, the other returns a score per channel.
    [Fact]
    public void RemoveBackground_WrongShapes_SayTheModelDoesNotFit()
    {
        using SKBitmap source = LeftWhiteRightBlack();

        InvalidOperationException wrongInput = Assert.Throws<InvalidOperationException>(() =>
            _service.RemoveBackground(source, Model("input.onnx", MeanMaskModel([1, 4, 8, 8], [1]))));
        InvalidOperationException wrongOutput = Assert.Throws<InvalidOperationException>(() =>
            _service.RemoveBackground(source, Model("output.onnx", MeanMaskModel([1, 3, 8, 8], [2, 3]))));

        Assert.Contains("background removal model", wrongInput.Message);
        Assert.Contains("background removal model", wrongOutput.Message);
    }

    // Catches a model's declared size being allocated before anything checks it: 100000x100000 is 40 GB of bitmap.
    [Fact]
    public void RemoveBackground_HugeDeclaredInput_IsRefusedBeforeAllocating()
    {
        using SKBitmap source = LeftWhiteRightBlack();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _service.RemoveBackground(source, Model("huge.onnx", MeanMaskModel([1, 3, 100000, 100000], [1]))));

        Assert.Contains("too large", ex.Message);
    }

    private BackgroundRemovalModel Model(string name, byte[] bytes)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return new BackgroundRemovalModel { FilePath = path, FileName = name, FileSize = bytes.Length };
    }

    private static SKBitmap LeftWhiteRightBlack()
    {
        SKBitmap bitmap = new(new SKImageInfo(16, 8, SKColorType.Bgra8888, SKAlphaType.Premul));

        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, x < bitmap.Width / 2 ? SKColors.White : SKColors.Black);
            }
        }

        return bitmap;
    }

    /// <summary>An ONNX graph with one ReduceMean node, keeping the reduced axes. Field numbers are from onnx.proto.</summary>
    private static byte[] MeanMaskModel(long[] inputShape, long[] axes)
    {
        byte[] axesAttribute = [.. Text(1, "axes"), .. axes.SelectMany(axis => Varint(8, axis)), .. Varint(20, 7)];
        byte[] keepDimsAttribute = [.. Text(1, "keepdims"), .. Varint(3, 1), .. Varint(20, 2)];
        byte[] node = [.. Text(1, "x"), .. Text(2, "y"), .. Text(4, "ReduceMean"), .. Message(5, axesAttribute), .. Message(5, keepDimsAttribute)];
        byte[] shape = [.. inputShape.SelectMany(size => Message(1, Varint(1, size)))];
        byte[] tensorType = [.. Varint(1, 1), .. Message(2, shape)];
        byte[] input = [.. Text(1, "x"), .. Message(2, Message(1, tensorType))];
        byte[] output = [.. Text(1, "y"), .. Message(2, Message(1, Varint(1, 1)))];
        byte[] graph = [.. Message(1, node), .. Text(2, "g"), .. Message(11, input), .. Message(12, output)];
        return [.. Varint(1, 7), .. Message(7, graph), .. Message(8, Varint(2, 13))];
    }

    private static byte[] Text(int field, string value) => Message(field, System.Text.Encoding.UTF8.GetBytes(value));

    private static byte[] Message(int field, byte[] value) => [.. Key(field, 2), .. Raw(value.Length), .. value];

    private static byte[] Varint(int field, long value) => [.. Key(field, 0), .. Raw(value)];

    private static byte[] Key(int field, int wireType) => Raw((field << 3) | wireType);

    private static byte[] Raw(long value)
    {
        List<byte> bytes = [];

        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }
}
