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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib;

public enum SpectacleMode
{
    AllMonitors,
    ActiveMonitor,
    ActiveWindow,
    ActiveWindowNoShadow,
    ActiveWindowClientArea
}

public sealed class ScreenCaptureException : Exception
{
    public ScreenCaptureException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>A captured image. <see cref="ScreenName"/> is the output captured, when KWin or spectacle says.</summary>
public sealed record SpectacleImage(SKBitmap Bitmap, string? ScreenName);

/// <summary>
/// The only place that spawns spectacle. KWin's ScreenShot2 interface goes first, see <see cref="KWinScreenShot"/>,
/// and spectacle is what's left when KWin refuses or fails.
/// </summary>
public static class SpectacleCapture
{
    public const string ExecutableName = "spectacle";

    // One run hung past 20 s in testing, where a normal run takes 150 ms to 700 ms.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    // Only what spectacle needs from the session. An AppImage runtime sets LD_LIBRARY_PATH, QT_PLUGIN_PATH,
    // APPDIR and others that would break a Qt program from the host, so nothing else is passed on.
    private static readonly string[] InheritedVariables =
    [
        "HOME", "USER", "LOGNAME", "LANG", "LANGUAGE", "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY",
        "DBUS_SESSION_BUS_ADDRESS", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME",
        "XDG_RUNTIME_DIR", "XDG_CONFIG_DIRS", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_SESSION_ID",
        "KDE_FULL_SESSION", "KDE_SESSION_VERSION", "KDE_SESSION_UID", "KDE_DEBUG"
    ];

    /// <exception cref="ScreenCaptureException">Spectacle is missing, hung, failed, or wrote no usable image.</exception>
    public static SpectacleImage Capture(SpectacleMode mode, bool includeCursor)
    {
        SpectacleImage? image = KWinScreenShot.TryCapture(mode, includeCursor);

        if (image != null)
        {
            return image;
        }

        DirectoryInfo directory;

        try
        {
            // Created with mode 0700. The screenshot must not be readable by other users, even briefly.
            directory = Directory.CreateTempSubdirectory("sharex-capture-");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ScreenCaptureException("Could not create a temporary folder for the screenshot: " + e.Message, e);
        }

        try
        {
            string filePath = Path.Combine(directory.FullName, "capture.png");
            RunSpectacle(CreateStartInfo(mode, includeCursor, filePath));
            return LoadImage(filePath);
        }
        finally
        {
            try
            {
                directory.Delete(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DebugHelper.WriteException(e, "Temporary screenshot folder could not be deleted: " + directory.FullName);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(SpectacleMode mode, bool includeCursor, string filePath)
    {
        string searchPath = StripAppDir(Environment.GetEnvironmentVariable("PATH"));
        string executable = FindExecutable(searchPath) ?? throw new ScreenCaptureException(
            "ShareX needs Spectacle to take screenshots on KDE Plasma, but the 'spectacle' program was not found. " +
            "Install it with your package manager (on Arch: pacman -S spectacle).");

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Never add -u, -t, -w or a call to KWin's CaptureInteractive here. They wait for a click, and KWin keeps
        // its "select window" prompt on screen even after spectacle is killed. -u locked up a real desktop in testing.
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-n");

        foreach (string argument in GetModeArguments(mode))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (includeCursor)
        {
            startInfo.ArgumentList.Add("-p");
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(filePath);

        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = searchPath;

        foreach (string name in InheritedVariables)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value != null)
            {
                startInfo.Environment[name] = value;
            }
        }

        string? dataDirectories = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (dataDirectories != null)
        {
            startInfo.Environment["XDG_DATA_DIRS"] = StripAppDir(dataDirectories);
        }

        return startInfo;
    }

    private static string[] GetModeArguments(SpectacleMode mode)
    {
        return mode switch
        {
            SpectacleMode.AllMonitors => ["-f"],
            SpectacleMode.ActiveMonitor => ["-m"],
            SpectacleMode.ActiveWindow => ["-a"],
            SpectacleMode.ActiveWindowNoShadow => ["-a", "-S"],
            SpectacleMode.ActiveWindowClientArea => ["-a", "-e", "-S"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static void RunSpectacle(ProcessStartInfo startInfo)
    {
        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ScreenCaptureException("Could not start Spectacle: " + e.Message, e);
        }

        process.StandardInput.Close();
        process.OutputDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        Task<string> errors = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Watchdog))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                DebugHelper.WriteException(e, "Spectacle could not be stopped.");
            }

            throw new ScreenCaptureException($"Spectacle did not finish within {Watchdog.TotalSeconds:0} s and was stopped. No screenshot was taken.");
        }

        if (process.ExitCode != 0)
        {
            // Bounded: a child that outlives spectacle could keep the pipe open.
            string errorText = errors.Wait(TimeSpan.FromMilliseconds(500)) ? errors.Result.Trim() : "";

            throw new ScreenCaptureException($"Spectacle failed with exit code {process.ExitCode}, so no screenshot was taken. {errorText}".TrimEnd());
        }
    }

    private static SpectacleImage LoadImage(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new ScreenCaptureException("Spectacle finished without writing a screenshot. Is there an active window or monitor to capture?");
        }

        SKBitmap? bitmap = SKBitmap.Decode(filePath);
        if (bitmap == null)
        {
            throw new ScreenCaptureException("Spectacle wrote a screenshot that could not be read.");
        }

        return new SpectacleImage(bitmap, ReadPngText(filePath, "screen"));
    }

    /// <summary>Reads one uncompressed text chunk. Spectacle stores the output it captured there.</summary>
    internal static string? ReadPngText(string filePath, string key)
    {
        using BinaryReader reader = new(File.OpenRead(filePath));
        reader.BaseStream.Seek(8, SeekOrigin.Begin);
        byte[] prefix = Encoding.Latin1.GetBytes(key + "\0");

        while (reader.BaseStream.Length - reader.BaseStream.Position >= 12)
        {
            byte[] lengthBytes = reader.ReadBytes(4);
            uint length = (uint)(lengthBytes[0] << 24 | lengthBytes[1] << 16 | lengthBytes[2] << 8 | lengthBytes[3]);
            string type = Encoding.ASCII.GetString(reader.ReadBytes(4));

            if (type == "IDAT" || type == "IEND" || length > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                return null;
            }

            if (type == "tEXt")
            {
                byte[] data = reader.ReadBytes((int)length);
                if (data.AsSpan().StartsWith(prefix))
                {
                    return Encoding.Latin1.GetString(data, prefix.Length, data.Length - prefix.Length);
                }

                reader.BaseStream.Seek(4, SeekOrigin.Current);
            }
            else
            {
                reader.BaseStream.Seek(length + 4L, SeekOrigin.Current);
            }
        }

        return null;
    }

    private static string? FindExecutable(string searchPath)
    {
        foreach (string directory in searchPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, ExecutableName);

            if (File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string StripAppDir(string? pathList)
    {
        string? appDir = Environment.GetEnvironmentVariable("APPDIR");
        IEnumerable<string> entries = (pathList ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);

        if (!string.IsNullOrEmpty(appDir))
        {
            entries = entries.Where(x => !x.StartsWith(appDir, StringComparison.Ordinal));
        }

        return string.Join(':', entries);
    }
}
