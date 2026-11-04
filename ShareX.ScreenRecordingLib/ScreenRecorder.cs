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

namespace ShareX.ScreenRecordingLib;

/// <summary>
/// A single recording session driving the host's gpu-screen-recorder. Prepare checks the host and reserves the output
/// during a countdown. The process only starts in StartAsync, because gpu-screen-recorder has no armed state.
/// </summary>
public sealed class ScreenRecorder : IDisposable, IAsyncDisposable
{
    // Measured: a silent microphone averaged 6 bytes per AAC packet, real audio 400 or more.
    private const double EmptyTrackBytesPerPacket = 32;

    private readonly RecordingOptions options;
    private readonly RecordingClock clock = new();
    private readonly object sync = new();
    private readonly ManualResetEvent stop = new(false);
    private readonly ManualResetEvent begin = new(false);
    private readonly AutoResetEvent changed = new(false);
    private readonly TaskCompletionSource prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RecordingResult> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? worker;
    private bool startRequested, disposed, resumeRequested;
    private int recording, aborted;
    private string? journalFile;

    public Task<RecordingResult> Completion => completed.Task;
    public VideoEncoderInfo? Encoder { get; private set; }
    /// <summary>Always null: the camera overlay isn't available with gpu-screen-recorder yet.</summary>
    public CameraCaptureInfo? Camera => null;
    /// <summary>The rectangle actually recorded: Region rounded up to even, moved back one pixel if that would leave the monitor.</summary>
    public Rectangle CapturedRegion { get; private set; }
    /// <summary>Things that don't stop the recording but that the user may want to know, such as an empty microphone track.</summary>
    public event Action<string>? Diagnostic;
    public bool IsRecording => Volatile.Read(ref recording) != 0;
    public bool IsPaused => clock.IsPaused;
    public TimeSpan Elapsed => TimeSpan.FromTicks(clock.Elapsed);

    public ScreenRecorder(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options with { OutputPath = Path.GetFullPath(options.OutputPath) };
    }

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker == null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                worker = new Thread(Run) { IsBackground = true, Name = "ShareX recording" };
                worker.Start();
            }
        }
        // Cancellation stops this session, including its process and partially created output.
        using CancellationTokenRegistration registration = cancellationToken.Register(RequestStop);
        await prepared.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (startRequested) throw new InvalidOperationException("A recorder instance can only be started once.");
            startRequested = true;
            begin.Set();
        }
        using CancellationTokenRegistration registration = cancellationToken.Register(RequestStop);
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Pause()
    {
        lock (sync)
        {
            if (disposed) return;
            resumeRequested = false;
            clock.Pause();
            changed.Set();
        }
    }

    public void Resume() => RequestResume();

    /// <summary>gpu-screen-recorder can't move a running capture, so a different position is logged and recording resumes where it was.</summary>
    public void Resume(Rectangle region)
    {
        if ((region.Width & ~1) != (options.Region.Width & ~1) || (region.Height & ~1) != (options.Region.Height & ~1))
            throw new ArgumentException("Moving the recording region cannot change its output dimensions.", nameof(region));
        if (region.Location != options.Region.Location)
            ReportDiagnostic($"The recording region can't move while recording, so it stays at {options.Region.X},{options.Region.Y}.");
        RequestResume();
    }

    private void RequestResume()
    {
        lock (sync)
        {
            if (disposed || !clock.IsPaused) return;
            resumeRequested = true;
            changed.Set();
        }
    }

    /// <summary>Nonblocking stop request, safe from a UI thread. Await StopAsync or Completion to finalize the output.</summary>
    public void RequestStop()
    {
        lock (sync) { if (!disposed) stop.Set(); }
    }

    /// <summary>Discard this recording.</summary>
    public void RequestAbort()
    {
        Interlocked.Exchange(ref aborted, 1);
        RequestStop();
    }

    public Task<RecordingResult> StopAsync()
    {
        lock (sync)
        {
            if (worker == null) throw new InvalidOperationException("The recorder has not been prepared.");
            if (!disposed) stop.Set();
        }
        return Completion;
    }

    private void Run()
    {
        string directory = Path.GetDirectoryName(options.OutputPath)!;
        // gsr silently overwrites its output, so it writes to a private name and the result moves without clobbering.
        string temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(options.OutputPath)}-{Guid.NewGuid():N}.mp4");
        string firstFrameFile = temporary + ".ts";
        string socketPath = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath(), $"sharex-gsr-{Guid.NewGuid():N}.sock");
        GsrSession? session = null;
        bool ownsOutput = false, keepTemporary = false;
        try
        {
            string recorder = HostTools.RequireRecorder();
            CapturedRegion = GsrCommand.PlanRegion(options.Region, HostTools.GetMonitors());
            Encoder = new VideoEncoderInfo("gpu-screen-recorder h264", options.RequireHardwareEncoder, false);
            ReportIgnoredOptions();

            Directory.CreateDirectory(directory);
            // Reserve the name atomically. Never overwrite or delete a pre-existing user file.
            using (new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            ownsOutput = true;
            WriteJournal(temporary, 0);
            prepared.TrySetResult();
            if (WaitHandle.WaitAny([stop, begin]) == 0) throw new OperationCanceledException("Recording stopped before capture began.");

            session = new GsrSession(recorder, GsrCommand.BuildArguments(options, CapturedRegion, temporary, socketPath), socketPath, firstFrameFile);
            WriteJournal(temporary, session.ProcessId);
            session.WaitForFirstFrame(stop);
            clock.Start(RecordingClock.Now);
            Volatile.Write(ref recording, 1);
            started.TrySetResult();

            RunUntilStopped(session);

            clock.Stop();
            Volatile.Write(ref recording, 0);
            session.Stop();
            if (Volatile.Read(ref aborted) != 0) throw new OperationCanceledException("Recording aborted.");
            if (session.GetFailure() is Exception failure) throw failure;
            if (session.EncoderName is string encoderName)
                Encoder = new VideoEncoderInfo(encoderName, encoderName is not ("libx264" or "libopenh264"), false);

            RecordingResult result = InspectOutput(temporary);
            ownsOutput = false;
            try { ReplaceReservation(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                keepTemporary = true;
                throw new IOException($"The recording finished, but it couldn't be moved to '{options.OutputPath}'. The completed recording is available at '{temporary}'.", ex);
            }
            completed.TrySetResult(result);
        }
        catch (Exception ex)
        {
            // gsr has to be gone first: it may still be saving, and that decides whether there's a file to keep.
            session?.Dispose();
            session = null;
            if (ownsOutput)
            {
                // An abort, or a stop before the first frame, is the user's choice and not a failure, so that file goes.
                if (ex is not OperationCanceledException && Volatile.Read(ref aborted) == 0 && HasContent(temporary))
                {
                    ex = KeepRejected(temporary, ex, out keepTemporary);
                }
                else
                {
                    try
                    {
                        // Only the empty reservation may go, here as on the way to success.
                        FileInfo reservation = new(options.OutputPath);
                        if (reservation.Exists && reservation.Length == 0) reservation.Delete();
                    }
                    catch (Exception cleanup) { ex = new AggregateException("Recording failed and its incomplete output could not be removed.", ex, cleanup); }
                }
            }
            prepared.TrySetException(ex); started.TrySetException(ex); completed.TrySetException(ex);
        }
        finally
        {
            clock.Stop();
            Volatile.Write(ref recording, 0);
            session?.Dispose();
            if (!keepTemporary) TryDelete(temporary);
            TryDelete(firstFrameFile);
            TryDelete(socketPath);
            if (journalFile != null) TryDelete(journalFile);
        }
    }

    private static bool HasContent(string path)
    {
        FileInfo file = new(path);
        return file.Exists && file.Length > 0;
    }

    /// <summary>Moves the file to the reserved name. Only the empty reservation may go: anything else at that name now belongs to someone else.</summary>
    private void ReplaceReservation(string temporary)
    {
        if (File.Exists(options.OutputPath))
        {
            if (new FileInfo(options.OutputPath).Length != 0) throw new IOException($"'{options.OutputPath}' appeared while recording.");
            File.Delete(options.OutputPath);
        }
        File.Move(temporary, options.OutputPath, overwrite: false);
    }

    /// <summary>Leaves a file that failed its checks at the output path, or at the temporary name if that's taken, and says where.</summary>
    private RecordingKeptException KeepRejected(string temporary, Exception why, out bool keptAtTemporary)
    {
        keptAtTemporary = false;
        try { ReplaceReservation(temporary); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { keptAtTemporary = true; }
        return new RecordingKeptException(why.Message, keptAtTemporary ? temporary : options.OutputPath, why);
    }

    /// <summary>A recording without its note can still finish normally, so failing to write one only gets logged.</summary>
    private void WriteJournal(string temporary, int recorderPid)
    {
        if (options.JournalFolder == null) return;
        try
        {
            journalFile ??= Path.Combine(options.JournalFolder, $"{Guid.NewGuid():N}.json");
            RecordingJournal.Write(journalFile, temporary, options.OutputPath, options.RecoveryPath ?? options.OutputPath, recorderPid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportDiagnostic($"Couldn't write the recording journal, so this recording can't be recovered if ShareX is killed: {ex.Message}");
        }
    }

    /// <summary>Returns when a stop was requested, gsr exited by itself, a black video was detected or the fixed duration ran out.</summary>
    private void RunUntilStopped(GsrSession session)
    {
        bool paused = false;
        WaitHandle[] waits = [stop, changed, session.Exited];
        while (true)
        {
            int signaled = WaitHandle.WaitAny(waits, 50);
            if (signaled is 0 or 2 || session.HasCaptureFailure) return;
            if (options.Duration > TimeSpan.Zero && clock.Elapsed >= options.Duration.Ticks) return;

            bool wantPaused;
            lock (sync) wantPaused = clock.IsPaused && !resumeRequested;
            if (wantPaused != paused)
            {
                if (session.SetPaused(wantPaused) is string error)
                    ReportDiagnostic($"gpu-screen-recorder didn't {(wantPaused ? "pause" : "resume")}: {error}");
                paused = wantPaused;
            }
            // Media time stays paused until gsr has actually resumed. A later Pause supersedes this request.
            lock (sync)
            {
                if (resumeRequested && !paused)
                {
                    clock.Resume();
                    resumeRequested = false;
                }
            }
        }
    }

    /// <summary>A clean exit doesn't prove there's a usable file, so check it before it replaces the reserved name.</summary>
    private RecordingResult InspectOutput(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length == 0) throw new IOException("gpu-screen-recorder exited normally but wrote no video.");
        VideoEncoderInfo encoder = Encoder!;

        RecordingProbe? probe;
        string notChecked = "ffprobe isn't installed or timed out";
        try { probe = HostTools.ProbeRecording(path); }
        catch (Exception ex)
        {
            // The recorder finished, and a check that didn't finish says nothing about the file, so it's no reason to lose it.
            probe = null;
            notChecked = $"ffprobe failed ({ex.Message})";
        }
        if (probe == null)
        {
            ReportDiagnostic($"{notChecked}, so the frame count and microphone track weren't checked.");
            return new RecordingResult(options.OutputPath, Elapsed, -1, -1, -1, encoder);
        }
        if (!probe.IsReadable) throw new IOException($"gpu-screen-recorder wrote a file that can't be read: {probe.Error.Replace(path + ": ", "")}"); // That hidden name is about to change.
        if (probe.VideoFrames == 0) throw new IOException("gpu-screen-recorder wrote a file with no video frames.");
        if (options.CaptureMicrophone) ReportEmptyMicrophone(probe);
        return new RecordingResult(options.OutputPath, probe.Duration ?? Elapsed, probe.VideoFrames ?? -1, -1, -1, encoder);
    }

    private void ReportEmptyMicrophone(RecordingProbe probe)
    {
        int track = options.CaptureSystemAudio ? 1 : 0;
        if (track >= probe.AudioBytesPerPacket.Count)
            ReportDiagnostic("The recording has no microphone track, so the microphone wasn't recorded.");
        else if (probe.AudioBytesPerPacket[track] is not double bytes)
            ReportDiagnostic("Couldn't tell whether the microphone track has sound: ffprobe didn't report its size.");
        else if (bytes < EmptyTrackBytesPerPacket)
            ReportDiagnostic($"The microphone track looks empty ({bytes:0} bytes per packet), so the microphone may be silent. Check the input device in the recording settings.");
    }

    private void ReportIgnoredOptions()
    {
        if (options.CaptureCamera) ReportDiagnostic("The camera overlay isn't available yet, so recording continues without the camera.");
        if (options.CaptureSystemAudio && options.SystemAudioGain != 1) ReportDiagnostic("gpu-screen-recorder has no gain option, so the system audio gain is ignored.");
        if (options.CaptureMicrophone && options.MicrophoneGain != 1) ReportDiagnostic("gpu-screen-recorder has no gain option, so the microphone gain is ignored.");
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportDiagnostic($"Couldn't remove '{path}': {ex.Message}"); }
    }

    private void ReportDiagnostic(string message)
    {
        try { Diagnostic?.Invoke(message); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            stop.Set();
        }
        worker?.Join();
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            stop.Dispose(); begin.Dispose(); changed.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        if (worker != null) { try { await Completion.ConfigureAwait(false); } catch { } }
        Dispose();
    }
}
