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
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>
/// A throwaway folder holding the fake host tools, with PATH pointing only there, so the real gpu-screen-recorder can't be
/// reached even if a fake goes missing. PATH is process-wide, so only one test class may use this: xunit runs the tests of a
/// class one at a time.
/// </summary>
internal sealed class FakeHost : IDisposable
{
    /// <summary>What the fake recorder saves. It isn't a real MP4, which is why ffprobe is faked too.</summary>
    public const string Video = "fake video\n";
    public const string HealthyProbe = """{"streams":[{"codec_type":"video","nb_frames":"30","duration":"1.000000","bit_rate":"8000000"}],"format":{"duration":"1.000000"}}""";

    private static readonly string[] Variables = ["PATH", "XDG_RUNTIME_DIR", "LD_LIBRARY_PATH", "FAKE_GSR_MODE", "FAKE_GSR_LOG", "FAKE_GSR_TRIGGER", "FAKE_GETCAP", "FAKE_FFPROBE_JSON", "FAKE_FFPROBE_ERROR"];

    private readonly Dictionary<string, string?> saved = Variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    private readonly string root, bin, logPath, triggerPath;

    public string Work { get; }
    public string Runtime { get; }
    public string OutputPath => Path.Combine(Work, "out.mp4");

    public FakeHost()
    {
        string python = HostTools.FindOnPath("python3") ?? throw new InvalidOperationException("The fake recorder is a Python script, so these tests need python3 on PATH.");
        string setpriv = HostTools.FindOnPath("setpriv") ?? throw new InvalidOperationException("These tests need the real setpriv from util-linux on PATH.");

        root = Directory.CreateTempSubdirectory("sxgsr-").FullName;
        bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        Work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        Runtime = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
        logPath = Path.Combine(root, "recorder.log");
        triggerPath = Path.Combine(root, "trigger");

        foreach (string fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures")))
        {
            string target = Path.Combine(bin, Path.GetFileName(fixture));
            File.Copy(fixture, target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllText(Path.Combine(bin, "gsr-kms-server"), ""); // Only has to exist: the fake getcap answers for it.
        File.CreateSymbolicLink(Path.Combine(bin, "python3"), python);
        File.CreateSymbolicLink(Path.Combine(bin, "setpriv"), setpriv);

        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Runtime);
        Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", null);
        Environment.SetEnvironmentVariable("FAKE_GSR_LOG", logPath);
        Environment.SetEnvironmentVariable("FAKE_GSR_TRIGGER", triggerPath);
        Environment.SetEnvironmentVariable("FAKE_GSR_MODE", "normal");
        Environment.SetEnvironmentVariable("FAKE_GETCAP", null);
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_JSON", HealthyProbe);
        Environment.SetEnvironmentVariable("FAKE_FFPROBE_ERROR", null);
    }

    public RecordingOptions Options() => new()
    {
        OutputPath = OutputPath,
        Region = new Rectangle(2700, 200, 640, 360),
        CaptureSystemAudio = false
    };

    public void SetMode(string mode) => Environment.SetEnvironmentVariable("FAKE_GSR_MODE", mode);

    /// <summary>Takes a fake or a real tool off PATH.</summary>
    public void RemoveTool(string name) => File.Delete(Path.Combine(bin, name));

    /// <summary>Makes a mid-recording mode misbehave now.</summary>
    public void Trigger() => File.WriteAllText(triggerPath, "");

    public IReadOnlyList<JsonElement> Events(string kind)
    {
        if (!File.Exists(logPath)) return [];
        return File.ReadAllLines(logPath)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(entry => entry.GetProperty("event").GetString() == kind)
            .ToList();
    }

    /// <summary>The value after a flag in what the recorder was started with.</summary>
    public string RecorderArgument(string flag)
    {
        string?[] arguments = Events("start").Single().GetProperty("argv").EnumerateArray().Select(argument => argument.GetString()).ToArray();
        return arguments[Array.IndexOf(arguments, flag) + 1]!;
    }

    public void AssertRecorderNeverStarted() => Assert.Empty(Events("start"));

    public void AssertRecorderGone()
    {
        int pid = Events("start").Single().GetProperty("pid").GetInt32();
        Assert.False(Directory.Exists($"/proc/{pid}"), $"The recorder (pid {pid}) is still running.");
    }

    /// <summary>No output, reservation, temporary file, first-frame marker or socket.</summary>
    public void AssertNothingLeft()
    {
        Assert.Empty(Directory.GetFileSystemEntries(Work));
        Assert.Empty(Directory.GetFileSystemEntries(Runtime));
    }

    public void Dispose()
    {
        foreach (JsonElement start in Events("start"))
        {
            // Only reached with a live process when a test failed. These pids come from the fake's own log.
            int pid = start.GetProperty("pid").GetInt32();
            if (Directory.Exists($"/proc/{pid}")) Process.GetProcessById(pid).Kill();
        }
        foreach ((string name, string? value) in saved) Environment.SetEnvironmentVariable(name, value);
        Directory.Delete(root, recursive: true);
    }
}
