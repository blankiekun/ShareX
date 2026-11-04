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

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>
/// Drives the public ScreenRecorder against a fake gpu-screen-recorder. Losing a recording with no error is the failure that
/// matters most here. The session layer ends at ScreenRecorder: ScreenRecordManager.FinishRecording, which saves the file when
/// the app quits, is in the ShareX app project behind windows, and isn't covered.
/// </summary>
public sealed class RecordingLifecycleTests : IDisposable
{
    // Only a limit for a hang coming back. Nothing here waits this long when it passes.
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private readonly FakeHost host = new();

    public void Dispose() => host.Dispose();

    private sealed record Outcome(RecordingResult? Result, Exception? Error, List<string> Diagnostics, Rectangle CapturedRegion);

    /// <summary>Records once and returns after the recorder is disposed, so its cleanup has finished. By default it stops as soon as recording starts.</summary>
    private static async Task<Outcome> Record(RecordingOptions options, Action<ScreenRecorder>? whileRecording = null, bool stop = true)
    {
        List<string> diagnostics = [];
        RecordingResult? result = null;
        Exception? error = null;
        Rectangle captured;
        await using (ScreenRecorder recorder = new(options))
        {
            recorder.Diagnostic += diagnostics.Add;
            try
            {
                await recorder.StartAsync().WaitAsync(Limit);
                whileRecording?.Invoke(recorder);
                if (stop) recorder.RequestStop();
                result = await recorder.Completion.WaitAsync(Limit);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            captured = recorder.CapturedRegion;
        }
        return new Outcome(result, error, diagnostics, captured);
    }

    // Catches: a stop that reports completion without a finished file at the path it reports, or leaves the temporary file,
    // first-frame marker or socket behind, or doesn't really stop the recorder. This is the Stop route of the lost-MP4 report.
    [Fact]
    public async Task Stop_LeavesTheFinishedFileAtTheExpectedPath()
    {
        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Error);
        Assert.Equal(host.OutputPath, outcome.Result!.OutputPath);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        Assert.Equal([host.OutputPath], Directory.GetFileSystemEntries(host.Work));
        Assert.Empty(Directory.GetFileSystemEntries(host.Runtime));
        Assert.Equal(30, outcome.Result.VideoFrames);
        Assert.Equal("h264_nvenc", outcome.Result.Encoder.Name);
        Assert.Contains(host.Events("ipc"), request => request.GetProperty("name").GetString() == "stop");
        host.AssertRecorderGone();
    }

    // Catches: gsr writing straight to the final name. It silently overwrites (measured), so a file that appears at that name
    // while recording would be destroyed. Keeping the same folder is what makes the final move atomic.
    [Fact]
    public async Task Recorder_WritesToATemporaryNameInTheSameFolder()
    {
        await Record(host.Options());

        string written = host.RecorderArgument("-o");
        Assert.NotEqual(host.OutputPath, written);
        Assert.Equal(host.Work, Path.GetDirectoryName(written));
    }

    // Catches: the recording being lost when the IPC socket dies at stop. SIGINT must still save it.
    [Fact]
    public async Task Stop_FallsBackToSigintWhenTheSocketIsLost()
    {
        host.SetMode("ipc_close");

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        Assert.Equal(2, host.Events("signal").Single().GetProperty("num").GetInt32());
        host.AssertRecorderGone();
    }

    // Catches: throwing away a good file because the recorder ended by itself with exit 0 before anyone asked it to stop.
    [Fact]
    public async Task RecorderThatFinishesByItself_StillDeliversTheFile()
    {
        host.SetMode("self_exit");

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
    }

    // Catches: Duration never stopping the recorder, so a timed recording runs on with no one watching.
    [Fact]
    public async Task Duration_StopsTheRecordingAndDeliversTheFile()
    {
        Outcome outcome = await Record(host.Options() with { Duration = TimeSpan.FromMilliseconds(50) }, stop: false);

        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        host.AssertRecorderGone();
    }

    // Catches: an exit code being swallowed, or the error losing gsr's own reason. Exit 3 stands for any code not listed.
    [Theory]
    [InlineData(1, typeof(ArgumentException), "rejected its options")]
    [InlineData(2, typeof(NotSupportedException), "Wayland")]
    [InlineData(3, typeof(IOException), "exit 3")]
    [InlineData(50, typeof(IOException), "audio device")]
    [InlineData(51, typeof(IOException), "region or monitor")]
    [InlineData(126, typeof(UnauthorizedAccessException), "setcap")]
    public async Task RecorderThatExitsAtStartup_SurfacesWhy(int exitCode, Type errorType, string reason)
    {
        host.SetMode($"exit{exitCode}");

        Outcome outcome = await Record(host.Options());

        Assert.IsType(errorType, outcome.Error);
        Assert.Contains(reason, outcome.Error!.Message);
        Assert.Contains("fake reason", outcome.Error.Message);
        host.AssertNothingLeft();
    }

    // Catches: a crash mid-recording going unnoticed until the user presses stop, or Completion reporting success with no file.
    [Fact]
    public async Task RecorderThatCrashes_SurfacesAnErrorWithoutBeingStopped()
    {
        host.SetMode("die_mid");

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        Assert.Null(outcome.Result);
        Assert.Contains("exit 139", outcome.Error?.Message);
        host.AssertNothingLeft();
    }

    // Catches: treating a killed recorder's stub of a file as a recording. SIGKILL left an unplayable 88-byte MP4 (measured).
    [Fact]
    public async Task RecorderThatIsKilled_SurfacesTheLossAndKeepsNoStub()
    {
        host.SetMode("killed_mid");

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        Assert.Null(outcome.Result);
        Assert.Contains("SIGKILL", outcome.Error?.Message);
        host.AssertNothingLeft();
    }

    // Catches: the silent loss itself. The recorder says everything went fine but saved nothing, and the empty reservation
    // would otherwise look like a recording.
    [Fact]
    public async Task RecorderThatExitsCleanlyWithoutAFile_SurfacesAnError()
    {
        host.SetMode("no_file");

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Result);
        Assert.Contains("wrote no video", outcome.Error?.Message);
        host.AssertNothingLeft();
    }

    // Catches: exit 0 being trusted when the stop request itself was answered with a failure to save.
    [Fact]
    public async Task StopAnsweredWithAnError_SurfacesThatError()
    {
        host.SetMode("ipc_stop_error");

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Result);
        Assert.Contains("failed to save", outcome.Error?.Message);
        host.AssertNothingLeft();
    }

    // Catches: delivering an all-black video. With an uncapped helper gsr exits 0 with a valid black MP4 (measured), so only
    // these stderr lines give it away, and they can come before or after recording starts.
    [Theory]
    [InlineData("black_start")]
    [InlineData("black_late")]
    public async Task BlackVideoMarkers_SurfaceAnErrorAndKeepNoFile(string mode)
    {
        host.SetMode(mode);

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        Assert.Null(outcome.Result);
        Assert.Contains("black", outcome.Error?.Message);
        host.AssertNothingLeft();
    }

    // Catches: an unusable file replacing the reservation and being reported as a finished recording.
    [Theory]
    [InlineData(null, "moov atom not found", "moov atom not found")]
    [InlineData("""{"streams":[{"codec_type":"video","nb_frames":"0"}],"format":{}}""", null, "no video frames")]
    [InlineData("""{"streams":[{"codec_type":"audio"}],"format":{}}""", null, "no video stream")]
    public async Task OutputThatFailsTheProbe_SurfacesAnErrorInsteadOfAFile(string? json, string? probeError, string reason)
    {
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", json);
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_ERROR", probeError);

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Result);
        Assert.Contains(reason, outcome.Error?.Message);
        Assert.False(File.Exists(host.OutputPath));
    }

    // Catches: losing a good recording only because ffprobe, an optional check, isn't installed.
    [Fact]
    public async Task MissingProbe_StillDeliversTheFileAndSaysSo()
    {
        host.RemoveTool("ffprobe");

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        Assert.Equal(-1, outcome.Result!.VideoFrames);
        Assert.Contains(outcome.Diagnostics, message => message.Contains("ffprobe"));
    }

    // Catches: abort keeping a partial file, reporting a finished recording, or leaving the recorder running.
    [Fact]
    public async Task Abort_RemovesThePartialFileAndStopsTheRecorder()
    {
        Outcome outcome = await Record(host.Options(), recorder => recorder.RequestAbort(), stop: false);

        Assert.Null(outcome.Result);
        Assert.IsAssignableFrom<OperationCanceledException>(outcome.Error);
        host.AssertNothingLeft();
        host.AssertRecorderGone();
    }

    // Catches: aborting while gsr is still starting leaving it running. It shows no indicator, so nobody would notice.
    [Fact]
    public async Task AbortBeforeTheFirstFrame_StopsTheRecorder()
    {
        host.SetMode("no_first_frame");
        await using (ScreenRecorder recorder = new(host.Options()))
        {
            Task start = recorder.StartAsync();
            using CancellationTokenSource limit = new(Limit);
            while (host.Events("start").Count == 0) await Task.Delay(5, limit.Token);

            recorder.RequestAbort();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Limit));
        }
        host.AssertNothingLeft();
        host.AssertRecorderGone();
    }

    // Catches: stopping during the countdown starting a recorder anyway, or leaving the empty reservation at the output path.
    [Fact]
    public async Task StopDuringTheCountdown_NeverStartsTheRecorder()
    {
        await using (ScreenRecorder recorder = new(host.Options()))
        {
            await recorder.PrepareAsync().WaitAsync(Limit);
            Assert.True(File.Exists(host.OutputPath));
            host.AssertRecorderNeverStarted();

            recorder.RequestStop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.Completion.WaitAsync(Limit));
        }
        host.AssertRecorderNeverStarted();
        host.AssertNothingLeft();
    }

    // Catches: gsr hanging at startup making the app wait forever or report a start. Takes the session's full 10 second limit.
    [Fact]
    public async Task RecorderThatNeverDeliversAFrame_TimesOutAndIsStopped()
    {
        host.SetMode("no_first_frame");

        Outcome outcome = await Record(host.Options());

        Assert.Null(outcome.Result);
        Assert.Contains("didn't deliver a frame", outcome.Error?.Message);
        host.AssertNothingLeft();
        host.AssertRecorderGone();
    }

    // Catches: overwriting or deleting the user's existing file, which is the reason the name is reserved first.
    [Fact]
    public async Task ExistingFile_IsRefusedAndLeftAlone()
    {
        File.WriteAllText(host.OutputPath, "the user's file");

        Outcome outcome = await Record(host.Options());

        Assert.IsType<IOException>(outcome.Error);
        Assert.Equal("the user's file", File.ReadAllText(host.OutputPath));
        host.AssertRecorderNeverStarted();
    }

    // Catches: losing either file when something else takes the reserved name during a recording. The finished recording must
    // stay on disk, and the error must say where.
    [Fact]
    public async Task NameTakenWhileRecording_KeepsBothFilesAndSaysWhere()
    {
        Outcome outcome = await Record(host.Options(), _ => File.WriteAllText(host.OutputPath, "someone else's file"));

        Assert.Contains("available at", outcome.Error?.Message);
        Assert.Equal("someone else's file", File.ReadAllText(host.OutputPath));
        string kept = Assert.Single(Directory.GetFiles(host.Work, ".out-*.mp4"));
        Assert.Contains(kept, outcome.Error!.Message);
        Assert.Equal(FakeHost.Video, File.ReadAllText(kept));
    }

    // Catches: the region check being bypassed on the way to the recorder, so a spanning region silently gives wrong pixels.
    [Fact]
    public async Task SpanningRegion_NeverReachesTheRecorder()
    {
        Outcome outcome = await Record(host.Options() with { Region = new Rectangle(2400, 100, 400, 300) });

        Assert.IsType<NotSupportedException>(outcome.Error);
        host.AssertRecorderNeverStarted();
        host.AssertNothingLeft();
    }

    // Catches: the recorder and CapturedRegion disagreeing about what was recorded, which shifts a later GIF crop.
    [Fact]
    public async Task OddRegion_IsRecordedAsPlannedAndReported()
    {
        Outcome outcome = await Record(host.Options() with { Region = new Rectangle(100, 100, 801, 601) });

        Assert.Equal("802x602+100+100", host.RecorderArgument("-w"));
        Assert.Equal(new Rectangle(100, 100, 802, 602), outcome.CapturedRegion);
    }

    // Catches: a recorder that outlives a crashed ShareX and keeps recording the screen with no indicator (fixed by the
    // setpriv guard), and two spawn mistakes: stdout going to a regular file corrupts the video (measured), and inheriting the
    // AppImage's library path breaks a host binary.
    [Fact]
    public async Task Recorder_IsGuardedAgainstParentDeathAndGetsACleanSpawn()
    {
        const int SIGINT = 2;
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", "/appimage/lib");

        await Record(host.Options());

        System.Text.Json.JsonElement start = host.Events("start").Single();
        Assert.Equal(SIGINT, start.GetProperty("pdeathsig").GetInt32());
        Assert.True(start.GetProperty("stdout_fifo").GetBoolean());
        Assert.True(start.GetProperty("stderr_fifo").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, start.GetProperty("ld_library_path").ValueKind);
    }

    // Catches: starting the recorder without the guard when setpriv is missing, instead of refusing.
    [Fact]
    public async Task MissingGuard_RefusesToStartTheRecorder()
    {
        host.RemoveTool("setpriv");

        Outcome outcome = await Record(host.Options());

        Assert.IsType<FileNotFoundException>(outcome.Error);
        Assert.Contains("setpriv", outcome.Error!.Message);
        host.AssertRecorderNeverStarted();
        host.AssertNothingLeft();
    }

    // Catches: starting gsr with an uncapped helper, which gives a root prompt or a silently black video (measured). Another
    // capability, or one that's permitted but not effective, doesn't count.
    [Theory]
    [InlineData("none")]
    [InlineData("cap_net_admin=ep")]
    [InlineData("cap_sys_admin=p")]
    public async Task HelperWithoutTheCapability_IsRefusedUpFront(string capability)
    {
        Environment.SetEnvironmentVariable("FAKE_GETCAP", capability);

        Outcome outcome = await Record(host.Options());

        Assert.IsType<UnauthorizedAccessException>(outcome.Error);
        Assert.Contains("setcap", outcome.Error!.Message);
        host.AssertRecorderNeverStarted();
        host.AssertNothingLeft();
    }

    // Catches: a missing recorder failing as anything but a clear install message. PATH holds only the fakes, so this can't
    // find a real one.
    [Fact]
    public async Task MissingRecorder_SaysToInstallIt()
    {
        host.RemoveTool("gpu-screen-recorder");

        Outcome outcome = await Record(host.Options());

        Assert.IsType<FileNotFoundException>(outcome.Error);
        Assert.Contains("isn't installed", outcome.Error!.Message);
        host.AssertNothingLeft();
    }

    // Catches: a turned-off monitor counting as a place to record, and a rotated monitor keeping its unrotated size, which
    // would refuse valid regions on it and accept invalid ones.
    [Fact]
    public void Monitors_SkipDisabledOnesAndSwapRotatedSizes()
    {
        IReadOnlyList<ShareX.ScreenRecordingLib.Gsr.Monitor> monitors = HostTools.GetMonitors();

        Assert.Equal(["DP-1", "DP-3", "DP-ROT"], monitors.Select(monitor => monitor.Name));
        Assert.Equal(new Rectangle(0, 2000, 1080, 1920), monitors.Single(monitor => monitor.Name == "DP-ROT").Bounds);
    }

    // Catches: a silent or missing microphone going unmentioned (one real default mic delivered about 6 bytes per packet),
    // reading the wrong track, and false alarms on real audio. Tracks are system first, then microphone.
    [Theory]
    [InlineData(true, new[] { 128000, 1600 }, "looks empty")]
    [InlineData(false, new[] { 1600 }, "looks empty")]
    [InlineData(true, new[] { 128000 }, "no microphone track")]
    [InlineData(true, new[] { 1600, 128000 }, null)]
    public async Task MicrophoneTrack_IsCheckedOnTheRightTrack(bool systemAudio, int[] trackBitRates, string? warning)
    {
        string tracks = string.Concat(trackBitRates.Select(bitRate => $$""",{"codec_type":"audio","nb_frames":"47","duration":"1.000000","bit_rate":"{{bitRate}}"}"""));
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", $$$"""{"streams":[{"codec_type":"video","nb_frames":"30"}{{{tracks}}}],"format":{"duration":"1.000000"}}""");

        Outcome outcome = await Record(host.Options() with { CaptureSystemAudio = systemAudio, CaptureMicrophone = true });

        Assert.Null(outcome.Error);
        if (warning == null) Assert.Empty(outcome.Diagnostics);
        else Assert.Contains(outcome.Diagnostics, message => message.Contains(warning));
    }
}
