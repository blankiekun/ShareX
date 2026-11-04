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
using ShareX.PhotoEditor.Core;
using SkiaSharp;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

// These run the real magick command, and pass trivially on a machine without it.
public sealed class PhotoEditorImageIOTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("sharex-imageio-").FullName;
    private static bool HasMagick => FileHelpers.FindExecutableOnPath("magick") != null;

    public void Dispose() => Directory.Delete(folder, true);

    // Catches a stdin and stdout pipe deadlock, a lost alpha channel, and colour channels swapped on the way through PNG.
    [Fact]
    public void TiffRoundTripKeepsPixelsAndAlpha()
    {
        if (!HasMagick) return;
        using SKBitmap source = new(new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        source.Erase(new SKColor(200, 40, 10, 255));
        source.SetPixel(3, 4, new SKColor(0, 0, 0, 0));
        string path = Path.Combine(folder, "round.tiff");

        ImageIO.SaveBitmap(source, path);
        using SKBitmap? decoded = ImageIO.DecodeImage(path);

        Assert.NotNull(decoded);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(32, decoded.Height);
        Assert.Equal(new SKColor(200, 40, 10, 255), decoded.GetPixel(20, 20));
        Assert.Equal(0, decoded.GetPixel(3, 4).Alpha);
    }

    // Catches ImageMagick picking its decoder from the content: MVG draws other files into the result, so a
    // download named .tiff could read files from disk.
    [Fact]
    public void DrawingScriptNamedTiffIsNotRun()
    {
        if (!HasMagick) return;
        string path = Path.Combine(folder, "draw.tiff");
        File.WriteAllText(path, "push graphic-context\nviewbox 0 0 16 16\nfill red\nrectangle 0,0 16,16\npop graphic-context\n");

        Assert.Null(ImageIO.DecodeImage(path));
    }

    // Catches a decode failure escaping as an unhandled exception instead of the message Open shows.
    [Theory]
    [InlineData("junk.heic")]
    [InlineData("junk.avif")]
    [InlineData("junk.tif")]
    public void DamagedFileGivesTheOpenMessage(string name)
    {
        if (!HasMagick) return;
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[2048]);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ImageIO.Open(path));
        Assert.StartsWith("Could not open", exception.Message);
    }
}
