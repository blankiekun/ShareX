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

using Avalonia.Threading;
using ShareX.HelpersLib;
using DrawingPoint = System.Drawing.Point;

namespace ShareX.Tools;

// Runs only while the tool is on: disposing it unloads the KWin script and closes the window, and there is no timer. The script
// reports on a D-Bus thread, and only the newest position is ever waiting for the UI thread.
internal sealed class MouseHighlighterService : IDisposable
{
    private readonly MouseHighlighterOverlayWindow _window = new();
    private readonly KWinCursorWatcher? _watcher;
    private DesktopCoordinates _coordinates = null!;
    private long _latestPosition;
    private int _moveQueued;
    private bool _disposed;

    public MouseHighlighterOptions Options { get; private set; }

    public MouseHighlighterService(MouseHighlighterOptions options)
    {
        options.Validate();
        Options = options;
        _window.Apply(options);
        try
        {
            _coordinates = KWinDesktop.Query().Coordinates;
            _window.Screens.Changed += OnScreensChanged;
            _watcher = KWinCursorWatcher.Start(OnPointerMoved);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void UpdateOptions(MouseHighlighterOptions options)
    {
        options.Validate();
        Options = options;
        _window.Apply(options);
    }

    private void OnPointerMoved(DrawingPoint logical)
    {
        Interlocked.Exchange(ref _latestPosition, ((long)logical.X << 32) | (uint)logical.Y);
        if (Interlocked.Exchange(ref _moveQueued, 1) == 0) Dispatcher.UIThread.Post(MoveWindow);
    }

    private void MoveWindow()
    {
        Volatile.Write(ref _moveQueued, 0);
        if (_disposed) return;
        long position = Interlocked.Read(ref _latestPosition);
        _window.MoveTo(_coordinates.LogicalToX11(new DrawingPoint((int)(position >> 32), (int)position)));
    }

    // The largest output scale sets the X11 scale, so plugging in a monitor can change where everything is.
    private void OnScreensChanged(object? sender, EventArgs e)
    {
        try { _coordinates = KWinDesktop.Query().Coordinates; }
        catch (KWinScriptException ex) { MouseHighlighterManager.StopOnError(this, ex); }
    }

    public void ReleaseScript() => _watcher?.Dispose();

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _window.Screens.Changed -= OnScreensChanged;
        _window.Close();
    }
}
