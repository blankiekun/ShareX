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
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public sealed class VideoConverterGifTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-converter-test").FullName;
    private readonly List<(VideoConversionRequest Request, IProgress<double> Progress, bool PaletteExisted)> _runs = [];

    public void Dispose() => Directory.Delete(_folder, true);

    private VideoConverterViewModel ViewModel(ConverterVideoCodecs codec, params Func<VideoConversionResult>[] results)
    {
        string input = Path.Combine(_folder, "input.mp4");
        File.WriteAllBytes(input, []);

        VideoConverterOptions options = new()
        {
            InputFilePath = input,
            OutputFolderPath = _folder,
            OutputFileName = "output",
            VideoCodec = codec,
            AutoOpenFolder = true
        };

        return new VideoConverterViewModel(options, (request, progress, _) =>
        {
            // Like ffmpeg, the first run leaves the palette at the path it was given.
            if (_runs.Count == 0 && request.Arguments.Contains("palettegen"))
            {
                File.WriteAllBytes(request.OutputFilePath, [1]);
            }

            bool paletteExisted = _runs.Count > 0 && File.Exists(_runs[0].Request.OutputFilePath);
            _runs.Add((request, progress, paletteExisted));
            return Task.FromResult(results[_runs.Count - 1]());
        });
    }

    private static Func<VideoConversionResult> Returns(bool succeeded, bool cancelled = false, string? error = null) =>
        () => new VideoConversionResult(succeeded, cancelled, error);

    // Catches: palettegen and paletteuse back in one command, which holds every frame in memory until the input ends and
    // got the session OOM-killed. Also the second run missing the palette, the folder opening after the palette run, the
    // progress bar reaching 100% on the palette run and starting again, and the temp palette being left behind.
    [Fact]
    public async Task Gif_MakesThePaletteInItsOwnRun_ThenUsesItAndRemovesIt()
    {
        VideoConverterViewModel viewModel = ViewModel(ConverterVideoCodecs.gif, Returns(true), Returns(true));

        await viewModel.StartEncodingCommand.ExecuteAsync(null);

        Assert.Equal(2, _runs.Count);
        (VideoConversionRequest palette, IProgress<double> paletteProgress, _) = _runs[0];
        (VideoConversionRequest gif, IProgress<double> gifProgress, bool paletteExisted) = _runs[1];

        Assert.Contains("palettegen=stats_mode=full", palette.Arguments);
        Assert.DoesNotContain("paletteuse", palette.Arguments);
        Assert.False(palette.AutoOpenFolder);

        Assert.Contains("paletteuse=dither=sierra2_4a", gif.Arguments);
        Assert.DoesNotContain("palettegen", gif.Arguments);
        Assert.Contains($"-i \"{palette.OutputFilePath}\"", gif.Arguments);
        Assert.EndsWith($"-y \"{viewModel.OutputFilePath}\"", gif.Arguments);
        Assert.True(gif.AutoOpenFolder);
        Assert.True(paletteExisted);

        Assert.NotSame(paletteProgress, gifProgress);
        Assert.False(File.Exists(palette.OutputFilePath));
        Assert.Equal(100, viewModel.Progress);
    }

    // Catches: the second run starting without a palette, or after the user pressed Stop (ffmpeg stopped with q exits 0,
    // so a stopped palette run looks like a success), and the failed run's temp palette being left behind.
    [Theory]
    [InlineData(false, false, "Invalid data found")]
    [InlineData(true, true, null)]
    [InlineData(false, true, null)]
    public async Task Gif_FailedOrStoppedPaletteRun_NeverStartsTheSecondRun(bool succeeded, bool cancelled, string? error)
    {
        VideoConverterViewModel viewModel = ViewModel(ConverterVideoCodecs.gif, Returns(succeeded, cancelled, error));

        await viewModel.StartEncodingCommand.ExecuteAsync(null);

        Assert.Single(_runs);
        Assert.False(File.Exists(_runs[0].Request.OutputFilePath));

        if (error != null)
        {
            Assert.Contains(error, viewModel.StatusText);
        }
    }

    // Catches: the temp palette surviving a failure or a stop in the second run. The handler throws when Stop lands between
    // the runs, so the cleanup can't depend on a result coming back.
    public static TheoryData<Func<VideoConversionResult>> SecondRunEndings => new()
    {
        Returns(false, false, "Conversion failed!"),
        Returns(true, true),
        () => throw new OperationCanceledException(),
        () => throw new InvalidOperationException("ffmpeg could not start")
    };

    [Theory]
    [MemberData(nameof(SecondRunEndings))]
    public async Task Gif_SecondRunThatDoesNotFinish_StillRemovesThePalette(Func<VideoConversionResult> ending)
    {
        VideoConverterViewModel viewModel = ViewModel(ConverterVideoCodecs.gif, Returns(true), ending);

        await viewModel.StartEncodingCommand.ExecuteAsync(null);

        Assert.Equal(2, _runs.Count);
        Assert.False(File.Exists(_runs[0].Request.OutputFilePath));
        Assert.Equal(0, viewModel.Progress);
    }

    // Catches: the palette branch swallowing other codecs, which have no palette and take a single run.
    [Fact]
    public async Task OtherCodecs_StayOneRun()
    {
        VideoConverterViewModel viewModel = ViewModel(ConverterVideoCodecs.x264, Returns(true));

        await viewModel.StartEncodingCommand.ExecuteAsync(null);

        VideoConversionRequest request = Assert.Single(_runs).Request;
        Assert.DoesNotContain("palette", request.Arguments);
        Assert.Equal(viewModel.OutputFilePath, request.OutputFilePath);
    }
}
