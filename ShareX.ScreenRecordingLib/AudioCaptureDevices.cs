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

using ShareX.ScreenRecordingLib.Gsr;
using System.Text.Json;

namespace ShareX.ScreenRecordingLib;

/// <summary>A PulseAudio or PipeWire source. Id can be passed to RecordingOptions.MicrophoneDeviceId or SystemAudioDeviceId.</summary>
public sealed record AudioCaptureDevice(string Id, string Name);

public static class AudioCaptureDevices
{
    /// <summary>Lists real audio inputs using pactl, without opening them.</summary>
    public static IReadOnlyList<AudioCaptureDevice> GetMicrophones() => GetSources(monitors: false);

    /// <summary>Lists the monitor sources of audio outputs, which is how system audio gets recorded.</summary>
    public static IReadOnlyList<AudioCaptureDevice> GetSystemAudioDevices() => GetSources(monitors: true);

    public static AudioCaptureDevice? GetDefaultSystemAudioDevice()
    {
        string? sink = GetDefault("get-default-sink");
        return sink == null ? null : GetSystemAudioDevices().FirstOrDefault(device => device.Id == sink + ".monitor");
    }

    public static AudioCaptureDevice? GetDefaultMicrophone()
    {
        string? source = GetDefault("get-default-source");
        return source == null ? null : GetMicrophones().FirstOrDefault(device => device.Id == source);
    }

    private static string? GetDefault(string command)
    {
        ToolResult? result = HostTools.Run("pactl", command);
        return result?.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private static List<AudioCaptureDevice> GetSources(bool monitors)
    {
        List<AudioCaptureDevice> devices = [];
        ToolResult? result = HostTools.Run("pactl", "-f", "json", "list", "sources");
        if (result?.ExitCode != 0) return devices;
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Output);
            foreach (JsonElement source in document.RootElement.EnumerateArray())
            {
                string id = source.GetProperty("name").GetString() ?? "";
                if (id.EndsWith(".monitor", StringComparison.Ordinal) != monitors) continue;
                devices.Add(new AudioCaptureDevice(id, source.GetProperty("description").GetString() ?? id));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // An unexpected pactl format leaves only the default device choices.
        }
        return devices;
    }
}
