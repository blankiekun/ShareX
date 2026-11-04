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
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public sealed class PrivateConfigFilesTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateFolder = Private | UnixFileMode.UserExecute;
    private const UnixFileMode WorldReadable = Private | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-private-config-").FullName;

    private string ConfigPath => Path.Combine(_folder, "Config.json");
    private string BackupFolder => Path.Combine(_folder, "Backup");

    public void Dispose() => Directory.Delete(_folder, true);

    public class Settings : SettingsBase<Settings>
    {
        public string Header { get; set; } = "Authorization: Bearer key";
    }

    public class UnsavableSettings : SettingsBase<UnsavableSettings>
    {
        public string Header
        {
            get => throw new InvalidOperationException("can't be encrypted");
            set { }
        }
    }

    private static void WriteWorldReadable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, WorldReadable);
    }

    // Bug: a first save leaves an API key in a file every local user can read.
    [Fact]
    public void A_new_config_file_is_private()
    {
        Assert.True(new Settings().Save(ConfigPath));

        Assert.Equal(Private, File.GetUnixFileMode(ConfigPath));
    }

    // Bug: FileMode.Create reuses a 0644 temp file left by a crash, so the mode passed for a new file is ignored and the secrets are
    // written into a readable file. A chmod after the rename would also fail here, because the failed rename leaves the temp file to check.
    [Fact]
    public void The_temp_file_is_private_before_the_rename_even_over_a_leftover()
    {
        Directory.CreateDirectory(ConfigPath);
        string temp = ConfigPath + ".temp";
        WriteWorldReadable(temp, "{}");

        Assert.False(new Settings().Save(ConfigPath));

        Assert.Equal(Private, File.GetUnixFileMode(temp));
        Assert.Contains("Bearer", File.ReadAllText(temp));
    }

    // Bug: the first save after this fix makes the config 0600 but renames the old 0644 file to Backup/, where the same secrets stay readable,
    // and older weekly copies, which are never rewritten, stay readable until they are cleaned up.
    [Fact]
    public void An_existing_config_and_its_backups_are_tightened_on_save()
    {
        Directory.CreateDirectory(BackupFolder);
        WriteWorldReadable(ConfigPath, """{ "Header": "old" }""");
        WriteWorldReadable(Path.Combine(BackupFolder, "Config.json"), """{ "Header": "older" }""");
        string oldWeekly = Path.Combine(BackupFolder, "Config-2020-01-W01.json");
        WriteWorldReadable(oldWeekly, """{ "Header": "oldest" }""");

        Settings settings = Settings.Load(ConfigPath, BackupFolder);
        settings.CreateBackup = true;
        settings.CreateWeeklyBackup = true;

        Assert.True(settings.Save());

        Assert.Equal(Private, File.GetUnixFileMode(ConfigPath));
        string backup = Path.Combine(BackupFolder, "Config.json");
        Assert.Contains("\"old\"", File.ReadAllText(backup));
        Assert.Equal(Private, File.GetUnixFileMode(backup));
        Assert.Equal(PrivateFolder, File.GetUnixFileMode(BackupFolder));
        Assert.All(Directory.GetFiles(BackupFolder).Where(file => file != oldWeekly), file => Assert.Equal(Private, File.GetUnixFileMode(file)));
        Assert.Equal(3, Directory.GetFiles(BackupFolder).Length);
        Assert.False(File.Exists(ConfigPath + ".temp"));
    }

    // Bug: clearing the leftover before serializing throws away the temp file Load falls back to, on a save that then writes nothing.
    [Fact]
    public void A_save_that_cannot_serialize_keeps_the_leftover_temp_file()
    {
        string temp = ConfigPath + ".temp";
        WriteWorldReadable(temp, """{ "Header": "recovered" }""");

        Assert.False(new UnsavableSettings().Save(ConfigPath));

        Assert.Contains("recovered", File.ReadAllText(temp));
    }
}
