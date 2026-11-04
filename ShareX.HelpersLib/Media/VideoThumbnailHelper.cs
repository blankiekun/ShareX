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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib
{
    // One video frame for a thumbnail, from the host's ffmpeg. The files can be anything, so every failure ends in null and the caller's placeholder.
    public static class VideoThumbnailHelper
    {
        private const string FrameOffsetSeconds = "1";

        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        // Shared by every window, so a long list of videos can't start one ffmpeg per item.
        private static readonly SemaphoreSlim slots = new SemaphoreSlim(2);

        public static Task<byte[]?> ReadFrameAsync(string filePath, int width, int height, CancellationToken token)
        {
            return ReadFrameAsync(FileHelpers.FindExecutableOnPath("ffmpeg"), filePath, width, height, DefaultTimeout, token);
        }

        // Returns a PNG that fits inside width x height, or null. Time spent waiting for a slot doesn't count towards the timeout.
        public static async Task<byte[]?> ReadFrameAsync(string? ffmpegPath, string filePath, int width, int height, TimeSpan timeout, CancellationToken token)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return null;
            }

            try
            {
                await slots.WaitAsync(token);

                try
                {
                    using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(timeout);

                    byte[] frame = await Task.Run(async () =>
                    {
                        byte[] atOffset = await RunAsync(ffmpegPath, BuildArguments(filePath, FrameOffsetSeconds, width, height), limit.Token);

                        // A clip shorter than the offset has no frame there.
                        return atOffset.Length > 0 ? atOffset : await RunAsync(ffmpegPath, BuildArguments(filePath, "0", width, height), limit.Token);
                    }, limit.Token);

                    return frame.Length > 0 ? frame : null;
                }
                finally
                {
                    slots.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine($"No video thumbnail for {filePath}: {(e is OperationCanceledException ? "timed out" : e.Message)}");
                return null;
            }
        }

        // An absolute path can't be read as a protocol or an option, and -protocol_whitelist keeps a playlist or concat list named .mp4 on local files.
        // -threads 1 keeps a huge video from buffering a frame per thread.
        private static string[] BuildArguments(string filePath, string offset, int width, int height) =>
        [
            "-hide_banner", "-nostdin", "-v", "error", "-threads", "1", "-protocol_whitelist", "file", "-ss", offset, "-i", Path.GetFullPath(filePath),
            "-map", "0:V:0", "-frames:v", "1", "-vf", $"scale={Math.Max(1, width)}:{Math.Max(1, height)}:force_original_aspect_ratio=decrease",
            "-pix_fmt", "rgb24", "-c:v", "png", "-f", "image2pipe", "pipe:1"
        ];

        private static async Task<byte[]> RunAsync(string ffmpegPath, string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            using Process process = new Process();
            process.StartInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();

            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            });

            // Disposing a Process leaves its streams open until the finalizers run. A damaged file can write a lot of errors, so they are read and dropped.
            using StreamReader standardOutput = process.StandardOutput;
            using StreamReader standardError = process.StandardError;
            using MemoryStream image = new MemoryStream();
            Task errors = standardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            await standardOutput.BaseStream.CopyToAsync(image, CancellationToken.None);
            await errors;
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"ffmpeg exited with code {process.ExitCode}");
            }

            return image.ToArray();
        }
    }
}
