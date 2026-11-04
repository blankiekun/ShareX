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
using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShareX.HistoryLib
{
    public readonly record struct HistoryMergeResult(int Added, int AlreadyPresent, int Unreadable);

    public class HistoryManagerSQLite : HistoryManager, IDisposable
    {
        private static readonly string[] mergedColumns = { "FileName", "FilePath", "DateTime", "Type", "Host", "URL", "ThumbnailURL", "DeletionURL", "ShortenedURL", "Tags" };

        private SqliteConnection connection;

        public HistoryManagerSQLite(string filePath) : base(filePath)
        {
            Connect(filePath);
            EnsureDatabase();
        }

        private void Connect(string filePath)
        {
            FileHelpers.CreateDirectoryFromFilePath(filePath);

            // Sandbox mode has no path, and SQLite then keeps the database in a temporary file of its own.
            if (!string.IsNullOrEmpty(filePath))
            {
                MakeDatabasePrivate(filePath);
            }

            string connectionString = $"Data Source={filePath}";
            connection = new SqliteConnection(connectionString);
            connection.Open();

            SetBusyTimeout(5000);
        }

        // Deletion URLs live here. SQLite would create the file with the default mode, and gives the sidecar files it creates the mode of
        // the database, so an empty file of our own settles both. Sidecars left by a crash keep the mode they were created with.
        private static void MakeDatabasePrivate(string filePath)
        {
            if (!File.Exists(filePath))
            {
                using FileStream _ = new FileStream(filePath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.Write,
                    UnixCreateMode = FileHelpers.PrivateFileMode
                });
            }

            foreach (string path in new[] { filePath, filePath + "-wal", filePath + "-shm", filePath + "-journal" })
            {
                if (File.Exists(path))
                {
                    FileHelpers.TryMakePrivate(path);
                }
            }
        }

        private void SetBusyTimeout(int milliseconds)
        {
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA busy_timeout = {milliseconds};";
                cmd.ExecuteNonQuery();
            }
        }

        private void EnsureDatabase()
        {
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS History (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    FileName TEXT,
    FilePath TEXT,
    DateTime TEXT,
    Type TEXT,
    Host TEXT,
    URL TEXT,
    ThumbnailURL TEXT,
    DeletionURL TEXT,
    ShortenedURL TEXT,
    Tags TEXT
);
";
                cmd.ExecuteNonQuery();
            }
        }

        internal override List<HistoryItem> Load(string dbPath)
        {
            List<HistoryItem> items = new List<HistoryItem>();

            using (SqliteCommand cmd = new SqliteCommand("SELECT * FROM History;", connection))
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    HistoryItem item = new HistoryItem()
                    {
                        Id = (long)reader["Id"],
                        FileName = reader["FileName"].ToString(),
                        FilePath = reader["FilePath"].ToString(),
                        DateTime = DateTime.Parse(reader["DateTime"].ToString()),
                        Type = reader["Type"].ToString(),
                        Host = reader["Host"].ToString(),
                        URL = reader["URL"].ToString(),
                        ThumbnailURL = reader["ThumbnailURL"].ToString(),
                        DeletionURL = reader["DeletionURL"].ToString(),
                        ShortenedURL = reader["ShortenedURL"].ToString(),
                        Tags = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader["Tags"]?.ToString() ?? "{}")
                    };

                    items.Add(item);
                }
            }

            // By date, not row order: merged rows get new ids however old they are, and the windows reverse this list to put the newest first.
            return items.OrderBy(item => item.DateTime).ToList();
        }

        // Adds the rows of another History.db, which may come from another machine. Values are copied as stored, so paths stay as written
        // and a second merge of the same file finds every row already here. Rows are compared on every column but Id, so two rows
        // identical in all of them count as one. All of it lands or none does.
        public HistoryMergeResult MergeFrom(string sourceFilePath)
        {
            SqliteConnectionStringBuilder sourceConnection = new SqliteConnectionStringBuilder()
            {
                DataSource = sourceFilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            using (SqliteConnection source = new SqliteConnection(sourceConnection.ToString()))
            {
                source.Open();
                CheckMergeSource(source);

                string columns = string.Join(", ", mergedColumns);
                HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);

                using (SqliteCommand cmd = new SqliteCommand($"SELECT {columns} FROM History;", connection))
                using (SqliteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (TryReadMergeRow(reader, out string[] row))
                        {
                            known.Add(GetMergeKey(row));
                        }
                    }
                }

                int added = 0, alreadyPresent = 0, unreadable = 0;

                using (SqliteTransaction transaction = connection.BeginTransaction())
                using (SqliteCommand insert = connection.CreateCommand())
                using (SqliteCommand select = new SqliteCommand($"SELECT {columns} FROM History;", source))
                using (SqliteDataReader reader = select.ExecuteReader())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = $"INSERT INTO History ({columns}) VALUES ({string.Join(", ", mergedColumns.Select(column => "@" + column))});";
                    SqliteParameter[] parameters = mergedColumns.Select(column => insert.Parameters.Add("@" + column, SqliteType.Text)).ToArray();

                    while (reader.Read())
                    {
                        if (!TryReadMergeRow(reader, out string[] row) || !IsLoadableMergeRow(row))
                        {
                            unreadable++;
                        }
                        else if (!known.Add(GetMergeKey(row)))
                        {
                            alreadyPresent++;
                        }
                        else
                        {
                            for (int i = 0; i < row.Length; i++)
                            {
                                parameters[i].Value = row[i] ?? (object)DBNull.Value;
                            }

                            insert.ExecuteNonQuery();
                            added++;
                        }
                    }

                    transaction.Commit();
                }

                return new HistoryMergeResult(added, alreadyPresent, unreadable);
            }
        }

        // The file came from elsewhere: nothing but a plain, intact History table gets read.
        private static void CheckMergeSource(SqliteConnection source)
        {
            string RunScalar(string sql)
            {
                using (SqliteCommand cmd = new SqliteCommand(sql, source))
                {
                    return cmd.ExecuteScalar()?.ToString();
                }
            }

            RunScalar("PRAGMA trusted_schema = OFF;");

            if (RunScalar("PRAGMA quick_check(1);") != "ok")
            {
                throw new InvalidDataException("The history file is damaged.");
            }

            if (RunScalar("SELECT type FROM sqlite_master WHERE name = 'History' COLLATE NOCASE;") != "table")
            {
                throw new InvalidDataException("The file has no history table.");
            }

            HashSet<string> columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (SqliteCommand cmd = new SqliteCommand("SELECT name FROM pragma_table_info('History');", source))
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            if (!mergedColumns.All(columns.Contains))
            {
                throw new InvalidDataException("The history table has unexpected columns.");
            }
        }

        // False for a value that isn't text, which History.db never stores itself.
        private static bool TryReadMergeRow(SqliteDataReader reader, out string[] row)
        {
            row = new string[mergedColumns.Length];

            for (int i = 0; i < row.Length; i++)
            {
                if (reader.IsDBNull(i))
                {
                    continue;
                }

                if (reader.GetValue(i) is not string text)
                {
                    return false;
                }

                row[i] = text;
            }

            return true;
        }

        // Load parses these two for every row and one failure loses the whole list, so a bad row can't be let in.
        private static bool IsLoadableMergeRow(string[] row)
        {
            if (!DateTime.TryParse(row[Array.IndexOf(mergedColumns, "DateTime")], out _))
            {
                return false;
            }

            try
            {
                JsonConvert.DeserializeObject<Dictionary<string, string>>(row[Array.IndexOf(mergedColumns, "Tags")] ?? "{}");
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string GetMergeKey(string[] row)
        {
            return string.Join('\u001f', row.Select(value => value ?? "\u0000"));
        }

        protected override bool Append(string dbPath, IEnumerable<HistoryItem> historyItems)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                foreach (HistoryItem item in historyItems)
                {
                    using (SqliteCommand cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = @"
INSERT INTO History
(FileName, FilePath, DateTime, Type, Host, URL, ThumbnailURL, DeletionURL, ShortenedURL, Tags)
VALUES (@FileName, @FilePath, @DateTime, @Type, @Host, @URL, @ThumbnailURL, @DeletionURL, @ShortenedURL, @Tags);
SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("@FileName", item.FileName ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@FilePath", item.FilePath ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@DateTime", item.DateTime.ToString("o") ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Type", item.Type ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Host", item.Host ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@URL", item.URL ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@ThumbnailURL", item.ThumbnailURL ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@DeletionURL", item.DeletionURL ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@ShortenedURL", item.ShortenedURL ?? (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@Tags", item.Tags != null ? JsonConvert.SerializeObject(item.Tags) : (object)DBNull.Value);
                        item.Id = (long)cmd.ExecuteScalar();
                    }
                }

                transaction.Commit();
            }

            return true;
        }

        public void Edit(HistoryItem item)
        {
            using (SqliteTransaction transaction = connection.BeginTransaction())
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE History SET
FileName = @FileName,
FilePath = @FilePath,
DateTime = @DateTime,
Type = @Type,
Host = @Host,
URL = @URL,
ThumbnailURL = @ThumbnailURL,
DeletionURL = @DeletionURL,
ShortenedURL = @ShortenedURL,
Tags = @Tags
WHERE Id = @Id;";
                cmd.Parameters.AddWithValue("@FileName", item.FileName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@FilePath", item.FilePath ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@DateTime", item.DateTime.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Type", item.Type ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Host", item.Host ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@URL", item.URL ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ThumbnailURL", item.ThumbnailURL ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@DeletionURL", item.DeletionURL ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@ShortenedURL", item.ShortenedURL ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Tags", item.Tags != null ? JsonConvert.SerializeObject(item.Tags) : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", item.Id);
                cmd.ExecuteNonQuery();

                transaction.Commit();
            }
        }

        public void Delete(params HistoryItem[] items)
        {
            if (items != null && items.Length > 0)
            {
                using (SqliteTransaction transaction = connection.BeginTransaction())
                using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM History WHERE Id = @Id;";
                    SqliteParameter idParam = cmd.CreateParameter();
                    idParam.ParameterName = "@Id";
                    cmd.Parameters.Add(idParam);

                    foreach (HistoryItem item in items)
                    {
                        idParam.Value = item.Id;
                        cmd.ExecuteNonQuery();
                    }

                    transaction.Commit();
                }
            }
        }

        public void MigrateFromJSON(string jsonFilePath)
        {
            HistoryManagerJSON jsonManager = new HistoryManagerJSON(jsonFilePath);
            List<HistoryItem> items = jsonManager.Load(jsonFilePath);

            if (items.Count > 0)
            {
                Append(items);
            }
        }

        public void Dispose()
        {
            if (connection != null)
            {
                connection.Close();
                connection.Dispose();
                SqliteConnection.ClearPool(connection);
            }
        }
    }
}
