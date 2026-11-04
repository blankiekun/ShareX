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

using ShareX.HelpersLib;
using System.Text;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public sealed class HostSoundPlayerTests : IDisposable
{
    private static readonly byte[] Sound = Encoding.ASCII.GetBytes("RIFF not really a sound");

    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-sound-test").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private string Log(string name) => Path.Combine(_folder, name + ".log");

    /// <summary>A player that records how it was called and what it could read: argument count, path, file mode, contents.</summary>
    private void FakePlayer(string name, string lastLine = "exit 0")
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" +
            $"{{ printf '%s\\n' \"$#\" \"$1\"; stat -c %a \"$1\"; cat \"$1\"; }} > '{Log(name)}.part'\n" +
            $"mv '{Log(name)}.part' '{Log(name)}'\n" +
            lastLine + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Catches the embedded sound being left in the temp folder after each play, readable by other users, or written incompletely.
    [Fact]
    public async Task PlayStream_GivesThePlayerAPrivateCopyAndRemovesIt()
    {
        FakePlayer("pw-play");
        MemoryStream stream = new(Sound);

        await HostSoundPlayer.PlayStreamAsync(stream, _folder);

        string[] log = File.ReadAllLines(Log("pw-play"));
        Assert.Equal("1", log[0]);
        Assert.Equal("600", log[2]);
        Assert.Equal(Encoding.ASCII.GetString(Sound), log[3]);
        Assert.False(File.Exists(log[1]));
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    // Catches a path being joined into a command line, where a custom sound with a space in its name reaches the player as two
    // arguments, and the user's own file being removed like a temporary one.
    [Fact]
    public async Task PlayFile_PassesThePathAsOneArgumentAndKeepsTheFile()
    {
        FakePlayer("pw-play");
        string sound = Path.Combine(_folder, "my sound; touch bad.wav");
        File.WriteAllBytes(sound, Sound);

        await HostSoundPlayer.PlayFileAsync(sound, _folder);

        string[] log = File.ReadAllLines(Log("pw-play"));
        Assert.Equal("1", log[0]);
        Assert.Equal(sound, log[1]);
        Assert.True(File.Exists(sound));
    }

    // Catches paplay being preferred, or tried as well, when pw-play worked: the sound would play twice.
    [Fact]
    public async Task Play_UsesPwPlayFirstAndOnlyOnce()
    {
        FakePlayer("pw-play");
        FakePlayer("paplay");

        await HostSoundPlayer.PlayStreamAsync(new MemoryStream(Sound), _folder);

        Assert.True(File.Exists(Log("pw-play")));
        Assert.False(File.Exists(Log("paplay")));
    }

    // Catches silence on a PulseAudio desktop: pw-play is often installed there as a dependency, and fails because no PipeWire server runs.
    [Fact]
    public async Task Play_FallsBackToPaplayWhenPwPlayFails()
    {
        FakePlayer("pw-play", "echo no server >&2; exit 1");
        FakePlayer("paplay");

        await HostSoundPlayer.PlayStreamAsync(new MemoryStream(Sound), _folder);

        Assert.True(File.Exists(Log("paplay")));
    }

    // Catches a hung player keeping its process and temp file forever, and fast repeats (the clicker game) stacking up players.
    // Also catches the fallback taking the stopped player for a failed one and playing the old sound again through paplay.
    [Fact]
    public async Task Play_NewSoundStopsTheLastOneAndCleansUpAfterIt()
    {
        FakePlayer("pw-play", "exec sleep 30");
        FakePlayer("paplay");
        Task first = HostSoundPlayer.PlayStreamAsync(new MemoryStream(Sound), _folder);

        for (int i = 0; i < 500 && !File.Exists(Log("pw-play")); i++)
        {
            await Task.Delay(10);
        }

        string firstFile = File.ReadAllLines(Log("pw-play"))[1];
        FakePlayer("pw-play");

        await HostSoundPlayer.PlayStreamAsync(new MemoryStream(Sound), _folder);

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(firstFile));
        Assert.False(File.Exists(Log("paplay")));
    }

    // Catches a missing player reaching the user as an exception or error box.
    [Fact]
    public async Task Play_WithNoPlayerIsSilent()
    {
        await HostSoundPlayer.PlayStreamAsync(new MemoryStream(Sound), _folder);
        await HostSoundPlayer.PlayFileAsync(Path.Combine(_folder, "any.wav"), null);
    }
}
