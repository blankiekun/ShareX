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

using ShareX.ScreenRecordingLib.Gsr;
using System.Drawing;
using Xunit;
using GsrMonitor = ShareX.ScreenRecordingLib.Gsr.Monitor;

namespace ShareX.ScreenRecordingLib.Tests;

public class GsrCommandTests
{
    private static readonly GsrMonitor[] TwoMonitors =
    [
        new("DP-1", new Rectangle(0, 0, 2560, 1440), 1),
        new("DP-3", new Rectangle(2560, 0, 2560, 1440), 1)
    ];

    private static List<string> Build(RecordingOptions options) =>
        GsrCommand.BuildArguments(options, new Rectangle(2700, 200, 640, 360), "/out/x.mp4", "/run/x.sock");

    private static RecordingOptions Options() => new() { OutputPath = "/out/x.mp4", Region = new Rectangle(2700, 200, 640, 360), CaptureSystemAudio = false };

    private static string? Value(List<string> arguments, string flag) =>
        arguments.IndexOf(flag) is >= 0 and var index ? arguments[index + 1] : null;

    // Catches: a region gsr can't record reaching it. Spanning regions exit 0 with wrapped, wrong pixels (measured), so
    // nothing later would notice.
    [Theory]
    [InlineData(2400, 100, 400, 300, "spans DP-1 and DP-3")]
    [InlineData(5000, 1300, 400, 300, "extends past the edge of DP-3")]
    [InlineData(9000, 9000, 400, 300, "is outside every monitor")]
    public void PlanRegion_RefusesRegionsThatDontFitOneMonitor(int x, int y, int width, int height, string reason)
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => GsrCommand.PlanRegion(new Rectangle(x, y, width, height), TwoMonitors));
        Assert.Contains(reason, error.Message);
    }

    // Catches: recording the wrong area on a scaled monitor, because gsr sees physical pixels and KDE reports logical ones.
    [Fact]
    public void PlanRegion_RefusesScaledMonitor()
    {
        GsrMonitor[] scaled = [new("DP-1", new Rectangle(0, 0, 2560, 1440), 1.5)];
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => GsrCommand.PlanRegion(new Rectangle(0, 0, 100, 100), scaled));
        Assert.Contains("scaled", error.Message);
    }

    // Catches: gsr rounding an odd size up by itself (measured), which makes CapturedRegion wrong, and rounding up past the
    // monitor's edge, which turns a valid region into a spanning one. The last case checks the edge of a monitor not at 0,0.
    [Theory]
    [InlineData(100, 100, 801, 601, 100, 100, 802, 602)]
    [InlineData(1759, 839, 801, 601, 1758, 838, 802, 602)]
    [InlineData(4319, 0, 801, 2, 4318, 0, 802, 2)]
    [InlineData(2560, 0, 2560, 1440, 2560, 0, 2560, 1440)]
    public void PlanRegion_RoundsOddSizesUpInsideTheMonitor(int x, int y, int width, int height, int plannedX, int plannedY, int plannedWidth, int plannedHeight)
    {
        Rectangle planned = GsrCommand.PlanRegion(new Rectangle(x, y, width, height), TwoMonitors);
        Assert.Equal(new Rectangle(plannedX, plannedY, plannedWidth, plannedHeight), planned);
    }

    // Catches: moving back one pixel off the monitor when the whole odd-sized monitor was selected, which would ask gsr for x = -1.
    [Fact]
    public void PlanRegion_RefusesOddRegionWithNoRoomToGrow()
    {
        GsrMonitor[] odd = [new("DP-1", new Rectangle(0, 0, 1921, 1080), 1)];
        Assert.Throws<NotSupportedException>(() => GsrCommand.PlanRegion(new Rectangle(0, 0, 1921, 100), odd));
    }

    // Catches: losing a flag the session depends on. Without -ipc or -write-first-frame-ts it never sees a first frame, and
    // the deprecated -region form or a missing -c mp4 changes what gsr produces.
    [Fact]
    public void BuildArguments_NamesTheRegionOutputAndSignals()
    {
        List<string> arguments = Build(Options());
        Assert.Equal("640x360+2700+200", Value(arguments, "-w"));
        Assert.DoesNotContain("-region", arguments);
        Assert.Equal("mp4", Value(arguments, "-c"));
        Assert.Equal("h264", Value(arguments, "-k"));
        Assert.Equal("/out/x.mp4", Value(arguments, "-o"));
        Assert.Equal("/run/x.sock", Value(arguments, "-ipc"));
        Assert.Equal("yes", Value(arguments, "-write-first-frame-ts"));
    }

    // Catches: cfr being always on (the man page warns it can collapse to 1-2 fps under GPU load) or dropped when asked for.
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "cfr")]
    public void BuildArguments_AsksForConstantFrameRateOnlyWhenSet(bool constant, string? expected)
    {
        Assert.Equal(expected, Value(Build(Options() with { ConstantFrameRate = constant }), "-fm"));
    }

    // Catches: a recording falling back to the CPU when the user required the hardware encoder.
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "yes")]
    public void BuildArguments_AllowsCpuFallbackOnlyWhenHardwareIsNotRequired(bool required, string? expected)
    {
        Assert.Equal(expected, Value(Build(Options() with { RequireHardwareEncoder = required }), "-fallback-cpu-encoding"));
    }

    // Catches: merging the tracks with "a|b", which hides a silent microphone, and swapping
    // their order, which makes the empty-microphone check read the system audio track.
    [Fact]
    public void BuildArguments_KeepsAudioOnSeparateTracks_SystemFirst()
    {
        List<string> arguments = Build(Options() with { CaptureSystemAudio = true, CaptureMicrophone = true });
        int first = arguments.IndexOf("-a");
        Assert.Equal(["-a", "default_output", "-a", "default_input"], arguments.Skip(first).Take(4));
    }

    // Catches: a chosen device being ignored or passed without its device: prefix, which gsr rejects with exit 50. A device
    // name must stay one argument even with a space in it.
    [Fact]
    public void BuildArguments_UsesChosenDevicesOverDefaults()
    {
        List<string> arguments = Build(Options() with { CaptureSystemAudio = true, CaptureMicrophone = true, SystemAudioDeviceId = "sink.monitor", MicrophoneDeviceId = "mic with space" });
        Assert.Contains("device:sink.monitor", arguments);
        Assert.Contains("device:mic with space", arguments);
        Assert.DoesNotContain("default_output", arguments);
        Assert.DoesNotContain("default_input", arguments);
    }

    // Catches: audio flags with no audio track, and losing -ac aac, because gsr's default is Opus in the MP4 (measured).
    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "aac")]
    [InlineData(false, true, "aac")]
    public void BuildArguments_SetsAacOnlyWithAudio(bool system, bool microphone, string? expected)
    {
        List<string> arguments = Build(Options() with { CaptureSystemAudio = system, CaptureMicrophone = microphone });
        Assert.Equal(expected, Value(arguments, "-ac"));
        Assert.Equal(system || microphone, arguments.Contains("-a"));
    }

    // Catches: passing ShareX's bits per second where gsr expects kilobits, which asks for a bitrate a thousand times too high.
    [Fact]
    public void BuildArguments_ConvertsBitratesToKilobits()
    {
        List<string> arguments = Build(Options() with { VideoBitrate = 5_000_000, AudioBitrate = 128_000, CaptureSystemAudio = true });
        Assert.Equal("cbr", Value(arguments, "-bm"));
        Assert.Equal("5000", Value(arguments, "-q"));
        Assert.Equal("128", Value(arguments, "-ab"));
    }
}
