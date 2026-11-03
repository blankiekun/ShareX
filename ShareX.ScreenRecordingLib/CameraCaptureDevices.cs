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

namespace ShareX.ScreenRecordingLib;

public sealed record CameraCaptureDevice(string Id, string Name);

public static class CameraCaptureDevices
{
    /// <summary>Always empty: the camera overlay isn't available with gpu-screen-recorder yet, so no camera can be chosen.</summary>
    public static IReadOnlyList<CameraCaptureDevice> GetCameras() => [];
}

public enum CameraOverlayPosition { TopLeft, TopRight, BottomLeft, BottomRight }
public enum CameraOverlayShape { Rectangle, Circle }
public enum CameraCaptureResolution { Size640x480, Size1280x720, Size1920x1080 }

/// <summary>The camera's negotiated mode; actual delivery depends on the device.</summary>
public sealed record CameraCaptureInfo(string Name, int Width, int Height, double FramesPerSecond, string PixelFormat);
