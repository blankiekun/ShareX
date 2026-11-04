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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

/// <summary>
/// Plays sounds through the host's player, a host dependency like ffmpeg and spectacle. One sound plays at a time: a new one
/// stops the last, as Windows' PlaySound did. That also keeps a fast run of sounds, or a player that hangs, from piling up.
/// </summary>
public static class HostSoundPlayer
{
    // pw-play can be installed without PipeWire being the running server, so a player that fails hands over to the next.
    private static readonly string[] PlayerNames = ["pw-play", "paplay"];

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, byte> TemporaryFiles = new();
    private static Process? _current;
    private static int _missingPlayerLogged;

    static HostSoundPlayer()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopForExit();
    }

    /// <summary>
    /// Background waits die with the process, so nothing else would stop the player or remove its file. ProcessExit calls this.
    /// A signal that keeps its default action doesn't reach ProcessExit, so that handler has to call it too.
    /// </summary>
    public static void StopForExit()
    {
        Stop();

        foreach (string path in TemporaryFiles.Keys)
        {
            DeleteTemporaryFile(path);
        }
    }

    /// <summary>Writes the sound to a file only this user can read, plays it and removes it. Takes ownership of the stream.</summary>
    public static async Task PlayStreamAsync(Stream stream, string? searchPath)
    {
        string? path = null;

        try
        {
            await using (stream)
            {
                List<string> players = FindPlayers(searchPath);

                if (players.Count == 0)
                {
                    LogMissingPlayer();
                    return;
                }

                // GetTempFileName creates the file with mode 600 under TMPDIR.
                path = Path.GetTempFileName();
                TemporaryFiles.TryAdd(path, 0);

                await using (FileStream file = File.OpenWrite(path))
                {
                    await stream.CopyToAsync(file).ConfigureAwait(false);
                }

                await PlayAsync(players, path).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Could not play a sound");
        }
        finally
        {
            if (path != null)
            {
                DeleteTemporaryFile(path);
            }
        }
    }

    public static async Task PlayFileAsync(string filePath, string? searchPath)
    {
        try
        {
            List<string> players = FindPlayers(searchPath);

            if (players.Count == 0)
            {
                LogMissingPlayer();
                return;
            }

            await PlayAsync(players, filePath).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e, "Could not play a sound");
        }
    }

    private static async Task PlayAsync(List<string> players, string filePath)
    {
        foreach (string player in players)
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo(player)
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    ArgumentList = { filePath }
                }
            };

            try
            {
                lock (Gate)
                {
                    Stop();
                    process.Start();
                    _current = process;
                }
            }
            catch (Win32Exception e)
            {
                DebugHelper.WriteLine($"Could not start {player}: {e.Message}");
                continue;
            }

            string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);

            lock (Gate)
            {
                if (_current != process)
                {
                    return;
                }

                _current = null;
            }

            if (process.ExitCode == 0)
            {
                return;
            }

            DebugHelper.WriteLine($"{player} exited with code {process.ExitCode}: {error.Trim()}");
        }
    }

    private static void Stop()
    {
        lock (Gate)
        {
            try
            {
                _current?.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                // It has already exited.
            }

            _current = null;
        }
    }

    /// <summary>The executables on PATH in order of preference. The working directory is never searched.</summary>
    private static List<string> FindPlayers(string? searchPath)
    {
        List<string> players = [];

        foreach (string name in PlayerNames)
        {
            foreach (string directory in (searchPath ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(directory, name);

                if (File.Exists(candidate) &&
                    (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                {
                    players.Add(candidate);
                    break;
                }
            }
        }

        return players;
    }

    private static void LogMissingPlayer()
    {
        if (Interlocked.Exchange(ref _missingPlayerLogged, 1) == 0)
        {
            DebugHelper.WriteLine("No sound player was found on PATH (pw-play or paplay), so ShareX plays no sounds.");
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DebugHelper.WriteException(e, "Could not delete a temporary sound file: " + path);
        }

        TemporaryFiles.TryRemove(path, out _);
    }
}
