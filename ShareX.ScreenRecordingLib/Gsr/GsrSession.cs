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

using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShareX.ScreenRecordingLib.Gsr;

/// <summary>One running gpu-screen-recorder process: spawn, IPC socket, stderr watch, stop, and what its exit means.</summary>
internal sealed partial class GsrSession : IDisposable
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(10);
    // Stopping converts the file from fragmented to regular MP4, so a long recording takes a while to save.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(5);
    private const int SIGINT = 2;
    private const int SIGKILL_EXIT_CODE = 128 + 9;
    private const string NoReplyPrefix = "no reply: ";

    // gsr exits 0 and writes a valid all-black MP4 when these appear, so they count as failure whatever the exit code.
    private static readonly string[] BlackVideoMarkers = ["no drm found", "drmfb handle is NULL"];

    private readonly Process process;
    private readonly ManualResetEvent exited = new(false);
    private readonly string socketPath, firstFrameFile;
    private readonly Task stderrReader, stdoutReader;
    private readonly Queue<string> stderrTail = new();
    private Socket? socket;
    private StreamReader? replies;
    private long nextRequestId;
    private volatile string? blackVideoMarker;
    private string? stopError;
    private bool killed;

    public WaitHandle Exited => exited;
    public bool HasCaptureFailure => blackVideoMarker != null;
    /// <summary>The video encoder named in gsr's log, for example h264_nvenc.</summary>
    public string? EncoderName { get; private set; }

    public GsrSession(string recorderPath, IEnumerable<string> arguments, string socketPath, string firstFrameFile)
    {
        this.socketPath = socketPath;
        this.firstFrameFile = firstFrameFile;
        // gsr has no consent prompt or indicator, so it must not keep recording if ShareX dies. The kernel sends the signal
        // when the thread that started it exits, and that's the recording thread, which outlives this session.
        string guard = HostTools.FindOnPath("setpriv")
            ?? throw new FileNotFoundException("setpriv wasn't found, so gpu-screen-recorder couldn't be stopped if ShareX crashed. Install util-linux.");
        ProcessStartInfo info = new(guard, ["--pdeathsig", "INT", "--", recorderPath, .. arguments])
        {
            UseShellExecute = false,
            // gsr's output goes to pipes, never a regular file: it corrupts the video when its stdout is one.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        HostTools.UseHostLibraries(info);
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => exited.Set();
        process.Start();
        process.StandardInput.Close();
        stderrReader = Task.Run(ReadStderrAsync);
        stdoutReader = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
    }

    /// <summary>Returns once the IPC socket is connected and the first frame exists. Throws what went wrong otherwise.</summary>
    public void WaitForFirstFrame(WaitHandle stop)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            if (stop.WaitOne(10)) throw new OperationCanceledException("Recording stopped before the first video frame.");
            if (exited.WaitOne(0))
            {
                DrainOutput();
                throw GetFailure() ?? new IOException("gpu-screen-recorder exited before it recorded a frame.");
            }
            if (HasCaptureFailure) throw GetFailure()!;
            TryConnect();
            if (socket != null && File.Exists(firstFrameFile)) return;
            if (elapsed.Elapsed > FirstFrameTimeout)
                throw new TimeoutException($"gpu-screen-recorder didn't deliver a frame within {FirstFrameTimeout.TotalSeconds:0} seconds. {Tail()}");
        }
    }

    private void TryConnect()
    {
        if (socket != null || !File.Exists(socketPath)) return;
        Socket candidate = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            candidate.Connect(new UnixDomainSocketEndPoint(socketPath));
            replies = new StreamReader(new NetworkStream(candidate, ownsSocket: false), Encoding.UTF8);
            socket = candidate;
        }
        catch (SocketException)
        {
            candidate.Dispose(); // The socket file exists before gsr is listening; try again on the next pass.
        }
    }

    /// <summary>Returns null on success, or why gsr didn't pause or resume.</summary>
    public string? SetPaused(bool paused) => Request("set-paused", paused, TimeSpan.FromSeconds(5));

    /// <summary>Stops gsr and waits for it to save and exit. IPC first, then SIGINT, and SIGKILL only as the last resort, because it loses the file.</summary>
    public void Stop()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        if (!exited.WaitOne(0))
        {
            stopError = Request("stop", null, StopTimeout);
            if (stopError != null && stopError.StartsWith(NoReplyPrefix, StringComparison.Ordinal))
            {
                stopError = null;
                kill(process.Id, SIGINT);
            }
            TimeSpan remaining = StopTimeout - elapsed.Elapsed;
            if (!exited.WaitOne(remaining > TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5)))
            {
                killed = true;
                process.Kill();
                exited.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
        DrainOutput();
    }

    /// <summary>What's wrong with this recording, or null when gsr finished cleanly. Exit codes only count once it has exited.</summary>
    public Exception? GetFailure()
    {
        if (blackVideoMarker != null)
        {
            return new IOException($"gpu-screen-recorder couldn't read the screen (\"{blackVideoMarker}\"), so the video would be black and was discarded. Check its helper with: getcap /usr/bin/gsr-kms-server");
        }
        if (killed) return new IOException($"gpu-screen-recorder didn't stop within {StopTimeout.TotalMinutes:0} minutes and had to be killed, so the recording is lost. {Tail()}");
        if (!process.HasExited) return null;
        string tail = Tail();
        return process.ExitCode switch
        {
            0 => stopError == null ? null : new IOException($"gpu-screen-recorder couldn't save the recording: {stopError}"),
            1 => new ArgumentException($"gpu-screen-recorder rejected its options (exit 1). {tail}"),
            2 => new NotSupportedException($"gpu-screen-recorder can't capture this source on Wayland (exit 2). {tail}"),
            50 => new IOException($"An audio device isn't available (exit 50). Choose the system audio and microphone devices again in the recording settings. {tail}"),
            51 => new IOException($"gpu-screen-recorder doesn't recognise the region or monitor (exit 51). The monitor layout may have changed. {tail}"),
            126 => new UnauthorizedAccessException($"gpu-screen-recorder's helper failed (exit 126), usually because gsr-kms-server is missing its capability. Run: sudo setcap cap_sys_admin+ep /usr/bin/gsr-kms-server. {tail}"),
            SIGKILL_EXIT_CODE => new IOException("gpu-screen-recorder was killed (SIGKILL) while recording. An MP4 can't be finalized after that, so the recording is lost."),
            int code => new IOException($"gpu-screen-recorder stopped unexpectedly (exit {code}), so the recording is lost. {tail}")
        };
    }

    private string? Request(string name, bool? data, TimeSpan timeout)
    {
        if (socket == null || replies == null) return NoReplyPrefix + "not connected";
        long id = ++nextRequestId;
        string request = data == null
            ? JsonSerializer.Serialize(new { id, name })
            : JsonSerializer.Serialize(new { id, name, data });
        try
        {
            socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            socket.Send(Encoding.UTF8.GetBytes(request + "\n"));
            string? reply = replies.ReadLine();
            if (reply == null) return NoReplyPrefix + "the socket closed";
            using JsonDocument document = JsonDocument.Parse(reply);
            if (document.RootElement.GetProperty("result").GetString() == "ok") return null;
            return document.RootElement.TryGetProperty("data", out JsonElement detail) ? detail.ToString() : reply;
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return NoReplyPrefix + ex.Message;
        }
    }

    private async Task ReadStderrAsync()
    {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is string line)
        {
            foreach (string marker in BlackVideoMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal)) blackVideoMarker ??= marker;
            }
            Match encoder = EncoderLogLine().Match(line);
            if (encoder.Success) EncoderName = encoder.Groups[1].Value;
            if (line.Contains("fps", StringComparison.Ordinal)) continue;
            lock (stderrTail)
            {
                stderrTail.Enqueue(line);
                if (stderrTail.Count > 3) stderrTail.Dequeue();
            }
        }
    }

    [GeneratedRegex(@"\[(h264_\w+|libx264|libopenh264)\s*@")]
    private static partial Regex EncoderLogLine();

    private string Tail()
    {
        lock (stderrTail) return string.Join(" ", stderrTail);
    }

    // The helper can briefly hold the pipes after gsr exits, so don't wait on them forever.
    private void DrainOutput() => Task.WaitAll([stderrReader, stdoutReader], TimeSpan.FromSeconds(2));

    public void Dispose()
    {
        if (!exited.WaitOne(0))
        {
            kill(process.Id, SIGINT);
            if (!exited.WaitOne(TimeSpan.FromSeconds(5)))
            {
                process.Kill();
                exited.WaitOne(TimeSpan.FromSeconds(5));
            }
        }
        DrainOutput();
        replies?.Dispose();
        socket?.Dispose();
        process.Dispose();
        exited.Dispose();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
