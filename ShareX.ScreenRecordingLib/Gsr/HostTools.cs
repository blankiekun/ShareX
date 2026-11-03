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
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShareX.ScreenRecordingLib.Gsr;

internal sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>What ffprobe says about a finished recording. Unknown numbers are null.</summary>
internal sealed record RecordingProbe(bool IsReadable, string Error, long? VideoFrames, TimeSpan? Duration, IReadOnlyList<double?> AudioBytesPerPacket);

/// <summary>Short-lived calls to tools on the host: finding them, reading the monitor layout, inspecting a recording.</summary>
internal static partial class HostTools
{
    public static string? FindOnPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Host binaries link host libraries, so they must not inherit the AppImage's library path.</summary>
    public static void UseHostLibraries(ProcessStartInfo info) => info.Environment.Remove("LD_LIBRARY_PATH");

    /// <summary>Returns null when the tool isn't installed or doesn't finish in time.</summary>
    public static ToolResult? Run(string tool, params string[] arguments)
    {
        string? path = FindOnPath(tool);
        if (path == null) return null;
        ProcessStartInfo info = new(path, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        UseHostLibraries(info);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill();
            return null;
        }
        return new ToolResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    /// <summary>
    /// Finds gpu-screen-recorder and checks its helper up front. A helper without the capability either asks for root or,
    /// worse, exits 0 with an all-black video, so this can't be left to the exit code.
    /// </summary>
    public static string RequireRecorder()
    {
        string recorder = FindOnPath("gpu-screen-recorder")
            ?? throw new FileNotFoundException("gpu-screen-recorder isn't installed, or isn't on PATH. Install it with your package manager, for example: sudo pacman -S gpu-screen-recorder");

        // gsr prefers a helper beside its own binary and falls back to PATH.
        string beside = Path.Combine(Path.GetDirectoryName(recorder)!, "gsr-kms-server");
        string helper = File.Exists(beside) ? beside : FindOnPath("gsr-kms-server")
            ?? throw new FileNotFoundException("gsr-kms-server, the helper that ships with gpu-screen-recorder, wasn't found. Reinstall gpu-screen-recorder.");

        ToolResult getcap = Run("getcap", helper)
            ?? throw new FileNotFoundException($"getcap wasn't found, so the capability on {helper} can't be checked. Install libcap.");
        if (!SysAdminCapability().IsMatch(getcap.Output))
            throw new UnauthorizedAccessException($"{helper} doesn't have the cap_sys_admin capability, so recording would fail or come out black. Reinstall gpu-screen-recorder, or run: sudo setcap cap_sys_admin+ep '{helper}'");
        return recorder;
    }

    [GeneratedRegex(@"cap_sys_admin[\w,]*[=+][a-z]*e")]
    private static partial Regex SysAdminCapability();

    /// <summary>Lists enabled monitors using kscreen-doctor, read-only.</summary>
    public static IReadOnlyList<Monitor> GetMonitors()
    {
        ToolResult result = Run("kscreen-doctor", "-j")
            ?? throw new FileNotFoundException("kscreen-doctor wasn't found, so the recording region can't be checked against the monitors. It ships with Plasma (libkscreen).");
        if (result.ExitCode != 0)
            throw new IOException($"kscreen-doctor couldn't read the monitor layout: {result.Error.Trim()}");

        List<Monitor> monitors = [];
        using JsonDocument document = JsonDocument.Parse(result.Output);
        foreach (JsonElement output in document.RootElement.GetProperty("outputs").EnumerateArray())
        {
            if (!output.GetProperty("enabled").GetBoolean() || !output.GetProperty("connected").GetBoolean()) continue;
            JsonElement position = output.GetProperty("pos"), size = output.GetProperty("size");
            int width = size.GetProperty("width").GetInt32(), height = size.GetProperty("height").GetInt32();
            // KScreen rotation 2 and 8 are the quarter turns; "size" is the unrotated mode.
            if (output.GetProperty("rotation").GetInt32() is 2 or 8) (width, height) = (height, width);
            monitors.Add(new Monitor(output.GetProperty("name").GetString() ?? "unknown",
                new Rectangle(position.GetProperty("x").GetInt32(), position.GetProperty("y").GetInt32(), width, height),
                output.GetProperty("scale").GetDouble()));
        }
        return monitors;
    }

    /// <summary>Returns null when ffprobe isn't installed. Reads only the file header, so it's fast on long recordings.</summary>
    public static RecordingProbe? ProbeRecording(string path)
    {
        ToolResult? result = Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,nb_frames,bit_rate,duration:format=duration", "-of", "json", path);
        if (result == null) return null;
        if (result.ExitCode != 0) return new RecordingProbe(false, result.Error.Trim(), null, null, []);

        using JsonDocument document = JsonDocument.Parse(result.Output);
        long? frames = null;
        bool hasVideo = false;
        List<double?> audio = [];
        foreach (JsonElement stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            string type = stream.GetProperty("codec_type").GetString() ?? "";
            if (type == "video")
            {
                hasVideo = true;
                frames = Number(stream, "nb_frames") is double count ? (long)count : null;
            }
            else if (type == "audio")
            {
                audio.Add(Number(stream, "bit_rate") is double bitRate && Number(stream, "duration") is double seconds && Number(stream, "nb_frames") is double packets && packets > 0
                    ? bitRate / 8 * seconds / packets
                    : null);
            }
        }
        if (!hasVideo) return new RecordingProbe(false, "the file has no video stream", null, null, []);
        TimeSpan? duration = document.RootElement.TryGetProperty("format", out JsonElement format) && Number(format, "duration") is double total
            ? TimeSpan.FromSeconds(total) : null;
        return new RecordingProbe(true, "", frames, duration, audio);
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : null;
}
