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

using System.Drawing;
using System.Globalization;

namespace ShareX.ScreenRecordingLib.Gsr;

/// <summary>A monitor as KDE reports it, in global desktop coordinates.</summary>
internal sealed record Monitor(string Name, Rectangle Bounds, double Scale);

/// <summary>Turns recording options into the gpu-screen-recorder argument list.</summary>
internal static class GsrCommand
{
    /// <summary>
    /// Returns the even-sized rectangle to capture. A region that doesn't fit one monitor is refused rather than
    /// clipped: gsr gives wrong pixels for it, and clipping would silently drop part of what the user selected.
    /// An odd size rounds up, moving the rectangle back one pixel when the extra pixel would leave the monitor.
    /// </summary>
    public static Rectangle PlanRegion(Rectangle region, IReadOnlyList<Monitor> monitors)
    {
        Monitor? monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(region));
        if (monitor == null)
        {
            string[] touched = monitors.Where(m => m.Bounds.IntersectsWith(region)).Select(m => m.Name).ToArray();
            string where = touched.Length > 1 ? $"spans {string.Join(" and ", touched)}"
                : touched.Length == 1 ? $"extends past the edge of {touched[0]}" : "is outside every monitor";
            throw new NotSupportedException($"The recording region {Describe(region)} {where}. gpu-screen-recorder can only record inside one monitor, so select a region on a single monitor.");
        }
        if (Math.Abs(monitor.Scale - 1) > 0.001)
        {
            throw new NotSupportedException($"{monitor.Name} is scaled to {monitor.Scale.ToString(CultureInfo.InvariantCulture)}. Region recording is only confirmed at scale 1, because the recorder sees physical pixels and the wrong area could be recorded.");
        }

        int width = region.Width + (region.Width & 1);
        int height = region.Height + (region.Height & 1);
        int x = region.X + width > monitor.Bounds.Right ? region.X - 1 : region.X;
        int y = region.Y + height > monitor.Bounds.Bottom ? region.Y - 1 : region.Y;
        if (x < monitor.Bounds.X || y < monitor.Bounds.Y)
        {
            throw new NotSupportedException($"The recording region {Describe(region)} can't be rounded up to an even size inside {monitor.Name}, whose size is {monitor.Bounds.Width}x{monitor.Bounds.Height}.");
        }
        return new Rectangle(x, y, width, height);
    }

    public static List<string> BuildArguments(RecordingOptions options, Rectangle target, string outputPath, string socketPath)
    {
        List<string> args =
        [
            "-w", Describe(target),
            "-c", "mp4",
            "-k", "h264",
            "-f", options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-bm", "cbr",
            "-q", (options.VideoBitrate / 1000).ToString(CultureInfo.InvariantCulture),
            "-cursor", options.IncludeCursor ? "yes" : "no",
            "-write-first-frame-ts", "yes",
            "-ipc", socketPath
        ];
        // The man page warns cfr can collapse to 1-2 fps under heavy GPU load, so only ask for it when it's needed.
        if (options.ConstantFrameRate) args.AddRange(["-fm", "cfr"]);
        if (!options.RequireHardwareEncoder) args.AddRange(["-fallback-cpu-encoding", "yes"]);

        // Separate tracks rather than one merged track: a silent microphone can only be detected on its own track.
        if (options.CaptureSystemAudio) args.AddRange(["-a", AudioSource(options.SystemAudioDeviceId, "default_output")]);
        if (options.CaptureMicrophone) args.AddRange(["-a", AudioSource(options.MicrophoneDeviceId, "default_input")]);
        if (options.HasAudio) args.AddRange(["-ac", "aac", "-ab", (options.AudioBitrate / 1000).ToString(CultureInfo.InvariantCulture)]);

        args.AddRange(["-o", outputPath]);
        return args;
    }

    private static string AudioSource(string? deviceId, string fallback) =>
        string.IsNullOrEmpty(deviceId) ? fallback : "device:" + deviceId;

    private static string Describe(Rectangle r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.Width}x{r.Height}+{r.X}+{r.Y}");
}
