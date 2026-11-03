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

namespace ShareX.ScreenRecordingLib.Video;

// shortcut: recording always fails because Windows.Graphics.Capture does not exist on Linux, replace with portal ScreenCast and PipeWire in M6.
internal sealed class GraphicsCapture : IDisposable
{
    private const string Unavailable = "Screen recording is not available on Linux yet.";

    internal sealed record Target(Rectangle Source, Point Destination, nint Monitor, nint WindowHandle = 0);

    public int Width { get; }
    public int Height { get; }
    public bool HasFrame => false;
    public long LatestTimestamp => 0;
    public bool HasEnded => true;

    public static List<Target> GetTargets(RecordingOptions options, out int width, out int height) =>
        throw new PlatformNotSupportedException(Unavailable);

    public GraphicsCapture(GraphicsDevice graphics, GpuVideoProcessor processor, List<Target> targets, bool cursor, int fps) =>
        throw new PlatformNotSupportedException(Unavailable);

    public void Retarget(List<Target> targets) => throw new PlatformNotSupportedException(Unavailable);

    public void Start() => throw new PlatformNotSupportedException(Unavailable);

    public void Update() => throw new PlatformNotSupportedException(Unavailable);

    public void Dispose() { }
}
