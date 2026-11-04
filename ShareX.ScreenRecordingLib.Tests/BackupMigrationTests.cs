using Newtonsoft.Json.Linq;
using ShareX.HelpersLib;
using ShareX.UploadersLib;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class BackupMigrationTests
{
    private const string TaggedBlob = "$DPAPIEncrypted$AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAfakeblobfakeblob==";
    private const string UntaggedBlob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAAfakeblobfakeblob==";

    private static readonly LinuxFolders Folders = new("/h", "/h/Documents", "/h/Pictures", "/h/Desktop", "/h/Music", "/h/Videos", "/h/Downloads", "/h/Documents/ShareX");
    private static readonly HashSet<string> Secrets = BackupMigration.GetSecretPropertyNames(typeof(UploadersConfig).Assembly, typeof(HelpersOptions).Assembly);

    private static BackupMigrationReport Migrate(JObject root, Func<string, bool>? canDecrypt = null)
    {
        BackupMigrationReport report = new();
        BackupMigration.Migrate(root, Secrets, Folders, canDecrypt ?? (_ => false), report);
        return report;
    }

    // Bug: a blob that lost its tag, or one still tagged, is taken for a real credential and sent to the server.
    [Theory]
    [InlineData(TaggedBlob, true)]
    [InlineData(UntaggedBlob, true)]
    [InlineData("$ShareXEncrypted$abcdef", false)]
    [InlineData("ghp_madeuptoken", false)]
    [InlineData("", false)]
    public void Recognises_dpapi_blobs(string value, bool expected)
    {
        Assert.Equal(expected, BackupMigration.IsDpapiBlob(value));
    }

    // Bug: a blob stays in the settings, or the user isn't told by name which destination needs signing in again.
    [Fact]
    public void Blobs_are_cleared_and_named_by_destination()
    {
        JObject root = JObject.Parse($$"""
        {
            "ImgurOAuth2Info": { "Client_ID": "id", "Client_Secret": "{{TaggedBlob}}", "Token": { "access_token": "{{TaggedBlob}}", "refresh_token": "{{UntaggedBlob}}" } },
            "VgymeUserKey": "{{TaggedBlob}}",
            "FTPAccountList": [ { "Name": "My server", "Password": "{{TaggedBlob}}" }, { "Name": "Other", "Password": "plainpass" } ]
        }
        """);

        BackupMigrationReport report = Migrate(root);

        Assert.DoesNotContain("AQAAANCM", root.ToString());
        Assert.Equal(new[] { "FTP \"My server\"", "Imgur", "vgy.me" }, report.SignInAgain);
        Assert.Equal("", (string?)root.SelectToken("ImgurOAuth2Info.Token.access_token"));
        // The account next to it was a plaintext one and crosses untouched.
        Assert.Equal("plainpass", (string?)root.SelectToken("FTPAccountList[1].Password"));
        Assert.Contains("FTP \"Other\"", report.SecretsImported);
        Assert.True(report.HasPlaintextSecrets);
    }

    // Bug: a new destination's secret reaches the user as a raw property name such as "NewThingAPIKey".
    [Fact]
    public void Every_secret_in_the_uploaders_config_has_a_destination_name()
    {
        foreach (PropertyInfo property in typeof(UploadersConfig).GetProperties())
        {
            bool holdsSecret = property.IsDefined(typeof(JsonEncryptAttribute)) ||
                (property.PropertyType.Namespace?.StartsWith("ShareX") == true && property.PropertyType.GetProperties().Any(p => p.IsDefined(typeof(JsonEncryptAttribute))) &&
                !property.PropertyType.IsEnum);

            if (holdsSecret)
            {
                JObject root = new() { [property.Name] = "x" };
                Assert.NotEqual(property.Name, BackupMigration.GetDestinationName(root[property.Name]!));
            }
        }
    }

    // Bug: a password that looks like a path gets rewritten, or worse, its value lands in the report and the log.
    [Fact]
    public void A_plaintext_secret_is_never_treated_as_a_path()
    {
        JObject root = JObject.Parse("""{ "EmailPassword": "C:\\Users\\me\\hunter2" }""");

        BackupMigrationReport report = Migrate(root);

        Assert.Equal(@"C:\Users\me\hunter2", (string?)root["EmailPassword"]);
        Assert.Empty(report.PathsMapped);
        Assert.Empty(report.PathsReset);
    }

    // Bug: a backup from a machine with a different wallet key loads as garbage. One from this machine must not be cleared.
    [Fact]
    public void A_value_encrypted_here_stays_and_one_from_another_wallet_is_cleared()
    {
        JObject mine = JObject.Parse("""{ "VgymeUserKey": "$ShareXEncrypted$mine" }""");
        JObject theirs = JObject.Parse("""{ "VgymeUserKey": "$ShareXEncrypted$theirs" }""");

        BackupMigrationReport mineReport = Migrate(mine, _ => true);
        BackupMigrationReport theirsReport = Migrate(theirs, _ => false);

        Assert.Equal("$ShareXEncrypted$mine", (string?)mine["VgymeUserKey"]);
        Assert.Empty(mineReport.SignInAgain);
        Assert.False(mineReport.HasPlaintextSecrets);
        Assert.True(mineReport.NeedsSecretStorage);
        Assert.Equal("", (string?)theirs["VgymeUserKey"]);
        Assert.Contains("vgy.me", theirsReport.SignInAgain);
    }

    // Bug: a settings folder copied from Windows uploads with "$DPAPIEncrypted$..." as the credential.
    [Fact]
    public void Loading_settings_clears_a_windows_blob()
    {
        UploadersConfig config = UploadersConfig.LoadFromJson($$"""{ "VgymeUserKey": "{{TaggedBlob}}" }""");

        Assert.Equal("", config.VgymeUserKey);
    }

    // Bug: a backup is treated as encrypted when it isn't, so the warning before export would be wrong. Uses no wallet.
    [Fact]
    public void An_export_stream_holds_secrets_in_plain_text()
    {
        UploadersConfig config = new() { VgymeUserKey = "made-up-token" };

        using MemoryStream stream = config.SaveToMemoryStream(false);

        Assert.Contains("made-up-token", Encoding.UTF8.GetString(stream.ToArray()));
    }

    // Bug: an export replacing a world-readable file, or created with the default mode, can be read by other users.
    [Fact]
    public void An_archive_created_with_a_mode_gets_it_even_over_an_existing_file()
    {
        string folder = Directory.CreateTempSubdirectory("sxm8").FullName;

        try
        {
            string archive = Path.Combine(folder, "a.sxb");
            File.WriteAllText(archive, "old");
            File.SetUnixFileMode(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            ZipManager.Compress(archive, new List<ZipEntryInfo> { new(new MemoryStream(new byte[] { 1 }), "x.json") },
                unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive));
            using ZipArchive zip = ZipFile.OpenRead(archive);
            Assert.Equal("x.json", zip.Entries.Single().FullName);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    // Bug: a wrong table entry sends screenshots to the wrong place, or a junk folder is created under the Linux home.
    [Theory]
    [InlineData(@"C:\Users\me\Pictures", "/h/Pictures")]
    [InlineData(@"c:\users\ME\pictures\Shots\2026", "/h/Pictures/Shots/2026")]
    [InlineData(@"D:/Users/me/Videos/x", "/h/Videos/x")]
    [InlineData(@"C:\Users\me\Documents\ShareX\Screenshots", "/h/Documents/ShareX/Screenshots")]
    [InlineData(@"C:\Users\me\Documents\Notes", "/h/Documents/Notes")]
    [InlineData(@"C:\Users\me\OneDrive\Pictures\Shots", "/h/Pictures/Shots")]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\Documents\ShareX", "/h/Documents/ShareX")]
    [InlineData(@"C:\Users\me", "/h")]
    [InlineData(@"C:\Users\me\Downloads\", "/h/Downloads")]
    [InlineData(@"%USERPROFILE%\Pictures\Shots", "/h/Pictures/Shots")]
    [InlineData(@"%userprofile%\Desktop", "/h/Desktop")]
    [InlineData(@"%MyPictures%\Shots", "/h/Pictures/Shots")]
    [InlineData(@"%ShareX%\Screenshots", "/h/Documents/ShareX/Screenshots")]
    [InlineData(@"%MyDocuments%\ShareX\Screenshots", "/h/Documents/ShareX/Screenshots")]
    public void Maps_obvious_windows_folders(string windows, string expected)
    {
        Assert.True(BackupMigration.IsWindowsPath(windows));
        Assert.Equal(expected, BackupMigration.MapWindowsPath(windows, Folders));
    }

    // Bug: guessing a home for these invents folders, and a climbing path could point anywhere on the disk.
    [Theory]
    [InlineData(@"D:\Screenshots")]
    [InlineData(@"\\nas\share\shots")]
    [InlineData(@"C:\Program Files\paint.net\PaintDotNet.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\x")]
    [InlineData(@"C:\Users\Public\Pictures")]
    [InlineData(@"C:\Users\me\Pictures\..\..\..\etc")]
    [InlineData(@"%ProgramFiles%\x.exe")]
    [InlineData(@"%APPDATA%\ShareX")]
    [InlineData(@"%USERPROFILE%\Foo\Bar")]
    [InlineData(@"%windir%/system32/mspaint.exe")]
    public void Has_no_mapping_for_other_windows_paths(string windows)
    {
        Assert.True(BackupMigration.IsWindowsPath(windows));
        Assert.Null(BackupMigration.MapWindowsPath(windows, Folders));
    }

    // Bug: a name pattern, a URL or a path Linux ShareX wrote itself is taken for a Windows path and reset on a Linux-to-Linux restore.
    [Theory]
    [InlineData("%y-%mo")]
    [InlineData(@"%y\%mo")]
    [InlineData("ShareX/%y/%mo")]
    [InlineData("https://example.com/a")]
    [InlineData("/home/me/Pictures")]
    [InlineData("%MyPictures%/Shots")]
    [InlineData(@"\\d+ matches digits")]
    [InlineData("hello")]
    public void Leaves_what_is_not_a_windows_path(string value)
    {
        Assert.False(BackupMigration.IsWindowsPath(value));
    }

    // Bug: a Windows path stays in settings and the first capture makes a folder literally named "C:\Users\...", or nobody is told it was reset.
    [Fact]
    public void Paths_in_settings_are_mapped_or_removed_and_listed()
    {
        JObject root = JObject.Parse("""
        {
            "CustomScreenshotsPath": "C:\\Users\\me\\Pictures\\Shots",
            "CustomScreenshotsPath2": "E:\\Backup",
            "Hotkeys": [ { "TaskSettings": { "CustomCaptureSoundPath": "D:\\a.wav" } }, { "TaskSettings": { "CustomCaptureSoundPath": "D:\\a.wav" } } ],
            "SaveImageSubFolderPattern": "%y-%mo"
        }
        """);

        BackupMigrationReport report = Migrate(root);

        Assert.Equal("/h/Pictures/Shots", (string?)root["CustomScreenshotsPath"]);
        Assert.Null(root["CustomScreenshotsPath2"]);
        Assert.Null(root.SelectToken("Hotkeys[0].TaskSettings.CustomCaptureSoundPath"));
        Assert.Equal("%y-%mo", (string?)root["SaveImageSubFolderPattern"]);
        Assert.Single(report.PathsMapped);
        // The same reset across hotkeys is one line, not one per hotkey.
        Assert.Equal(2, report.PathsReset.Count);
        Assert.Contains(new MigratedPath("CustomScreenshotsPath2", @"E:\Backup", null!), report.PathsReset);
    }

    // Bug: a record of what happened gets rewritten, or a custom uploader's request body is altered.
    [Fact]
    public void Records_and_custom_uploaders_keep_their_values()
    {
        JObject root = JObject.Parse("""
        {
            "RecentTasks": [ { "FilePath": "C:\\Users\\me\\Pictures\\a.png" } ],
            "CustomUploadersList": [ { "Name": "Mine", "Arguments": { "folder": "C:\\Users\\me\\x" } } ]
        }
        """);

        BackupMigrationReport report = Migrate(root);

        Assert.Equal(@"C:\Users\me\Pictures\a.png", (string?)root.SelectToken("RecentTasks[0].FilePath"));
        Assert.Equal(@"C:\Users\me\x", (string?)root.SelectToken("CustomUploadersList[0].Arguments.folder"));
        Assert.Empty(report.PathsReset);
    }

    // Bug: a backup from someone else runs a program of its choosing on the next capture or opened link.
    [Fact]
    public void Programs_chosen_by_the_backup_do_not_run()
    {
        JObject root = JObject.Parse("""
        {
            "BrowserPath": "/tmp/evil",
            "DefaultTaskSettings": {
                "CaptureSettings": { "FFmpegOptions": { "OverrideCLIPath": true, "CLIPath": "/tmp/evil" } },
                "ExternalPrograms": [ { "IsActive": true, "Name": "Evil", "Path": "/tmp/evil" }, { "IsActive": false, "Name": "Idle", "Path": "/usr/bin/gimp" } ]
            }
        }
        """);

        BackupMigrationReport report = Migrate(root);

        Assert.Null(root["BrowserPath"]);
        Assert.Null(root.SelectToken("DefaultTaskSettings.CaptureSettings.FFmpegOptions.CLIPath"));
        Assert.False((bool)root.SelectToken("DefaultTaskSettings.ExternalPrograms[0].IsActive")!);
        Assert.Equal(new[] { "Evil" }, report.TurnedOff);
    }

    // Bug: Json.NET loads a type named by the backup, or one unknown effect fails the whole settings file.
    [Fact]
    public void Only_shareX_types_survive()
    {
        JObject root = JObject.Parse("""
        {
            "Effects": [
                { "$type": "System.Diagnostics.Process, System.Diagnostics.Process" },
                { "$type": "ShareX.ImageEffectsLib.Gone, ShareX.ImageEffectsLib" },
                { "$type": "ShareX.HelpersLib.ExternalProgram, ShareX.HelpersLib", "Name": "kept" }
            ],
            "Single": { "$type": "Evil.Type, Evil" }
        }
        """);

        BackupMigrationReport report = Migrate(root);

        Assert.Equal("kept", (string?)((JArray)root["Effects"]!).Single()["Name"]);
        Assert.Null(root["Single"]);
        Assert.Equal(3, report.DroppedEffects.Count);
    }

    private static string MakeArchive(string folder, params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(folder, "a.sxb");

        using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach ((string name, string text) in entries)
            {
                using StreamWriter writer = new(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        return path;
    }

    private static void InTempFolder(Action<string> test)
    {
        string folder = Directory.CreateTempSubdirectory("sxm8").FullName;

        try
        {
            test(folder);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static readonly Dictionary<string, long> Allowed = new() { ["ApplicationConfig.json"] = 1000 };

    // Bug: a crafted name reaches the import, or a file lands outside the personal folder.
    [Fact]
    public void Only_exact_names_reach_the_reader()
    {
        InTempFolder(folder =>
        {
            string archive = MakeArchive(folder, ("../../evil.json", "x"), (@"..\..\evil.json", "x"), ("/etc/evil.json", "x"), (@"C:\evil.json", "x"),
                ("sub/ApplicationConfig.json", "x"), ("ApplicationConfig.exe", "x"), ("ApplicationConfig.json", "good"));
            List<string> read = new();

            List<string> skipped = ZipManager.ReadEntries(archive, Allowed, (name, stream) => read.Add(name + "=" + new StreamReader(stream).ReadToEnd()));

            Assert.Equal(new[] { "ApplicationConfig.json=good" }, read);
            Assert.Equal(6, skipped.Count);
            Assert.Equal(new[] { "a.sxb" }, Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
        });
    }

    // Bug: a second copy of a config silently wins, so a viewer of the zip and the import see different settings.
    [Fact]
    public void A_duplicate_entry_is_rejected()
    {
        InTempFolder(folder =>
        {
            string archive = MakeArchive(folder, ("ApplicationConfig.json", "a"), ("ApplicationConfig.json", "b"));

            Assert.Throws<InvalidDataException>(() => ZipManager.ReadEntries(archive, Allowed, (_, stream) => stream.CopyTo(Stream.Null)));
        });
    }

    // Bug: a config bigger than the cap is read into memory because only the total was checked.
    [Fact]
    public void An_entry_over_its_cap_is_rejected()
    {
        InTempFolder(folder =>
        {
            string archive = MakeArchive(folder, ("ApplicationConfig.json", new string('x', 5000)));

            Assert.Throws<InvalidDataException>(() => ZipManager.ReadEntries(archive, Allowed, (_, stream) => stream.CopyTo(Stream.Null)));
        });
    }

    // Bug: a zip bomb lies about its size in the header and the cap, which trusted the header, lets it through.
    [Fact]
    public void A_header_that_understates_the_size_does_not_beat_the_cap()
    {
        InTempFolder(folder =>
        {
            string archive = MakeArchive(folder, ("ApplicationConfig.json", new string('x', 500_000)));
            byte[] bytes = File.ReadAllBytes(archive);
            int central = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 0x01, 0x02 });
            // The uncompressed size field of the central directory entry, which is what ZipArchive believes.
            BitConverter.GetBytes(10).CopyTo(bytes, central + 24);
            File.WriteAllBytes(archive, bytes);
            long total = 0;

            // Either the zip reader stops at the claimed size or MaxLengthStream throws. What counts is how much came out.
            Record.Exception(() => ZipManager.ReadEntries(archive, Allowed, (_, stream) =>
            {
                byte[] buffer = new byte[4096];
                int read;

                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                }
            }));

            Assert.True(total <= Allowed["ApplicationConfig.json"] + 4096);
        });
    }

    // Bug: the junk folder gets created at runtime when a config copied by hand still holds a Windows path.
    [Fact]
    public void No_folder_is_created_from_a_windows_path()
    {
        string previous = Environment.CurrentDirectory;

        InTempFolder(folder =>
        {
            Environment.CurrentDirectory = folder;

            try
            {
                FileHelpers.CreateDirectory(@"C:\Users\me\Pictures");
                FileHelpers.CreateDirectoryFromFilePath(@"C:\Users\me\Pictures/a.png");

                Assert.Empty(Directory.GetFileSystemEntries(folder));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        });
    }
}
