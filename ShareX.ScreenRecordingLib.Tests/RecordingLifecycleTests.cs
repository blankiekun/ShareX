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
using System.Text.Json;
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

    /// <summary>The failure was reported, with where the file is, and the file is at the output path with nothing else left around it.</summary>
    private void AssertKeptAtTheOutputPath(Outcome outcome, string reason, string content = FakeHost.Video)
    {
        Assert.Null(outcome.Result);
        RecordingKeptException kept = Assert.IsType<RecordingKeptException>(outcome.Error);
        Assert.Contains(reason, kept.Message);
        Assert.Contains(host.OutputPath, kept.Message);
        Assert.Equal(host.OutputPath, kept.KeptPath);
        Assert.Equal(content, File.ReadAllText(host.OutputPath));
        Assert.Equal([host.OutputPath], Directory.GetFileSystemEntries(host.Work));
        Assert.Empty(Directory.GetFileSystemEntries(host.Runtime));
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

    // Catches: deleting what a killed recorder left. The 88-byte MP4 from a real SIGKILL (measured) may be useless, but only the user
    // can decide that, and an error that doesn't say SIGKILL gives them no reason why.
    [Fact]
    public async Task RecorderThatIsKilled_KeepsWhatItLeftAndSaysWhere()
    {
        host.SetMode("killed_mid");

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        AssertKeptAtTheOutputPath(outcome, "SIGKILL", new string('\0', 88));
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

    // Catches: keeping a file with nothing in it as if it were a recording, or leaving it under its hidden name. Zero bytes is the
    // one failed output with nothing to keep, so it must not produce a "kept at" error either.
    [Fact]
    public async Task RecorderThatWritesAZeroByteFile_KeepsNothing()
    {
        host.SetMode("empty_file");

        Outcome outcome = await Record(host.Options());

        Assert.IsNotType<RecordingKeptException>(outcome.Error);
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

    // Catches: reporting an all-black video as a finished recording, or deleting it. With an uncapped helper gsr exits 0 with a valid
    // black MP4 (measured), so only these stderr lines give it away, and they can come before or after recording starts. The file
    // stays, because a false alarm from these lines would otherwise lose a real recording.
    [Theory]
    [InlineData("black_start")]
    [InlineData("black_late")]
    public async Task BlackVideoMarkers_SurfaceAnErrorAndKeepTheFile(string mode)
    {
        host.SetMode(mode);

        Outcome outcome = await Record(host.Options(), _ => host.Trigger(), stop: false);

        AssertKeptAtTheOutputPath(outcome, "black");
    }

    // Catches: a rejected recording being reported as finished, or being deleted. ffprobe also exits 1 for reasons of its own, which
    // look just like a rejected file, so a good recording can land here.
    [Theory]
    [InlineData(null, "moov atom not found", "moov atom not found")]
    [InlineData("""{"streams":[{"codec_type":"video","nb_frames":"0"}],"format":{}}""", null, "no video frames")]
    [InlineData("""{"streams":[{"codec_type":"audio"}],"format":{}}""", null, "no video stream")]
    public async Task OutputThatFailsTheProbe_SurvivesAtTheOutputPathWithAnError(string? json, string? probeError, string reason)
    {
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", json);
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_ERROR", probeError);

        Outcome outcome = await Record(host.Options());

        AssertKeptAtTheOutputPath(outcome, reason);
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

    // The recording stays when only the check failed: same result as having no ffprobe, plus the reason.
    private void AssertDeliveredUnchecked(Outcome outcome, string reason)
    {
        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        Assert.Equal(-1, outcome.Result!.VideoFrames);
        Assert.Contains(outcome.Diagnostics, message => message.Contains("ffprobe") && message.Contains(reason));
    }

    // Catches: deleting a finished recording because ffprobe answered with something unparseable. The parse throws, which
    // isn't a verdict on the file.
    [Theory]
    [InlineData("not json", "invalid")]
    [InlineData("{}", "key")]
    public async Task ProbeThatAnswersNonsense_StillDeliversTheFile(string json, string reason)
    {
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", json);

        Outcome outcome = await Record(host.Options());

        AssertDeliveredUnchecked(outcome, reason);
    }

    // Catches: deleting a finished recording because ffprobe can't be started. Process.Start throws for this.
    [Fact]
    public async Task ProbeThatCantStart_StillDeliversTheFile()
    {
        File.SetUnixFileMode(HostTools.FindOnPath("ffprobe")!, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Outcome outcome = await Record(host.Options());

        AssertDeliveredUnchecked(outcome, "denied");
    }

    // Catches: blaming the recording when ffprobe itself is broken, as after a partial upgrade on a rolling distribution. The
    // loader exits 127 for any file, so a nonzero exit alone doesn't mean the file is bad.
    [Fact]
    public async Task ProbeWithMissingLibraries_StillDeliversTheFile()
    {
        File.WriteAllText(HostTools.FindOnPath("ffprobe")!, "#!/bin/sh\necho 'ffprobe: error while loading shared libraries: libavcodec.so.99' >&2\nexit 127\n");

        Outcome outcome = await Record(host.Options());

        AssertDeliveredUnchecked(outcome, "shared libraries");
    }

    // Catches: deleting a finished recording because ffprobe died while reading it, as in an OOM kill (137, here a real
    // SIGKILL) or a crash (139). Only exit 1 is ffprobe rejecting the file.
    [Theory]
    [InlineData("KILL", "exit 137")]
    [InlineData("139", "exit 139")]
    public async Task ProbeThatDies_StillDeliversTheFile(string death, string reason)
    {
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_DIE", death);

        Outcome outcome = await Record(host.Options());

        AssertDeliveredUnchecked(outcome, reason);
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

    // Catches: gsr hanging at startup making the app wait forever or report a start, and judging the file before gsr has saved it:
    // this fake only writes once it's told to stop. Takes the session's full 10 second limit.
    [Fact]
    public async Task RecorderThatNeverDeliversAFrame_TimesOutAndKeepsWhatItSaved()
    {
        host.SetMode("no_first_frame");

        Outcome outcome = await Record(host.Options());

        AssertKeptAtTheOutputPath(outcome, "didn't deliver a frame");
        host.AssertRecorderGone();
    }

    // Catches: treating the user's own stop before capture began as a failure, which would leave a file and show an error for it.
    [Fact]
    public async Task StopBeforeTheFirstFrame_KeepsNothing()
    {
        host.SetMode("no_first_frame");
        await using (ScreenRecorder recorder = new(host.Options()))
        {
            Task start = recorder.StartAsync();
            using CancellationTokenSource limit = new(Limit);
            while (host.Events("start").Count == 0) await Task.Delay(5, limit.Token);

            recorder.RequestStop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(Limit));
        }
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

    // Catches: a failed recording deleting a real file that something else put at the reserved name. Only the empty
    // reservation may go, on failure as on success.
    [Fact]
    public async Task FailureAfterTheNameWasTaken_LeavesTheOtherFileAlone()
    {
        host.SetMode("die_mid");

        Outcome outcome = await Record(host.Options(), _ =>
        {
            File.WriteAllText(host.OutputPath, "someone else's file");
            host.Trigger();
        }, stop: false);

        Assert.Contains("exit 139", outcome.Error?.Message);
        Assert.Equal("someone else's file", File.ReadAllText(host.OutputPath));
        Assert.Equal([host.OutputPath], Directory.GetFileSystemEntries(host.Work));
    }

    // Catches: a rejected recording being deleted, or replacing someone else's file, when the reserved name was taken meanwhile. It
    // can only stay under its hidden name then, so the error has to name that.
    [Fact]
    public async Task RejectedOutputWithTheNameTaken_KeepsBothFilesAndSaysWhere()
    {
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", null);
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_ERROR", "moov atom not found");

        Outcome outcome = await Record(host.Options(), _ => File.WriteAllText(host.OutputPath, "someone else's file"));

        RecordingKeptException error = Assert.IsType<RecordingKeptException>(outcome.Error);
        Assert.Equal("someone else's file", File.ReadAllText(host.OutputPath));
        Assert.NotEqual(host.OutputPath, error.KeptPath);
        Assert.Contains(error.KeptPath, error.Message);
        Assert.Equal(FakeHost.Video, File.ReadAllText(error.KeptPath));
    }

    private RecordingJournal.Entry SingleJournalEntry() =>
        JsonSerializer.Deserialize<RecordingJournal.Entry>(File.ReadAllText(Assert.Single(Directory.GetFiles(host.Journal))))!;

    // Catches: an entry that doesn't name what a later start needs, most of all the recorder's own pid and the hidden temporary
    // name, or one that stays after a normal finish, which a later start would then try to recover.
    [Fact]
    public async Task Journal_NamesTheRecordingWhileItRunsAndIsGoneAfter()
    {
        string final = Path.Combine(host.Work, "final.mp4");
        RecordingJournal.Entry? entry = null;

        Outcome outcome = await Record(host.Options() with { JournalFolder = host.Journal, RecoveryPath = final }, _ => entry = SingleJournalEntry());

        Assert.Null(outcome.Error);
        Assert.Equal(host.RecorderArgument("-o"), entry!.Temporary);
        Assert.Equal(host.OutputPath, entry.Reservation);
        Assert.Equal(final, entry.FinalPath);
        Assert.Equal(Environment.ProcessId, entry.OwnerPid);
        Assert.Equal(RecordingJournal.StartTimeOf(Environment.ProcessId), entry.OwnerStart);
        Assert.Equal(host.Events("start").Single().GetProperty("pid").GetInt32(), entry.RecorderPid);
        Assert.True(entry.RecorderStart > 0);
        Assert.Empty(Directory.GetFiles(host.Journal));
    }

    // Catches: an entry outliving a failure or an abort, which would make a later start report or move a file that was dealt with.
    [Theory]
    [InlineData("die_mid", false)]
    [InlineData("black_late", false)]
    [InlineData("normal", true)]
    public async Task Journal_IsGoneAfterAFailureOrAnAbort(string mode, bool abort)
    {
        host.SetMode(mode);

        await Record(host.Options() with { JournalFolder = host.Journal }, recorder =>
        {
            if (abort) recorder.RequestAbort(); else host.Trigger();
        }, stop: false);

        Assert.Empty(Directory.GetFiles(host.Journal));
    }

    // Catches: a second ShareX moving the file of a recording in progress, seen from the recording's side: it must finish normally
    // with the file at its own output path.
    [Fact]
    public async Task RecoveryWhileRecording_LeavesTheRecordingAlone()
    {
        RecordingRecovery? recovery = null;

        Outcome outcome = await Record(host.Options() with { JournalFolder = host.Journal }, _ => recovery = RecordingJournal.Recover(host.Journal));

        Assert.Equal(1, recovery!.StillRecording);
        Assert.Empty(recovery.Recovered);
        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
    }

    // Catches: an unwritable data folder costing the user the recording. The journal only protects against a kill, so it's not worth
    // stopping for.
    [Fact]
    public async Task JournalThatCantBeWritten_StillDeliversTheFileAndSaysSo()
    {
        string blocked = Path.Combine(host.Work, "blocked");
        File.WriteAllText(blocked, "");

        Outcome outcome = await Record(host.Options() with { JournalFolder = Path.Combine(blocked, "journal") });

        Assert.Null(outcome.Error);
        Assert.Equal(FakeHost.Video, File.ReadAllText(host.OutputPath));
        Assert.Contains(outcome.Diagnostics, message => message.Contains("journal"));
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
