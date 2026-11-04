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

using Microsoft.Data.Sqlite;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.Tools;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public sealed class PrivateHistoryAndLogsTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode WorldReadable = Private | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-private-history-").FullName;

    private string History => Path.Combine(_folder, "History.db");
    private string Log => Path.Combine(_folder, "Logs", "ShareX-Log.txt");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, true);
    }

    private static HistoryItem Item(string name) => new()
    {
        FileName = name,
        FilePath = "/home/me/" + name,
        DateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local),
        Type = "Image",
        URL = "https://i.example/" + name,
        DeletionURL = "https://i.example/d/" + name
    };

    // Bug: SQLite creates History.db with the default mode, so every local user can read the deletion URLs in it.
    [Fact]
    public void A_new_history_database_is_private_and_works()
    {
        using HistoryManagerSQLite manager = new(History);

        Assert.True(manager.AppendHistoryItem(Item("a.png")));

        Assert.Equal(Private, File.GetUnixFileMode(History));
        Assert.Equal("a.png", manager.GetHistoryItems().Single().FileName);
    }

    // Bug: a database from before this fix stays 0644, or only the main file is tightened and the deletion URLs in an
    // uncheckpointed -wal, which a crash leaves behind, stay readable.
    [Fact]
    public void An_existing_history_database_and_its_sidecars_are_tightened_on_open()
    {
        using (HistoryManagerSQLite first = new(History))
        {
            first.AppendHistoryItem(Item("before.png"));
        }

        using SqliteConnection crashed = new($"Data Source={History};Pooling=False");
        crashed.Open();
        using SqliteCommand command = crashed.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO History (FileName, DateTime) VALUES ('wal.png', '2026-01-02T00:00:00');";
        command.ExecuteNonQuery();
        string[] files = [History, History + "-wal", History + "-shm"];

        foreach (string file in files)
        {
            File.SetUnixFileMode(file, WorldReadable);
        }

        using HistoryManagerSQLite manager = new(History);

        Assert.All(files, file => Assert.Equal(Private, File.GetUnixFileMode(file)));
        Assert.Equal(["before.png", "wal.png"], manager.GetHistoryItems().Select(item => item.FileName));
    }

    // Bug: File.AppendAllText creates the log with the default mode, and the destinations and URLs in it become readable by every local user.
    [Fact]
    public void A_new_log_file_is_private()
    {
        Logger logger = new(Log) { AsyncWrite = false };

        logger.WriteLine("first");

        Assert.Equal(Private, File.GetUnixFileMode(Log));
    }

    // Bug: this month's log from before this fix stays 0644 until next month, or tightening it opens it in a way that loses what it holds.
    [Fact]
    public void An_existing_log_file_is_tightened_and_keeps_its_contents()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
        File.WriteAllText(Log, "earlier\n");
        File.SetUnixFileMode(Log, WorldReadable);

        Logger logger = new(Log) { AsyncWrite = false };

        Assert.Equal(Private, File.GetUnixFileMode(Log));

        logger.WriteLine("later");

        string contents = File.ReadAllText(Log);
        Assert.StartsWith("earlier\n", contents);
        Assert.Contains("later", contents);
    }

    // Bug: the network monitor writes its own log with File.WriteAllText, so a new or an older one stays readable by every local user,
    // and clearing a log that doesn't exist yet creates it the same way.
    [Fact]
    public async Task The_network_monitor_log_is_private_however_it_comes_to_exist()
    {
        string cleared = Path.Combine(_folder, "Logs", "cleared.log");
        string older = Path.Combine(_folder, "older.log");
        File.WriteAllText(older, "");
        File.SetUnixFileMode(older, WorldReadable);

        Assert.True(await new NetworkMonitorEventLog(cleared).ClearAsync(CancellationToken.None));
        Assert.True(new NetworkMonitorEventLog(older).EnsureFileExists());

        Assert.Equal(Private, File.GetUnixFileMode(cleared));
        Assert.Equal(Private, File.GetUnixFileMode(older));
    }
}
