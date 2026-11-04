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

namespace ShareX.ScreenRecordingLib;

/// <summary>H.264/AAC MP4 recording through gpu-screen-recorder. Coordinates are physical desktop pixels.</summary>
public sealed record RecordingOptions
{
    /// <summary>Must end in .mp4. A GIF is encoded from the finished file afterwards, see ScreenRecordManager.</summary>
    public required string OutputPath { get; init; }
    /// <summary>Must fit inside one monitor. An odd width or height rounds up to even, see ScreenRecorder.CapturedRegion.</summary>
    public Rectangle Region { get; init; }
    public int FramesPerSecond { get; init; } = 30;
    /// <summary>Force a constant frame rate. Off by default because it can collapse under heavy GPU load.</summary>
    public bool ConstantFrameRate { get; init; }
    public int VideoBitrate { get; init; } = 8_000_000;
    public int AudioBitrate { get; init; } = 192_000;
    public bool IncludeCursor { get; init; } = true;
    public bool CaptureSystemAudio { get; init; } = true;
    public bool CaptureMicrophone { get; init; }
    /// <summary>Monitor source name from AudioCaptureDevices; null selects the default output.</summary>
    public string? SystemAudioDeviceId { get; init; }
    /// <summary>Source name from AudioCaptureDevices; null selects the default input.</summary>
    public string? MicrophoneDeviceId { get; init; }
    /// <summary>Not available yet: logged through Diagnostic, and recording continues without the camera.</summary>
    public bool CaptureCamera { get; init; }
    /// <summary>Camera symbolic link; null selects the first available camera.</summary>
    public string? CameraDeviceId { get; init; }
    /// <summary>Preferred camera mode; the closest supported resolution/rate is negotiated.</summary>
    public CameraCaptureResolution CameraResolution { get; init; } = CameraCaptureResolution.Size1280x720;
    public int CameraFramesPerSecond { get; init; } = 30;
    public CameraOverlayPosition CameraPosition { get; init; } = CameraOverlayPosition.BottomRight;
    /// <summary>Circles use a centered square crop; rectangles preserve the full camera image.</summary>
    public CameraOverlayShape CameraShape { get; init; } = CameraOverlayShape.Rectangle;
    /// <summary>Overlay width as a percentage of the recording, fitted to the selected shape.</summary>
    public int CameraWidthPercent { get; init; } = 20;
    /// <summary>Distance from the recording's corner in physical pixels, clamped to fit.</summary>
    public int CameraMargin { get; init; } = 16;
    /// <summary>gpu-screen-recorder has no gain option: anything but 1 is logged through Diagnostic and ignored.</summary>
    public float SystemAudioGain { get; init; } = 1;
    /// <summary>gpu-screen-recorder has no gain option: anything but 1 is logged through Diagnostic and ignored.</summary>
    public float MicrophoneGain { get; init; } = 1;
    /// <summary>When false, gpu-screen-recorder may fall back to CPU encoding if the GPU encoder is unavailable.</summary>
    public bool RequireHardwareEncoder { get; init; } = true;
    public TimeSpan Duration { get; init; }
    /// <summary>Where this recording notes its temporary file, so a later start can recover it if ShareX is killed. Null keeps no note. See RecordingJournal.</summary>
    public string? JournalFolder { get; init; }
    /// <summary>Where a recovered recording goes. Null means OutputPath.</summary>
    public string? RecoveryPath { get; init; }

    internal bool HasAudio => CaptureSystemAudio || CaptureMicrophone;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (!Path.GetExtension(OutputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Recording writes H.264/AAC MP4 files.", nameof(OutputPath));
        if (Region.Width < 2 || Region.Height < 2)
            throw new ArgumentException("Select a region of at least 2 × 2 pixels.", nameof(Region));
        if (FramesPerSecond is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(FramesPerSecond));
        if (VideoBitrate is < 100_000 or > 200_000_000) throw new ArgumentOutOfRangeException(nameof(VideoBitrate));
        if (AudioBitrate is not (96_000 or 128_000 or 160_000 or 192_000)) throw new ArgumentOutOfRangeException(nameof(AudioBitrate));
        if (Duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Duration));
        if (!Enum.IsDefined(CameraResolution)) throw new ArgumentOutOfRangeException(nameof(CameraResolution));
        if (!Enum.IsDefined(CameraPosition)) throw new ArgumentOutOfRangeException(nameof(CameraPosition));
        if (!Enum.IsDefined(CameraShape)) throw new ArgumentOutOfRangeException(nameof(CameraShape));
        if (CameraFramesPerSecond is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(CameraFramesPerSecond));
        if (CameraWidthPercent is < 5 or > 50) throw new ArgumentOutOfRangeException(nameof(CameraWidthPercent));
        if (CameraMargin is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(CameraMargin));
        if (!float.IsFinite(SystemAudioGain) || SystemAudioGain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(SystemAudioGain));
        if (!float.IsFinite(MicrophoneGain) || MicrophoneGain is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(MicrophoneGain));
    }
}

/// <summary>The recording failed, but the file it wrote has content and stays at KeptPath. It may not play.</summary>
public sealed class RecordingKeptException(string reason, string keptPath, Exception innerException)
    : IOException($"{reason} The file was kept at '{keptPath}', but it may not play.", innerException)
{
    public string Reason { get; } = reason;
    public string KeptPath { get; } = keptPath;
}

/// <summary>IsD3D11Aware is always false here; it stays so existing readers of this record keep compiling.</summary>
public sealed record VideoEncoderInfo(string Name, bool IsHardwareAccelerated, bool IsD3D11Aware);

/// <summary>VideoFrames is -1 when ffprobe couldn't count them. gpu-screen-recorder reports neither dropped frames nor audio discontinuities, so those two are always -1.</summary>
public sealed record RecordingResult(string OutputPath, TimeSpan Duration, long VideoFrames,
    long DroppedVideoFrames, long AudioDiscontinuities, VideoEncoderInfo Encoder);
