using Microsoft.Data.Sqlite;
using ShareX.HistoryLib;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class HistoryMergeTests : IDisposable
{
    // Spelled out here rather than taken from HistoryManagerSQLite: the file being imported was written by Windows ShareX, not by this code.
    private const string WindowsSchema = """
        CREATE TABLE History (Id INTEGER PRIMARY KEY AUTOINCREMENT, FileName TEXT, FilePath TEXT, DateTime TEXT, Type TEXT, Host TEXT,
        URL TEXT, ThumbnailURL TEXT, DeletionURL TEXT, ShortenedURL TEXT, Tags TEXT);
        """;

    private readonly string folder = Directory.CreateTempSubdirectory("sxm8").FullName;
    private string Target => Path.Combine(folder, "History.db");
    private string Source => Path.Combine(folder, "Windows.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, true);
    }

    private void MakeSource(params string[] inserts)
    {
        using SqliteConnection connection = new($"Data Source={Source};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = WindowsSchema + string.Concat(inserts);
        command.ExecuteNonQuery();
    }

    private static string Row(string name, string date, string tags = "'{\"ProcessName\":\"explorer\"}'") =>
        $"INSERT INTO History (FileName, FilePath, DateTime, Type, Host, URL, ThumbnailURL, DeletionURL, ShortenedURL, Tags) VALUES " +
        $"('{name}', 'C:\\Users\\me\\Pictures\\{name}', '{date}', 'Image', 'Imgur', 'https://i.example/{name}', NULL, 'https://i.example/d/{name}', '', {tags});";

    private static HistoryItem Local(string name, DateTime date) => new()
    {
        FileName = name,
        FilePath = "/home/me/" + name,
        DateTime = date,
        Type = "Image",
        Host = "Imgur",
        URL = "https://i.example/" + name
    };

    // Bug: merging replaces either side, or rewrites a stored path, or a second import of the same file doubles every row.
    [Fact]
    public void Merge_keeps_both_sides_and_a_second_merge_adds_nothing()
    {
        MakeSource(Row("win1.png", "2024-03-01T10:00:00.0000000+01:00"), Row("win2.png", "2024-03-02T10:00:00.0000000+01:00"));

        using HistoryManagerSQLite manager = new(Target);
        manager.AppendHistoryItem(Local("linux.png", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));

        HistoryMergeResult first = manager.MergeFrom(Source);
        HistoryMergeResult second = manager.MergeFrom(Source);

        Assert.Equal(new HistoryMergeResult(2, 0, 0), first);
        Assert.Equal(new HistoryMergeResult(0, 2, 0), second);

        List<HistoryItem> items = manager.GetHistoryItems();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.FileName == "linux.png");
        Assert.Equal(@"C:\Users\me\Pictures\win1.png", items.Single(item => item.FileName == "win1.png").FilePath);
        Assert.Equal("explorer", items.Single(item => item.FileName == "win1.png").Tags["ProcessName"]);
    }

    // Bug: old Windows rows get the newest ids, so a year-old screenshot tops the history window, which shows the list reversed.
    [Fact]
    public void Merged_rows_sort_by_their_date()
    {
        MakeSource(Row("old.png", "2020-01-01T10:00:00.0000000+00:00"), Row("newest.png", "2027-01-01T10:00:00.0000000+00:00"));

        using HistoryManagerSQLite manager = new(Target);
        manager.AppendHistoryItem(Local("middle.png", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Local)));
        manager.MergeFrom(Source);

        Assert.Equal(new[] { "old.png", "middle.png", "newest.png" }, manager.GetHistoryItems().Select(item => item.FileName));
    }

    // Bug: a row with a bad date or bad tags gets in, and Load, which parses every row, then loses the whole history.
    [Fact]
    public void Rows_that_could_not_be_loaded_stay_out()
    {
        MakeSource(Row("good.png", "2024-03-01T10:00:00.0000000+01:00"), Row("baddate.png", "not a date"), Row("badtags.png", "2024-03-01T10:00:00", "'{nope'"),
            "INSERT INTO History (FileName, DateTime) VALUES (x'00FF', '2024-03-01T10:00:00');");

        using HistoryManagerSQLite manager = new(Target);
        HistoryMergeResult result = manager.MergeFrom(Source);

        Assert.Equal(new HistoryMergeResult(1, 0, 3), result);
        Assert.Equal("good.png", manager.GetHistoryItems().Single().FileName);
    }

    // Bug: a row with no tags, which upstream writes as NULL, is treated as unreadable or collides with one that has empty text.
    [Fact]
    public void Null_and_empty_columns_merge_and_stay_distinct()
    {
        MakeSource(Row("a.png", "2024-03-01T10:00:00", "NULL"), Row("a.png", "2024-03-01T10:00:00", "'{}'"));

        using HistoryManagerSQLite manager = new(Target);

        Assert.Equal(new HistoryMergeResult(2, 0, 0), manager.MergeFrom(Source));
        Assert.Equal(new HistoryMergeResult(0, 2, 0), manager.MergeFrom(Source));
    }

    // Bug: a file that isn't a history, or a view posing as one, gets read, and the existing history is left half changed.
    [Theory]
    [InlineData("not sqlite")]
    [InlineData("CREATE TABLE Other (x TEXT);")]
    [InlineData("CREATE TABLE Other (x TEXT); CREATE VIEW History AS SELECT x AS FileName FROM Other;")]
    [InlineData("CREATE TABLE History (FileName TEXT);")]
    public void A_bad_source_throws_and_changes_nothing(string content)
    {
        if (content == "not sqlite")
        {
            File.WriteAllText(Source, new string('x', 8192));
        }
        else
        {
            using SqliteConnection connection = new($"Data Source={Source};Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = content;
            command.ExecuteNonQuery();
        }

        using HistoryManagerSQLite manager = new(Target);
        manager.AppendHistoryItem(Local("linux.png", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));

        Assert.ThrowsAny<Exception>(() => manager.MergeFrom(Source));
        Assert.Equal("linux.png", manager.GetHistoryItems().Single().FileName);
    }
}
