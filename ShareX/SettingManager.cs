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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.HistoryLib;
using ShareX.Localization;
using ShareX.ScreenCaptureLib;
using ShareX.UploadersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    internal static class SettingManager
    {
        private const string ApplicationConfigFileName = "ApplicationConfig.json";

        private static string ApplicationConfigFilePath
        {
            get
            {
                if (StartupOptions.Sandbox) return null;

                return Path.Combine(AppPaths.PersonalFolder, ApplicationConfigFileName);
            }
        }

        private const string UploadersConfigFileNamePrefix = "UploadersConfig";
        private const string UploadersConfigFileNameExtension = "json";
        private const string UploadersConfigFileName = UploadersConfigFileNamePrefix + "." + UploadersConfigFileNameExtension;

        private static string UploadersConfigFilePath
        {
            get
            {
                if (StartupOptions.Sandbox) return null;

                string uploadersConfigFolder;

                if (Settings != null && !string.IsNullOrEmpty(Settings.CustomUploadersConfigPath))
                {
                    uploadersConfigFolder = FileHelpers.ExpandFolderVariables(Settings.CustomUploadersConfigPath);
                }
                else
                {
                    uploadersConfigFolder = AppPaths.PersonalFolder;
                }

                string uploadersConfigFileName = GetUploadersConfigFileName(uploadersConfigFolder);

                return Path.Combine(uploadersConfigFolder, uploadersConfigFileName);
            }
        }

        private const string HotkeysConfigFileName = "HotkeysConfig.json";

        private static string HotkeysConfigFilePath
        {
            get
            {
                if (StartupOptions.Sandbox) return null;

                string hotkeysConfigFolder;

                if (Settings != null && !string.IsNullOrEmpty(Settings.CustomHotkeysConfigPath))
                {
                    hotkeysConfigFolder = FileHelpers.ExpandFolderVariables(Settings.CustomHotkeysConfigPath);
                }
                else
                {
                    hotkeysConfigFolder = AppPaths.PersonalFolder;
                }

                return Path.Combine(hotkeysConfigFolder, HotkeysConfigFileName);
            }
        }

        public static string BackupFolder => Path.Combine(AppPaths.PersonalFolder, "Backup");

        // Far above any real config or history, low enough that a bad archive can't fill the disk or memory.
        private const long MaxImportedConfigSize = 64_000_000;
        private const long MaxImportedHistorySize = 256_000_000;

        private static readonly Dictionary<string, long> ImportableEntries = new Dictionary<string, long>()
        {
            [ApplicationConfigFileName] = MaxImportedConfigSize,
            [UploadersConfigFileName] = MaxImportedConfigSize,
            [HotkeysConfigFileName] = MaxImportedConfigSize,
            [AppPaths.HistoryFileName] = MaxImportedHistorySize,
            [AppPaths.HistoryFileNameOld] = MaxImportedHistorySize
        };

        private static ApplicationConfig Settings { get => ApplicationState.SettingsOrNull; set => ApplicationState.Settings = value; }
        private static TaskSettings DefaultTaskSettings { get => ApplicationState.DefaultTaskSettings; set => ApplicationState.DefaultTaskSettings = value; }
        private static UploadersConfig UploadersConfig { get => ApplicationState.UploadersConfigOrNull; set => ApplicationState.UploadersConfig = value; }
        private static HotkeysConfig HotkeysConfig { get => ApplicationState.HotkeysConfigOrNull; set => ApplicationState.HotkeysConfig = value; }

        private static ManualResetEvent uploadersConfigResetEvent = new ManualResetEvent(false);
        private static ManualResetEvent hotkeysConfigResetEvent = new ManualResetEvent(false);

        public static void LoadInitialSettings()
        {
            LoadApplicationConfig();

            Task.Run(() =>
            {
                LoadUploadersConfig();
                uploadersConfigResetEvent.Set();

                LoadHotkeysConfig();
                hotkeysConfigResetEvent.Set();
            });
        }

        public static void WaitUploadersConfig()
        {
            if (UploadersConfig == null)
            {
                uploadersConfigResetEvent.WaitOne();
            }
        }

        public static void WaitHotkeysConfig()
        {
            if (HotkeysConfig == null)
            {
                hotkeysConfigResetEvent.WaitOne();
            }
        }

        public static void LoadApplicationConfig(bool fallbackSupport = true)
        {
            Settings = ApplicationConfig.Load(ApplicationConfigFilePath, BackupFolder, fallbackSupport);
            Settings.CreateBackup = true;
            Settings.CreateWeeklyBackup = true;
            Settings.SettingsSaveFailed += Settings_SettingsSaveFailed;
            DefaultTaskSettings = Settings.DefaultTaskSettings;
            ApplicationConfigBackwardCompatibilityTasks();
            Settings.ThemeOptions ??= new ApplicationThemeOptions();
            ThemeManager.Configure(Settings.ThemeOptions);
            MigrateHistoryFile();
            HistoryConnect();
        }

        private static void Settings_SettingsSaveFailed(Exception e)
        {
            string message;

            if (e is UnauthorizedAccessException || e is FileNotFoundException)
            {
                message = Strings.YourAntiVirusSoftwareOrTheControlledFolderAccessFeatureInWindowsCouldBeBlockingShareX;
            }
            else
            {
                message = e.Message;
            }

            TaskHelpers.ShowNotificationTip(message, "ShareX - " + Strings.FailedToSaveSettings, 5000);
        }

        public static void LoadUploadersConfig(bool fallbackSupport = true)
        {
            UploadersConfig = UploadersConfig.Load(UploadersConfigFilePath, BackupFolder, fallbackSupport);
            UploadersConfig.CreateBackup = true;
            UploadersConfig.CreateWeeklyBackup = true;
            UploadersConfig.SettingsSaveFailed += Settings_SettingsSaveFailed;
            UploadersConfigBackwardCompatibilityTasks();
        }

        public static void LoadHotkeysConfig(bool fallbackSupport = true)
        {
            HotkeysConfig = HotkeysConfig.Load(HotkeysConfigFilePath, BackupFolder, fallbackSupport);
            HotkeysConfig.CreateBackup = true;
            HotkeysConfig.CreateWeeklyBackup = true;
            HotkeysConfig.SettingsSaveFailed += Settings_SettingsSaveFailed;
            HotkeysConfigBackwardCompatibilityTasks();
        }

        public static void LoadAllSettings()
        {
            LoadApplicationConfig();
            LoadUploadersConfig();
            LoadHotkeysConfig();
        }

        private static string GetUploadersConfigFileName(string destinationFolder)
        {
            if (string.IsNullOrEmpty(destinationFolder))
            {
                return UploadersConfigFileName;
            }

            if (Settings != null && Settings.UseMachineSpecificUploadersConfig)
            {
                string sanitizedMachineName = FileHelpers.SanitizeFileName(Environment.MachineName.ToLowerInvariant());

                if (!string.IsNullOrEmpty(sanitizedMachineName))
                {
                    string machineSpecificFileName = $"{UploadersConfigFileNamePrefix}-{sanitizedMachineName}.{UploadersConfigFileNameExtension}";
                    string machineSpecificPath = Path.Combine(destinationFolder, machineSpecificFileName);

                    if (!File.Exists(machineSpecificPath))
                    {
                        string defaultFilePath = Path.Combine(destinationFolder, UploadersConfigFileName);

                        if (File.Exists(defaultFilePath))
                        {
                            try
                            {
                                File.Copy(defaultFilePath, machineSpecificPath, false);
                            }
                            catch (IOException)
                            {
                                // Ignore copy issues; file may have been created in the meantime.
                            }
                        }
                    }

                    return machineSpecificFileName;
                }
            }

            return UploadersConfigFileName;
        }

        private static void ApplicationConfigBackwardCompatibilityTasks()
        {
            if (SystemOptions.DisableUpload)
            {
                DefaultTaskSettings.AfterCaptureJob = DefaultTaskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.UploadImageToHost);
            }

            if (Settings.IsUpgradeFrom("14.1.2"))
            {
                if (!Environment.Is64BitOperatingSystem && !string.IsNullOrEmpty(DefaultTaskSettings.CaptureSettings.FFmpegOptions.CLIPath))
                {
                    DefaultTaskSettings.CaptureSettings.FFmpegOptions.OverrideCLIPath = true;
                }
            }

            if (Settings.IsUpgradeFrom("15.0.1"))
            {
                DefaultTaskSettings.CaptureSettings.ScrollingCaptureOptions = new ScrollingCaptureOptions();
                DefaultTaskSettings.CaptureSettings.FFmpegOptions.FixSources();
            }

            if (Settings.IsUpgradeFrom("16.0.2"))
            {
                if (Settings.CheckPreReleaseUpdates)
                {
                    Settings.UpdateChannel = UpdateChannel.PreRelease;
                }
            }
        }

        public static void HistoryConnect()
        {
            HistoryClose();
            ApplicationState.HistoryManager = new HistoryManagerSQLite(AppPaths.HistoryFilePath);
        }

        public static void HistoryClose()
        {
            if (ApplicationState.HistoryManager != null)
            {
                ApplicationState.HistoryManager.Dispose();
                ApplicationState.HistoryManager = null;
            }
        }

        private static void MigrateHistoryFile()
        {
            if (File.Exists(AppPaths.HistoryFilePathOld))
            {
                try
                {
                    if (!File.Exists(AppPaths.HistoryFilePath))
                    {
                        DebugHelper.WriteLine($"Migrating JSON history file \"{AppPaths.HistoryFilePathOld}\" to SQLite history file \"{AppPaths.HistoryFilePath}\"");

                        using (HistoryManagerSQLite historyManager = new HistoryManagerSQLite(AppPaths.HistoryFilePath))
                        {
                            historyManager.MigrateFromJSON(AppPaths.HistoryFilePathOld);
                        }
                    }

                    FileHelpers.MoveFile(AppPaths.HistoryFilePathOld, BackupFolder);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                    e.ShowError();
                }
            }
        }

        private static void UploadersConfigBackwardCompatibilityTasks()
        {
            if (UploadersConfig.CustomUploadersList != null)
            {
                foreach (CustomUploaderItem cui in UploadersConfig.CustomUploadersList)
                {
                    try
                    {
                        cui.CheckBackwardCompatibility();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void HotkeysConfigBackwardCompatibilityTasks()
        {
            if (SystemOptions.DisableUpload)
            {
                foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
                {
                    if (taskSettings != null)
                    {
                        taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.UploadImageToHost);
                    }
                }
            }

            if (Settings.IsUpgradeFrom("15.0.1"))
            {
                foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
                {
                    if (taskSettings != null && taskSettings.CaptureSettings != null)
                    {
                        taskSettings.CaptureSettings.ScrollingCaptureOptions = new ScrollingCaptureOptions();
                        taskSettings.CaptureSettings.FFmpegOptions.FixSources();
                    }
                }
            }
        }

        public static void CleanupHotkeysConfig()
        {
            foreach (TaskSettings taskSettings in HotkeysConfig.Hotkeys.Select(x => x.TaskSettings))
            {
                taskSettings.Cleanup();
            }
        }

        public static void SaveAllSettings()
        {
            if (Settings != null)
            {
                Settings.Save(ApplicationConfigFilePath);
            }

            if (UploadersConfig != null)
            {
                UploadersConfig.Save(UploadersConfigFilePath);
            }

            if (HotkeysConfig != null)
            {
                CleanupHotkeysConfig();
                HotkeysConfig.Save(HotkeysConfigFilePath);
            }
        }

        public static void SaveApplicationConfigAsync()
        {
            if (Settings != null)
            {
                Settings.SaveAsync(ApplicationConfigFilePath);
            }
        }

        public static void SaveUploadersConfigAsync()
        {
            if (UploadersConfig != null)
            {
                UploadersConfig.SaveAsync(UploadersConfigFilePath);
            }
        }

        public static void SaveHotkeysConfigAsync()
        {
            if (HotkeysConfig != null)
            {
                CleanupHotkeysConfig();
                HotkeysConfig.SaveAsync(HotkeysConfigFilePath);
            }
        }

        public static void SaveAllSettingsAsync()
        {
            SaveApplicationConfigAsync();
            SaveUploadersConfigAsync();
            SaveHotkeysConfigAsync();
        }

        public static void ResetSettings()
        {
            if (File.Exists(ApplicationConfigFilePath)) File.Delete(ApplicationConfigFilePath);
            LoadApplicationConfig(false);

            if (File.Exists(UploadersConfigFilePath)) File.Delete(UploadersConfigFilePath);
            LoadUploadersConfig(false);

            if (File.Exists(HotkeysConfigFilePath)) File.Delete(HotkeysConfigFilePath);
            LoadHotkeysConfig(false);
        }

        public static bool Export(string archivePath, bool settings, bool history)
        {
            MemoryStream msApplicationConfig = null, msUploadersConfig = null, msHotkeysConfig = null;

            try
            {
                List<ZipEntryInfo> entries = new List<ZipEntryInfo>();

                if (settings)
                {
                    msApplicationConfig = Settings.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msApplicationConfig, ApplicationConfigFileName));

                    msUploadersConfig = UploadersConfig.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msUploadersConfig, UploadersConfigFileName));

                    msHotkeysConfig = HotkeysConfig.SaveToMemoryStream(false);
                    entries.Add(new ZipEntryInfo(msHotkeysConfig, HotkeysConfigFileName));
                }

                if (history)
                {
                    entries.Add(new ZipEntryInfo(AppPaths.HistoryFilePath));
                    HistoryClose();
                }

                ZipManager.Compress(archivePath, entries, unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                MessageBox.Show(string.Format(Strings.SettingManager_ExportBackupError, e), Strings.SettingManager_ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                msApplicationConfig?.Dispose();
                msUploadersConfig?.Dispose();
                msHotkeysConfig?.Dispose();

                if (history)
                {
                    HistoryConnect();
                }
            }

            return false;
        }

        // Returns null when nothing was imported, after telling the user why.
        public static BackupImportReport Import(string archivePath)
        {
            string workFolder = Path.Combine(AppPaths.PersonalFolder, "Import-" + Guid.NewGuid().ToString("N"));

            try
            {
                HistoryClose();

                Dictionary<string, string> configs = new Dictionary<string, string>();

                List<string> skipped = ZipManager.ReadEntries(archivePath, ImportableEntries, (name, stream) =>
                {
                    if (name == AppPaths.HistoryFileName || name == AppPaths.HistoryFileNameOld)
                    {
                        Directory.CreateDirectory(workFolder);

                        using (FileStream file = File.Create(Path.Combine(workFolder, name)))
                        {
                            stream.CopyTo(file);
                        }
                    }
                    else
                    {
                        // Held in memory, not extracted: the JSON can have secrets in the clear, and they must never reach the disk that way.
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            configs[name] = reader.ReadToEnd();
                        }
                    }
                });

                string importedHistory = Path.Combine(workFolder, AppPaths.HistoryFileName);
                string importedHistoryOld = Path.Combine(workFolder, AppPaths.HistoryFileNameOld);

                if (configs.Count == 0 && !File.Exists(importedHistory) && !File.Exists(importedHistoryOld))
                {
                    throw new InvalidDataException(Strings.SettingManager_ImportNothingFound);
                }

                BackupImportReport report = new BackupImportReport() { SkippedEntries = skipped };

                ApplicationConfig applicationConfig = MigrateConfig<ApplicationConfig>(configs, ApplicationConfigFileName, report.Migration);
                UploadersConfig uploadersConfig = MigrateConfig<UploadersConfig>(configs, UploadersConfigFileName, report.Migration);
                HotkeysConfig hotkeysConfig = MigrateConfig<HotkeysConfig>(configs, HotkeysConfigFileName, report.Migration);

                // Find out now rather than halfway through the saves, with the first config already replaced.
                if (report.Migration.NeedsSecretStorage)
                {
                    RequireSecretStorage();
                }

                // In this order: where the other two are saved depends on the application config.
                SaveImportedConfig(applicationConfig, ApplicationConfigFilePath);

                if (applicationConfig != null)
                {
                    Settings = applicationConfig;
                }

                SaveImportedConfig(uploadersConfig, UploadersConfigFilePath);
                SaveImportedConfig(hotkeysConfig, HotkeysConfigFilePath);

                if (File.Exists(importedHistory))
                {
                    MergeImportedHistory(importedHistory, report);
                }

                // The older format goes where startup looks for it, which migrates it or moves it to the backup folder.
                if (File.Exists(importedHistoryOld))
                {
                    File.Move(importedHistoryOld, AppPaths.HistoryFilePathOld, true);
                }

                return report;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                MessageBox.Show(string.Format(Strings.SettingManager_ImportBackupError, e.Message), Strings.SettingManager_ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (Directory.Exists(workFolder))
                {
                    Directory.Delete(workFolder, true);
                }

                HistoryConnect();
            }

            return null;
        }

        private static T MigrateConfig<T>(Dictionary<string, string> configs, string fileName, BackupMigrationReport report) where T : SettingsBase<T>, new()
        {
            if (!configs.TryGetValue(fileName, out string json))
            {
                return null;
            }

            try
            {
                JObject root;

                // DateParseHandling.None: every string stays exactly as stored, including a secret that happens to look like a date.
                using (JsonTextReader reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
                {
                    root = JObject.Load(reader);
                }

                HashSet<string> secretProperties = BackupMigration.GetSecretPropertyNames(typeof(UploadersConfig).Assembly, typeof(ApplicationConfig).Assembly,
                    typeof(ShareX.Tools.AIOptions).Assembly, typeof(HelpersOptions).Assembly);

                BackupMigration.Migrate(root, secretProperties, LinuxFolders.Current(AppPaths.PersonalFolder), CanDecryptHere, report);

                return SettingsBase<T>.LoadFromJson(root.ToString(Newtonsoft.Json.Formatting.None), BackupFolder);
            }
            catch (JsonException e)
            {
                // Not e.Message or e itself: a parsing message can quote the value it choked on, and this one could be a secret.
                DebugHelper.WriteLine($"{fileName} in the backup can't be read: {e.GetType().Name}");
                throw new InvalidDataException(string.Format(Strings.SettingManager_ImportFileDamaged, fileName));
            }
        }

        private static bool CanDecryptHere(string taggedValue)
        {
            // Throws if the wallet can't be reached, which stops the import. Only then does a failure below mean another machine's key.
            RequireSecretStorage();

            try
            {
                SecretServiceCrypto.Decrypt(taggedValue.Substring(taggedValue.IndexOf('$', 1) + 1));
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteLine($"A secret in the backup was encrypted with another machine's key: {e.GetType().Name}");
                return false;
            }
        }

        private static void RequireSecretStorage()
        {
            try
            {
                SecretServiceCrypto.Encrypt("ShareX");
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(string.Format(Strings.SettingManager_ImportNeedsWallet, e.Message), e);
            }
        }

        private static void SaveImportedConfig<T>(T config, string filePath) where T : SettingsBase<T>, new()
        {
            if (config != null && !config.SaveImported(filePath))
            {
                throw new IOException(string.Format(Strings.SettingManager_ImportSaveFailed, filePath));
            }
        }

        // A damaged history file shouldn't cost the settings that were imported fine, so this reports instead of throwing.
        private static void MergeImportedHistory(string importedHistory, BackupImportReport report)
        {
            try
            {
                string historyFilePath = AppPaths.HistoryFilePath;

                if (File.Exists(historyFilePath))
                {
                    FileHelpers.CreateDirectory(BackupFolder);
                    report.HistoryBackupPath = Path.Combine(BackupFolder, $"History-before-import-{DateTime.Now:yyyyMMdd-HHmmss-fff}.db");
                    File.Copy(historyFilePath, report.HistoryBackupPath);
                }

                using (HistoryManagerSQLite historyManager = new HistoryManagerSQLite(historyFilePath))
                {
                    report.History = historyManager.MergeFrom(importedHistory);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                report.HistoryError = e.Message;
            }
        }
    }
}
