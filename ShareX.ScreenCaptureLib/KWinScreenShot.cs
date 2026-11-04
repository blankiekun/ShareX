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

#nullable enable

using ShareX.HelpersLib;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ShareX.ScreenCaptureLib;

/// <summary>The pixel layout KWin announces in the reply to a ScreenShot2 call.</summary>
/// <param name="Opaque">The image is one output, so nothing in it can be transparent, whatever the unused byte holds.</param>
internal readonly record struct ScreenShotLayout(int Width, int Height, int Stride, bool Opaque)
{
    // QImage::Format values. These are the two KWin 6.8 sends: outputs and areas, and windows. Anything else goes to spectacle.
    private const uint FormatRgbx8888 = 16;
    private const uint FormatRgba8888Premultiplied = 18;

    /// <returns>Null when this isn't a layout <see cref="KWinScreenShot.ReadPixels"/> can read.</returns>
    public static ScreenShotLayout? Create(string? type, uint width, uint height, uint stride, uint format, string? screen)
    {
        if (type != "raw" || format is not (FormatRgbx8888 or FormatRgba8888Premultiplied) ||
            width == 0 || height == 0 || width > int.MaxValue / 4 || height > int.MaxValue || stride > int.MaxValue || stride < width * 4)
        {
            return null;
        }

        return new ScreenShotLayout((int)width, (int)height, (int)stride, screen != null);
    }
}

/// <summary>
/// Screenshots through KWin's org.kde.KWin.ScreenShot2: no process to start and no PNG in between, and a window is drawn on its own,
/// so it doesn't need to be active or uncovered. Options match what spectacle asks KWin for, so images are the same.
/// A null result always means the caller should use spectacle instead; only a window that no longer exists is told apart.
/// </summary>
internal static class KWinScreenShot
{
    private const string Service = "org.kde.KWin.ScreenShot2";
    private const string ObjectPath = "/org/kde/KWin/ScreenShot2";
    private const string Interface = "org.kde.KWin.ScreenShot2";

    private const string InvalidWindowError = Interface + ".Error.InvalidWindow";

    // KWin answers from its main thread once the image is drawn. Spectacle gives it 4 s as well.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    // KWin before 6.8 refuses callers without a desktop file naming this interface, and a session may have no such service.
    private static volatile bool _unavailable;
    private static int _loggedInUse;

    public static SpectacleImage? TryCapture(SpectacleMode mode, bool includeCursor)
    {
        string method = mode switch
        {
            SpectacleMode.AllMonitors => "CaptureWorkspace",
            SpectacleMode.ActiveMonitor => "CaptureActiveScreen",
            _ => "CaptureActiveWindow"
        };

        SpectacleImage? image = TryRequest(method, null, mode, includeCursor, out _);

        if (image != null && mode == SpectacleMode.AllMonitors)
        {
            ClearOutsideOutputs(image.Bitmap);
        }

        return image;
    }

    /// <param name="windowId">KWin's internal id for the window.</param>
    /// <param name="mode">One of the window modes, for the frame and shadow.</param>
    /// <param name="includeCursor">Draw the pointer into the image.</param>
    /// <param name="windowGone">KWin doesn't know this window any more, which spectacle couldn't change.</param>
    public static SpectacleImage? TryCaptureWindow(string windowId, SpectacleMode mode, bool includeCursor, out bool windowGone)
    {
        return TryRequest("CaptureWindow", windowId, mode, includeCursor, out windowGone);
    }

    /// <summary>What the desktop shows in a rectangle, less ShareX's own windows.</summary>
    /// <param name="area">In KWin logical coordinates.</param>
    /// <param name="includeCursor">Draw the pointer into the image.</param>
    public static SpectacleImage? TryCaptureArea(Rectangle area, bool includeCursor)
    {
        return TryRequest("CaptureArea", null, SpectacleMode.AllMonitors, includeCursor, out _, area);
    }

    // KWin draws the whole rectangle, so a window or shadow hanging off a monitor would show in the space beside it, where nobody can see it on screen.
    private static void ClearOutsideOutputs(SKBitmap bitmap)
    {
        try
        {
            KWinDesktop desktop = KWinDesktop.Query();
            DesktopCoordinates coordinates = desktop.Coordinates.ForImage(desktop.Outputs, new Size(bitmap.Width, bitmap.Height));
            ClearOutside(bitmap, desktop.Outputs.Select(x => coordinates.LogicalToImage(x.Geometry)));
        }
        catch (KWinScriptException e)
        {
            DebugHelper.WriteException(e, "The space between monitors could not be cleared from a screenshot.");
        }
    }

    internal static void ClearOutside(SKBitmap bitmap, IEnumerable<Rectangle> keep)
    {
        using SKRegion outside = new(new SKRectI(0, 0, bitmap.Width, bitmap.Height));

        foreach (Rectangle area in keep)
        {
            outside.Op(new SKRectI(area.Left, area.Top, area.Right, area.Bottom), SKRegionOperation.Difference);
        }

        // One rectangle of monitors, which is most desktops, leaves nothing to do.
        if (!outside.IsEmpty)
        {
            using SKCanvas canvas = new(bitmap);
            canvas.ClipRegion(outside);
            canvas.Clear(SKColors.Transparent);
        }
    }

    private static SpectacleImage? TryRequest(string method, string? windowId, SpectacleMode mode, bool includeCursor, out bool windowGone, Rectangle? area = null)
    {
        windowGone = false;

        if (_unavailable)
        {
            return null;
        }

        try
        {
            // The same way out of a synchronization context as KWinScript.Run, as this can be called from the UI thread.
            SpectacleImage image = Task.Run(() => RequestAsync(method, windowId, mode, includeCursor, area)).GetAwaiter().GetResult();

            if (Interlocked.Exchange(ref _loggedInUse, 1) == 0)
            {
                DebugHelper.WriteLine("Screenshots are taken through KWin's ScreenShot2 interface.");
            }

            return image;
        }
        catch (DBusErrorReplyException e) when (e.ErrorName == InvalidWindowError)
        {
            windowGone = true;
            return null;
        }
        catch (DBusErrorReplyException e) when (IsPermanentError(e.ErrorName))
        {
            _unavailable = true;
            DebugHelper.WriteLine($"Screenshots are taken through Spectacle, because KWin's ScreenShot2 interface is not available: {e.ErrorName}: {e.Message}");
            return null;
        }
        catch (Exception e)
        {
            DebugHelper.WriteLine($"This screenshot goes through Spectacle, because {method} failed: {e.Message}");
            return null;
        }
    }

    private static bool IsPermanentError(string name)
    {
        return name is Interface + ".Error.NoAuthorized" or "org.freedesktop.DBus.Error.AccessDenied" or
            "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner" or
            "org.freedesktop.DBus.Error.UnknownObject" or "org.freedesktop.DBus.Error.UnknownInterface" or
            "org.freedesktop.DBus.Error.UnknownMethod";
    }

    private static async Task<SpectacleImage> RequestAsync(string method, string? windowId, SpectacleMode mode, bool includeCursor, Rectangle? area)
    {
        string sessionBus = DBusAddress.Session ?? throw new ScreenCaptureException("There is no D-Bus session bus.");
        using DBusConnection connection = new(sessionBus);
        await connection.ConnectAsync().AsTask().WaitAsync(ReplyTimeout).ConfigureAwait(false);

        AnonymousPipeServerStream pipe = new(PipeDirection.In, HandleInheritability.None);
        ScreenShotLayout layout;
        string? screen;
        SKBitmap bitmap;

        try
        {
            // KWin sends the reply first and writes from another thread afterwards, so waiting for it can't block on a full pipe.
            (ScreenShotLayout? known, screen) = await CallAsync(connection, method, windowId, area, mode, includeCursor, pipe.ClientSafePipeHandle)
                .WaitAsync(ReplyTimeout).ConfigureAwait(false);

            // KWin has its own copy now. With this one still open, a write that stops early would never reach end of file.
            pipe.DisposeLocalCopyOfClientHandle();

            layout = known ?? throw new ScreenCaptureException("KWin sent a screenshot in a format ShareX doesn't read.");
            bitmap = AllocateBitmap(layout);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        // Straight into the bitmap, so one image in memory at a time. The reading thread closes the pipe, because closing it blocks while a read is in progress.
        Task read = Task.Run(() =>
        {
            using (pipe)
            {
                ReadPixels(pipe, layout, bitmap);
            }
        });

        try
        {
            await read.WaitAsync(ReadTimeout).ConfigureAwait(false);
            return new SpectacleImage(bitmap, screen);
        }
        catch (TimeoutException)
        {
            // The read still holds the pixels, until KWin gives up on the pipe after a minute.
            _ = read.ContinueWith(_ => bitmap.Dispose(), TaskScheduler.Default);
            throw;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static SKBitmap AllocateBitmap(ScreenShotLayout layout)
    {
        // Bgra8888 and these alpha types are what decoding spectacle's PNG gives, and the rest of ShareX has only ever seen those.
        SKBitmap bitmap = new(new SKImageInfo(layout.Width, layout.Height, SKColorType.Bgra8888, layout.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul));

        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            throw new ScreenCaptureException($"Not enough memory for a {layout.Width}x{layout.Height} screenshot.");
        }

        return bitmap;
    }

    private static Task<(ScreenShotLayout? Layout, string? Screen)> CallAsync(DBusConnection connection, string method, string? windowId, Rectangle? area, SpectacleMode mode,
        bool includeCursor, SafeHandle pipe)
    {
        MessageBuffer call;
        MessageWriter writer = connection.GetMessageWriter();

        try
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Interface, method, windowId != null ? "sa{sv}h" : area != null ? "iiuua{sv}h" : "a{sv}h");

            if (windowId != null)
            {
                writer.WriteString(windowId);
            }

            if (area is Rectangle rectangle)
            {
                writer.WriteInt32(rectangle.X);
                writer.WriteInt32(rectangle.Y);
                writer.WriteUInt32((uint)rectangle.Width);
                writer.WriteUInt32((uint)rectangle.Height);
            }

            ArrayStart options = writer.WriteDictionaryStart();
            WriteOption(ref writer, "include-cursor", includeCursor);
            WriteOption(ref writer, "native-resolution", true);
            // On by default, and would drop ShareX's main window, pinned images and mouse highlighter from every screen capture.
            // Only an area is asked for by a tool whose own windows can be in the way.
            WriteOption(ref writer, "hide-caller-windows", area != null);
            WriteOption(ref writer, "include-decoration", mode is SpectacleMode.ActiveWindow or SpectacleMode.ActiveWindowNoShadow);
            WriteOption(ref writer, "include-shadow", mode == SpectacleMode.ActiveWindow);
            writer.WriteDictionaryEnd(options);
            writer.WriteHandle(pipe);
            call = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        return connection.CallMethodAsync(call, static (Message message, object? _) =>
        {
            Dictionary<string, VariantValue> reply = message.GetBodyReader().ReadDictionaryOfStringToVariantValue();
            string? screen = TextOf(reply, "screen");

            return (ScreenShotLayout.Create(TextOf(reply, "type"), NumberOf(reply, "width"), NumberOf(reply, "height"), NumberOf(reply, "stride"),
                NumberOf(reply, "format"), screen), screen);
        }, null);
    }

    private static void WriteOption(ref MessageWriter writer, string name, bool value)
    {
        writer.WriteDictionaryEntryStart();
        writer.WriteString(name);
        writer.WriteVariantBool(value);
    }

    private static string? TextOf(Dictionary<string, VariantValue> reply, string key)
    {
        return reply.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.String ? value.GetString() : null;
    }

    private static uint NumberOf(Dictionary<string, VariantValue> reply, string key)
    {
        return reply.TryGetValue(key, out VariantValue value) && value.Type == VariantValueType.UInt32 ? value.GetUInt32() : 0;
    }

    /// <summary>Fills a BGRA <paramref name="bitmap"/> from KWin's RGBA rows, which are <see cref="ScreenShotLayout.Stride"/> apart.</summary>
    /// <exception cref="EndOfStreamException">The image stopped early, so no partial image is ever returned.</exception>
    internal static void ReadPixels(Stream source, ScreenShotLayout layout, SKBitmap bitmap)
    {
        Span<byte> pixels = bitmap.GetPixelSpan();
        int rowLength = layout.Width * 4;
        byte[] padding = new byte[layout.Stride - rowLength];

        for (int y = 0; y < layout.Height; y++)
        {
            Span<byte> row = pixels.Slice(y * bitmap.RowBytes, rowLength);
            source.ReadExactly(row);
            source.ReadExactly(padding);
            ConvertRow(row, layout.Opaque);
        }
    }

    // Keeps the fourth byte unless told otherwise: in the space between monitors of different sizes KWin leaves it 0, even though it calls
    // the format RGBX, and an all-monitors image needs that space transparent.
    private static void ConvertRow(Span<byte> row, bool opaque)
    {
        uint alpha = opaque ? 0xFF000000 : 0;

        foreach (ref uint pixel in MemoryMarshal.Cast<byte, uint>(row))
        {
            pixel = (pixel & 0xFF00FF00) | ((pixel & 0x000000FF) << 16) | ((pixel & 0x00FF0000) >> 16) | alpha;
        }
    }
}
