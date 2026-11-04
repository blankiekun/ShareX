using ShareX.HelpersLib;
using ShareX.HistoryLib;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class HistoryLocalPathTests : IDisposable
{
    private const string Stored = @"C:\Users\someone\Pictures\shot.png";
    private const string Mapped = "/h/Pictures/shot.png";

    private static readonly LinuxFolders Folders = new("/h", "/h/Documents", "/h/Pictures", "/h/Desktop", "/h/Music", "/h/Videos", "/h/Downloads", "/h/Documents/ShareX");

    private readonly string home = Directory.CreateTempSubdirectory("sxhist").FullName;

    public HistoryLocalPathTests()
    {
        Directory.CreateDirectory(Path.Combine(home, "Pictures"));
        HistoryItem.LocalFolders = new LinuxFolders(home, home, Path.Combine(home, "Pictures"), home, home, home, home, home);
    }

    public void Dispose()
    {
        HistoryItem.LocalFolders = null;
        Directory.Delete(home, true);
    }

    private static string Resolve(string stored, params string[] existing) => BackupMigration.ResolveHistoryPath(stored, Folders, path => Array.IndexOf(existing, path) >= 0);

    // Bug: an old row stays unopenable although the file was copied over, or a row with no copy points to a file that isn't there.
    [Fact]
    public void Uses_the_mapped_file_only_when_it_exists()
    {
        Assert.Equal(Mapped, Resolve(Stored, Mapped));
        Assert.Equal(Stored, Resolve(Stored));
    }

    // Bug: the mapped file replaces one that really is at the stored path.
    [Fact]
    public void The_stored_path_wins_when_it_exists()
    {
        Assert.Equal(Stored, Resolve(Stored, Stored, Mapped));
    }

    // Bug: MapWindowsPath takes any drive letter, so D:\Users would land in this home although it was never C:\Users. The others have no mapping at all.
    [Theory]
    [InlineData(@"D:\Users\someone\Pictures\shot.png")]
    [InlineData(@"D:\Screenshots\shot.png")]
    [InlineData(@"\\nas\share\shot.png")]
    [InlineData(@"%USERPROFILE%\Pictures\shot.png")]
    [InlineData(@"C:\Users\Public\Pictures\shot.png")]
    public void Other_places_stay_unmapped(string stored)
    {
        Assert.Equal(stored, Resolve(stored, Mapped));
    }

    // Bug: a row from an imported backup reaches a file outside the home.
    [Theory]
    [InlineData(@"C:\Users\someone\Pictures\..\..\..\..\etc\passwd")]
    [InlineData(@"C:\Users\someone\Pictures/../../../../etc/passwd")]
    [InlineData(@"C:\Users\..\..\etc\passwd")]
    public void A_climbing_path_cannot_leave_the_home(string stored)
    {
        Assert.Equal(stored, BackupMigration.ResolveHistoryPath(stored, Folders, path => throw new Xunit.Sdk.XunitException("asked the disk about " + path)));
    }

    // Bug: scrolling a long history asks the disk about rows that can't be mapped.
    [Fact]
    public void A_linux_path_never_touches_the_disk()
    {
        Assert.Equal("/h/Pictures/shot.png", BackupMigration.ResolveHistoryPath("/h/Pictures/shot.png", Folders, path => throw new Xunit.Sdk.XunitException("asked the disk about " + path)));
    }

    // Bug: every read of LocalPath asks the disk again, which a list reads for each row on each render. Setting FilePath has to decide again.
    [Fact]
    public void An_item_decides_once_until_its_path_changes()
    {
        string file = Path.Combine(home, "Pictures", "shot.png");
        File.WriteAllText(file, "x");
        HistoryItem item = new() { FilePath = Stored };

        Assert.Equal(file, item.LocalPath);

        File.Delete(file);
        Assert.Equal(file, item.LocalPath);

        item.FilePath = Stored;
        Assert.Equal(Stored, item.LocalPath);
    }

    // Bug: rename writes the mapped path into History.db, or leaves the stored name behind so the row loses its file.
    [Fact]
    public void Rename_moves_the_real_file_and_keeps_the_record_in_its_form()
    {
        string file = Path.Combine(home, "Pictures", "shot.png");
        string database = Path.Combine(home, "History.db");
        File.WriteAllText(file, "x");

        using (HistoryManagerSQLite manager = new(database))
        {
            manager.AppendHistoryItem(new HistoryItem { FileName = "shot.png", FilePath = Stored, DateTime = DateTime.Now, Type = "Image" });

            HistoryItem item = manager.GetHistoryItems().Single();
            item.RenameLocalFile("renamed.png");
            manager.Edit(item);

            Assert.False(File.Exists(file));
            Assert.True(File.Exists(Path.Combine(home, "Pictures", "renamed.png")));
            Assert.Equal(Path.Combine(home, "Pictures", "renamed.png"), item.LocalPath);

            HistoryItem saved = manager.GetHistoryItems().Single();
            Assert.Equal(@"C:\Users\someone\Pictures\renamed.png", saved.FilePath);
            Assert.Equal("renamed.png", saved.FileName);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // Bug: a row with a Linux path stops renaming as it did.
    [Fact]
    public void Rename_of_a_linux_path_stores_the_new_path()
    {
        string file = Path.Combine(home, "Pictures", "linux.png");
        File.WriteAllText(file, "x");
        HistoryItem item = new() { FileName = "linux.png", FilePath = file };

        item.RenameLocalFile("new.png");

        Assert.Equal(Path.Combine(home, "Pictures", "new.png"), item.FilePath);
        Assert.Equal(item.FilePath, item.LocalPath);
        Assert.True(File.Exists(item.FilePath));
    }
}
