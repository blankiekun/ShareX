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
using System.Diagnostics;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>
/// The real ffmpeg by its full path, because FakeHost changes PATH for the whole process while other test classes run,
/// and small fakes where the timing has to be under the test's control.
/// </summary>
public sealed class VideoThumbnailHelperTests : IDisposable
{
    private const string RealFFmpeg = "/usr/bin/ffmpeg";
    private static readonly TimeSpan NoLimit = TimeSpan.FromSeconds(30);

    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-video-thumbnail-").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private string Clip(string name, double seconds)
    {
        string path = Path.Combine(_folder, name);
        using Process process = Process.Start(new ProcessStartInfo(RealFFmpeg)
        {
            ArgumentList =
            {
                "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=10:duration={seconds}",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path
            }
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    /// <summary>
    /// The checkout has CRLF in this file, and a shell can't run a script with CRLF line endings. FakeHost swaps the process-wide PATH while other
    /// test classes run, and a fake inherits it: with no PATH of its own it can't find grep or sleep, so it ends in milliseconds with exit code 0.
    /// </summary>
    private string FakeFFmpeg(string script)
    {
        string path = Path.Combine(_folder, "fake-ffmpeg");
        File.WriteAllText(path, "#!/bin/sh\nPATH=/usr/bin:/bin\n" + script.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string HangingFFmpeg(string pidFile) => FakeFFmpeg($"""
        echo $$ >> "{pidFile}"
        exec sleep 60
        """);

    private static void AssertAllGone(string pidFile)
    {
        string[] pids = File.ReadAllLines(pidFile);
        Assert.NotEmpty(pids);
        Assert.All(pids, pid => Assert.False(Directory.Exists("/proc/" + pid), $"ffmpeg {pid} is still running"));
    }

    private static async Task WaitForFile(string path)
    {
        for (int i = 0; i < 200 && !File.Exists(path); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(path));
    }

    // Bug: the file name is built into one argument string, so a quote or a $() in it breaks the command or runs something. Also a frame
    // larger than asked for, or in a form the image decoder can't read.
    [Fact]
    public async Task A_frame_fits_the_size_asked_for_whatever_the_file_is_called()
    {
        string clip = Clip("it's \"a\" $(touch pwned) -x.mp4", 2);

        byte[]? frame = await VideoThumbnailHelper.ReadFrameAsync(RealFFmpeg, clip, 96, 96, NoLimit, CancellationToken.None);

        using SKBitmap bitmap = SKBitmap.Decode(frame);
        Assert.Equal(96, bitmap.Width);
        Assert.Equal(54, bitmap.Height);
        Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "pwned")));
    }

    // Bug: a recording shorter than the offset seeks past its end, ffmpeg succeeds with no output, and a good file shows the placeholder.
    [Fact]
    public async Task A_clip_shorter_than_the_offset_still_gives_a_frame()
    {
        string clip = Clip("short.mp4", 0.3);

        Assert.NotNull(await VideoThumbnailHelper.ReadFrameAsync(RealFFmpeg, clip, 96, 72, NoLimit, CancellationToken.None));
    }

    // Bug: a damaged file throws out of the helper and takes the window with it, or stderr left unread fills its pipe and hangs until the limit.
    [Fact]
    public async Task A_damaged_file_gives_no_frame_and_no_exception()
    {
        string garbage = Path.Combine(_folder, "garbage.mp4");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray());
        string empty = Path.Combine(_folder, "empty.mp4");
        File.WriteAllBytes(empty, []);
        string truncated = Path.Combine(_folder, "truncated.mp4");
        byte[] whole = File.ReadAllBytes(Clip("whole.mp4", 2));
        File.WriteAllBytes(truncated, whole[..(whole.Length / 2)]);

        foreach (string file in new[] { garbage, empty, truncated, Path.Combine(_folder, "missing.mp4") })
        {
            Assert.Null(await VideoThumbnailHelper.ReadFrameAsync(RealFFmpeg, file, 96, 72, NoLimit, CancellationToken.None));
        }
    }

    // Bug: PATH without ffmpeg, or one that can't start, surfaces as an exception instead of the placeholder.
    [Fact]
    public async Task No_ffmpeg_gives_no_frame_and_no_exception()
    {
        string clip = Path.Combine(_folder, "x.mp4");

        Assert.Null(await VideoThumbnailHelper.ReadFrameAsync(null, clip, 96, 72, NoLimit, CancellationToken.None));
        Assert.Null(await VideoThumbnailHelper.ReadFrameAsync(Path.Combine(_folder, "no-such-ffmpeg"), clip, 96, 72, NoLimit, CancellationToken.None));
    }

    // Bug: WaitForExit with a timeout, as VideoThumbnailer does, leaves the process running and its slot taken, so two bad files stop every thumbnail.
    [Fact]
    public async Task A_hung_ffmpeg_is_killed_at_the_limit()
    {
        string pids = Path.Combine(_folder, "pids");
        string ffmpeg = HangingFFmpeg(pids);

        Stopwatch elapsed = Stopwatch.StartNew();
        Assert.Null(await VideoThumbnailHelper.ReadFrameAsync(ffmpeg, "x.mp4", 96, 72, TimeSpan.FromMilliseconds(300), CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"took {elapsed.Elapsed}");
        AssertAllGone(pids);
    }

    // Bug: closing a window or removing a task leaves its ffmpeg running to the limit, holding a slot other items are waiting for.
    [Fact]
    public async Task Cancelling_kills_a_running_ffmpeg()
    {
        string pids = Path.Combine(_folder, "pids");
        string ffmpeg = HangingFFmpeg(pids);
        using CancellationTokenSource cancellation = new();

        Task<byte[]?> read = VideoThumbnailHelper.ReadFrameAsync(ffmpeg, "x.mp4", 96, 72, NoLimit, cancellation.Token);
        await WaitForFile(pids);
        cancellation.Cancel();

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertAllGone(pids);
    }

    // Bug: a long list of videos starts dozens of ffmpeg processes at once. Also a queued item that has left the view still starting one when a slot frees up.
    [Fact]
    public async Task At_most_two_run_at_once_and_a_cancelled_one_never_starts()
    {
        string log = Path.Combine(_folder, "log");
        string ffmpeg = FakeFFmpeg($"""
            echo + >> "{log}"
            # Wait for a second one, so a slow machine can't pass them off one at a time and hide what the limit allows.
            i=0
            while [ "$(grep -c + "{log}")" -lt 2 ] && [ $i -lt 50 ]; do sleep 0.1; i=$((i+1)); done
            sleep 0.2
            echo - >> "{log}"
            """);
        using CancellationTokenSource gone = new();

        List<Task<byte[]?>> reads = Enumerable.Range(0, 6)
            .Select(_ => VideoThumbnailHelper.ReadFrameAsync(ffmpeg, "x.mp4", 96, 72, NoLimit, CancellationToken.None)).ToList();
        Task<byte[]?> queuedThenGone = VideoThumbnailHelper.ReadFrameAsync(ffmpeg, "x.mp4", 96, 72, NoLimit, gone.Token);
        gone.Cancel();

        await Task.WhenAll(reads.Append(queuedThenGone));

        string[] lines = File.ReadAllLines(log);
        int running = 0, peak = 0;

        foreach (string line in lines)
        {
            running += line == "+" ? 1 : -1;
            peak = Math.Max(peak, running);
        }

        Assert.Equal(2, peak);
        // Each read that gets no frame at the offset tries once more at the start of the file, and the cancelled one starts nothing.
        Assert.Equal(12, lines.Count(line => line == "+"));
    }
}
