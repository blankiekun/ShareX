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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.Tools.Localization;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ShareX.Tools;

public sealed record VideoPlaybackInfo(double Duration, int Width, int Height);

/// <summary>
/// A player on the host's libmpv. mpv decodes and draws into its own child of this control's X11 window, so frames
/// never pass through Avalonia. mpv doesn't take input in that window, so X11 hands clicks and motion to Avalonia.
/// All public operations and events run on the Avalonia UI thread.
/// </summary>
public sealed class MpvPlayer : NativeControlHost, IDisposable
{
    private TaskCompletionSource<nint> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<VideoPlaybackInfo>? _loadCompletion;
    private MpvSession? _session;
    private int _generation;
    private double _duration;
    private double _videoAspectRatio;
    private double _seekTarget;
    private bool _seeking, _ended, _playWhenReady, _disposed, _leftButtonPressed;
    private double _volume = 1;
    private bool _isMuted;

    public event Action<double>? PositionChanged;
    public event Action<bool>? IsPlayingChanged;
    public event Action<Exception>? PlaybackFailed;
    public event Action? VideoClicked;

    /// <summary>Includes a play request mpv hasn't acted on yet.</summary>
    public bool IsPlaybackRequested => _playWhenReady;

    public double Volume
    {
        get => _volume;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (!double.IsFinite(value)) return;
            _volume = Math.Clamp(value, 0, 1);
            _session?.Command("set", "volume", Number(_volume * 100));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            _isMuted = value;
            _session?.Command("set", "mute", _isMuted ? "yes" : "no");
        }
    }

    public MpvPlayer()
    {
        Focusable = false;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    // Avalonia only hit-tests what a control draws, and this control's pixels belong to the child window.
    public override void Render(DrawingContext context) => context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_videoAspectRatio <= 0) return base.MeasureOverride(availableSize);
        double width = Math.Min(availableSize.Width, availableSize.Height * _videoAspectRatio);
        return double.IsFinite(width) ? new Size(width, width / _videoAspectRatio) : base.MeasureOverride(availableSize);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        IPlatformHandle handle = base.CreateNativeControlCore(parent);
        if (handle.HandleDescriptor != "XID") throw new PlatformNotSupportedException(handle.HandleDescriptor);
        _windowReady.TrySetResult(handle.Handle);
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // mpv draws into this window, so it has to be gone first.
        CloseMedia();
        base.DestroyNativeControlCore(control);
        _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<VideoPlaybackInfo> LoadAsync(string path, CancellationToken token = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseMedia();
        int generation = _generation;
        try
        {
            nint window = await _windowReady.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _generation) throw new OperationCanceledException();

            _loadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _session = new MpvSession(this, window, _volume, _isMuted);
            _session.Command("loadfile", Path.GetFullPath(path));
            return await _loadCompletion.Task.WaitAsync(token);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            if (generation == _generation) CloseMedia();
            throw new InvalidOperationException(Strings.VideoPlayer_MpvMissing, ex);
        }
        catch
        {
            if (generation == _generation) CloseMedia();
            throw;
        }
    }

    public void Unload()
    {
        Dispatcher.UIThread.VerifyAccess();
        CloseMedia();
    }

    public void Seek(double seconds)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_session == null || _duration <= 0 || !double.IsFinite(seconds)) return;
        Pause();
        _ended = false;
        // Positions already queued belong to the old place; playback restart says the new one is on screen.
        _seeking = true;
        _seekTarget = Math.Clamp(seconds, 0, _duration);
        _session.Command("seek", Number(_seekTarget), "absolute+exact");
    }

    public void Play()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_session == null || _duration <= 0) return;
        if (_ended) Seek(0);
        _playWhenReady = true;
        _session.Command("set", "pause", "no");
    }

    public void Pause()
    {
        Dispatcher.UIThread.VerifyAccess();
        _playWhenReady = false;
        _session?.Command("set", "pause", "yes");
        IsPlayingChanged?.Invoke(false);
    }

    public void StepFrame(bool forward)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_session == null || _duration <= 0) return;
        Pause();
        _session.Command(forward ? "frame-step" : "frame-back-step");
    }

    private void OnFirstFrame(MpvSession session, VideoPlaybackInfo info, int displayWidth, int displayHeight)
    {
        if (session != _session || _loadCompletion == null) return;
        if (!double.IsFinite(info.Duration) || info.Duration <= 0 || displayWidth <= 0 || displayHeight <= 0)
        {
            OnFailed(session, Strings.VideoTrimmer_InvalidVideo);
            return;
        }

        _duration = info.Duration;
        _videoAspectRatio = (double)displayWidth / displayHeight;
        InvalidateMeasure();
        _loadCompletion.TrySetResult(info);
    }

    private void OnFailed(MpvSession session, string message)
    {
        if (session != _session) return;
        InvalidOperationException error = new(message);
        if (_loadCompletion is { Task.IsCompleted: false } load)
        {
            load.TrySetException(error); // LoadAsync owns cleanup.
            return;
        }

        CloseMedia();
        PlaybackFailed?.Invoke(error);
    }

    private void OnPosition(MpvSession session, double position)
    {
        if (session != _session || _duration <= 0 || _seeking || _ended || !double.IsFinite(position)) return;
        PositionChanged?.Invoke(Math.Clamp(position, 0, _duration));
    }

    private void OnSeekFinished(MpvSession session, double position)
    {
        if (session != _session) return;
        _seeking = false;
        // An exact seek lands at or after its target, unless no frame is left there. mpv then shows the last frame paused,
        // and doesn't report the end of the file until it plays, which it won't do from here.
        if (position < _seekTarget - 0.001) ReachEnd();
        else OnPosition(session, position);
    }

    private void OnPauseChanged(MpvSession session, bool paused)
    {
        if (session != _session) return;
        // A pause already queued before a new play request can arrive afterward. Pause() itself reports the stop.
        if (!paused && _playWhenReady) IsPlayingChanged?.Invoke(true);
    }

    private void OnEndReached(MpvSession session, bool ended)
    {
        if (session != _session || _duration <= 0) return;
        if (ended) ReachEnd();
        else _ended = false;
    }

    private void ReachEnd()
    {
        _ended = true;
        _seeking = _playWhenReady = false;
        IsPlayingChanged?.Invoke(false);
        // The last frame starts before the end. The windows use "at the end" to decide where the next play starts.
        PositionChanged?.Invoke(_duration);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (VideoClicked == null || _duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _leftButtonPressed = true;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_leftButtonPressed) return;
        _leftButtonPressed = false;
        e.Handled = true;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) VideoClicked?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        _leftButtonPressed = false;
        base.OnPointerCaptureLost(e);
    }

    private void CloseMedia()
    {
        _generation++;
        _leftButtonPressed = false;
        _loadCompletion?.TrySetCanceled();
        _loadCompletion = null;
        _duration = 0;
        _videoAspectRatio = 0;
        InvalidateMeasure();
        _seeking = _ended = _playWhenReady = false;
        _session?.Stop();
        _session = null;
        IsPlayingChanged?.Invoke(false);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        CloseMedia();
        _windowReady.TrySetCanceled();
    }

    /// <summary>
    /// One mpv handle and the thread that reads its events. That thread is the only place the handle is destroyed, and
    /// only after the UI thread has made its last call, so no call can reach a destroyed handle. Every other mpv call
    /// on this handle comes from the UI thread, except the reads made while handling an event.
    /// </summary>
    private sealed unsafe class MpvSession
    {
        private const ulong TimePosId = 1, PauseId = 2, EofReachedId = 3;

        private static nint s_xErrorHandler;

        private readonly MpvPlayer _owner;
        private readonly nint _handle;
        private readonly ManualResetEventSlim _lastCallReturned = new();
        private readonly ManualResetEventSlim _destroyed = new();
        private volatile bool _stopRequested;
        private bool _stopped, _firstFrameSeen, _failureReported;
        private double _position;
        private int _positionQueued;

        public MpvSession(MpvPlayer owner, nint window, double volume, bool muted)
        {
            _owner = owner;
            KeepXErrorHandler();
            // mpv_create refuses any other numeric locale, because it parses and prints numbers with libc.
            setlocale(LC_NUMERIC, "C");
            _handle = mpv_create();
            if (_handle == 0) throw new InvalidOperationException(Strings.VideoTrimmer_InvalidVideo);
            try
            {
                Configure(window, volume, muted);
                new Thread(ReadEvents) { IsBackground = true, Name = "mpv events" }.Start();
            }
            catch
            {
                mpv_terminate_destroy(_handle);
                RestoreXErrorHandler();
                throw;
            }
        }

        private void Configure(nint window, double volume, bool muted)
        {
            // These mean the user's config, scripts and terminal stay out, and yt-dlp is never run: files are local.
            foreach ((string name, string value) in new[]
            {
                ("config", "no"), ("load-scripts", "no"), ("ytdl", "no"), ("terminal", "no"),
                ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("input-cursor", "no"), ("cursor-autohide", "no"),
                ("osc", "no"), ("osd-level", "0"),
                // X11 explicitly: with WAYLAND_DISPLAY set, mpv would otherwise pick a Wayland context and ignore the window.
                ("vo", "gpu,x11"), ("gpu-context", "x11egl,x11"), ("wid", window.ToString(CultureInfo.InvariantCulture)),
                // Pausing on the last frame instead of closing the file matches what the trimmer and player expect.
                ("idle", "yes"), ("pause", "yes"), ("keep-open", "always"), ("hr-seek", "yes"),
                ("audio-client-name", "ShareX"), ("volume", Number(volume * 100)), ("mute", muted ? "yes" : "no")
            })
            {
                Check(mpv_set_option_string(_handle, name, value));
            }

            // Scripts built into libmpv. Each one costs a thread. Older and newer versions have different ones.
            foreach (string script in new[] { "stats-overlay", "osd-console", "auto-profiles", "select", "commands", "context-menu", "positioning" })
            {
                mpv_set_option_string(_handle, "load-" + script, "no");
            }

            Check(mpv_request_log_messages(_handle, "warn"));
            Check(mpv_initialize(_handle));
            Check(mpv_observe_property(_handle, TimePosId, "time-pos", MpvFormatDouble));
            Check(mpv_observe_property(_handle, PauseId, "pause", MpvFormatFlag));
            Check(mpv_observe_property(_handle, EofReachedId, "eof-reached", MpvFormatFlag));
        }

        private static void Check(int result)
        {
            if (result < 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(mpv_error_string(result)));
        }

        public void Command(params string[] arguments)
        {
            nint[] native = new nint[arguments.Length + 1];
            try
            {
                for (int i = 0; i < arguments.Length; i++) native[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                // mpv copies the strings before returning. Unlike the command line, a path here is never parsed or expanded.
                fixed (nint* pointer = native) mpv_command_async(_handle, 0, pointer);
            }
            finally
            {
                foreach (nint argument in native) Marshal.FreeCoTaskMem(argument);
            }
        }

        /// <summary>Waits for mpv so that nothing else on the UI thread runs during the X11 handler gap described below.</summary>
        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _stopRequested = true;
            mpv_wakeup(_handle);
            _lastCallReturned.Set();
            if (!_destroyed.Wait(TimeSpan.FromSeconds(3))) ToolsDiagnostics.ReportWarning("MpvPlayer", "mpv took over 3 seconds to close.");
        }

        private void ReadEvents()
        {
            try
            {
                while (!_stopRequested)
                {
                    MpvEvent* mpvEvent = mpv_wait_event(_handle, -1);
                    if (mpvEvent->Id == MpvEventShutdown)
                    {
                        Fail(Strings.VideoTrimmer_InvalidVideo);
                        break;
                    }

                    // A managed exception here would end the process, and a bad event isn't worth that.
                    try { HandleEvent(mpvEvent); }
                    catch (Exception ex) { ToolsDiagnostics.ReportWarning("MpvPlayer", ex.Message, ex); }
                }
            }
            finally
            {
                _lastCallReturned.Wait();
                mpv_terminate_destroy(_handle);
                RestoreXErrorHandler();
                _destroyed.Set();
            }
        }

        private void HandleEvent(MpvEvent* mpvEvent)
        {
            switch (mpvEvent->Id)
            {
                case MpvEventLogMessage:
                    MpvLogMessage* message = (MpvLogMessage*)mpvEvent->Data;
                    ToolsDiagnostics.ReportWarning("mpv", $"{Marshal.PtrToStringUTF8(message->Prefix)}: {Marshal.PtrToStringUTF8(message->Text)?.TrimEnd()}");
                    break;
                case MpvEventEndFile:
                    MpvEndFile* end = (MpvEndFile*)mpvEvent->Data;
                    if (end->Reason == MpvEndFileReasonError) Fail(Marshal.PtrToStringUTF8(mpv_error_string(end->Error)) ?? Strings.VideoTrimmer_InvalidVideo);
                    break;
                case MpvEventVideoReconfig:
                    // A video output that failed its probe has closed its X11 display by now, so it's safe to put this back.
                    RestoreXErrorHandler();
                    break;
                case MpvEventPlaybackRestart:
                    HandlePlaybackRestart();
                    break;
                case MpvEventPropertyChange:
                    HandlePropertyChange(mpvEvent->ReplyUserdata, (MpvProperty*)mpvEvent->Data);
                    break;
            }
        }

        private void HandlePlaybackRestart()
        {
            if (_firstFrameSeen)
            {
                double position = GetDouble("time-pos");
                Dispatcher.UIThread.Post(() => _owner.OnSeekFinished(this, position));
                return;
            }

            _firstFrameSeen = true;
            RestoreXErrorHandler();
            VideoPlaybackInfo info = new(GetDouble("duration"), (int)GetDouble("width"), (int)GetDouble("height"));
            int displayWidth = (int)GetDouble("dwidth"), displayHeight = (int)GetDouble("dheight");
            ToolsDiagnostics.ReportWarning("MpvPlayer", $"vo={GetString("current-vo")} context={GetString("current-gpu-context")} ao={GetString("current-ao")} hwdec={GetString("hwdec-current")}");
            Dispatcher.UIThread.Post(() => _owner.OnFirstFrame(this, info, displayWidth, displayHeight));
        }

        private void HandlePropertyChange(ulong id, MpvProperty* property)
        {
            if (property->Data == 0) return; // Not available, as with no file loaded yet.
            switch (id)
            {
                case TimePosId when property->Format == MpvFormatDouble:
                    Volatile.Write(ref _position, *(double*)property->Data);
                    // time-pos changes on every frame. Keep one post waiting at most, carrying the newest value.
                    if (Interlocked.Exchange(ref _positionQueued, 1) == 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            Volatile.Write(ref _positionQueued, 0);
                            _owner.OnPosition(this, Volatile.Read(ref _position));
                        });
                    }
                    break;
                case PauseId when property->Format == MpvFormatFlag:
                    bool paused = *(int*)property->Data != 0;
                    Dispatcher.UIThread.Post(() => _owner.OnPauseChanged(this, paused));
                    break;
                case EofReachedId when property->Format == MpvFormatFlag:
                    bool ended = *(int*)property->Data != 0;
                    Dispatcher.UIThread.Post(() => _owner.OnEndReached(this, ended));
                    break;
            }
        }

        private void Fail(string message)
        {
            if (_failureReported) return;
            _failureReported = true;
            Dispatcher.UIThread.Post(() => _owner.OnFailed(this, message));
        }

        private double GetDouble(string name)
        {
            double value;
            return mpv_get_property(_handle, name, MpvFormatDouble, &value) < 0 ? double.NaN : value;
        }

        private string GetString(string name)
        {
            nint value;
            if (mpv_get_property(_handle, name, MpvFormatString, &value) < 0) return "none";
            string? text = Marshal.PtrToStringUTF8(value);
            mpv_free(value);
            return text ?? "none";
        }

        // X11 error handlers are global to the process. Closing a video output, mpv calls XSetErrorHandler(NULL) (checked
        // in the disassembly of 0.41), which brings back Xlib's default: print and exit. Avalonia normally ignores errors
        // from windows that have just gone, so the next one would end ShareX. Its handler goes back after each close.
        private static void KeepXErrorHandler()
        {
            if (s_xErrorHandler != 0) return;
            // Xlib only reads the current handler by replacing it. Going through this one avoids the default even briefly.
            s_xErrorHandler = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&IgnoreXError);
            XSetErrorHandler(s_xErrorHandler);
        }

        private static void RestoreXErrorHandler() => XSetErrorHandler(s_xErrorHandler);

        [UnmanagedCallersOnly]
        private static int IgnoreXError(nint display, nint error) => 0;

        private const int LC_NUMERIC = 1;
        private const int MpvFormatString = 1, MpvFormatFlag = 3, MpvFormatDouble = 5;
        private const int MpvEventShutdown = 1, MpvEventLogMessage = 2, MpvEventEndFile = 7, MpvEventVideoReconfig = 17,
            MpvEventPlaybackRestart = 21, MpvEventPropertyChange = 22;
        private const int MpvEndFileReasonError = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEvent
        {
            public int Id;
            public int Error;
            public ulong ReplyUserdata;
            public nint Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvProperty
        {
            public nint Name;
            public int Format;
            public nint Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvEndFile
        {
            public int Reason;
            public int Error;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MpvLogMessage
        {
            public nint Prefix;
            public nint Level;
            public nint Text;
        }

        [DllImport("libc.so.6")]
        private static extern nint setlocale(int category, [MarshalAs(UnmanagedType.LPUTF8Str)] string locale);
        [DllImport("libX11.so.6")]
        private static extern nint XSetErrorHandler(nint handler);
        [DllImport("libmpv.so.2")]
        private static extern nint mpv_create();
        [DllImport("libmpv.so.2")]
        private static extern int mpv_initialize(nint handle);
        [DllImport("libmpv.so.2")]
        private static extern void mpv_terminate_destroy(nint handle);
        [DllImport("libmpv.so.2")]
        private static extern int mpv_set_option_string(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport("libmpv.so.2")]
        private static extern int mpv_request_log_messages(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
        [DllImport("libmpv.so.2")]
        private static extern int mpv_observe_property(nint handle, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
        [DllImport("libmpv.so.2")]
        private static extern int mpv_command_async(nint handle, ulong id, nint* arguments);
        [DllImport("libmpv.so.2")]
        private static extern int mpv_get_property(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, void* data);
        [DllImport("libmpv.so.2")]
        private static extern MpvEvent* mpv_wait_event(nint handle, double timeout);
        [DllImport("libmpv.so.2")]
        private static extern void mpv_wakeup(nint handle);
        [DllImport("libmpv.so.2")]
        private static extern nint mpv_error_string(int error);
        [DllImport("libmpv.so.2")]
        private static extern void mpv_free(nint data);
    }
}
