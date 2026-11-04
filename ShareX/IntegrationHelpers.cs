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
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    public static class IntegrationHelpers
    {
        public const string DesktopFileId = "ShareX.desktop";
        private const string CustomUploaderDesktopFile = "ShareX-custom-uploader.desktop";
        private const string ImageEffectDesktopFile = "ShareX-image-effect.desktop";
        private const string CustomUploaderMimeType = "application/x-sharex-custom-uploader";
        private const string ImageEffectMimeType = "application/x-sharex-image-effect";
        private const string IconName = "ShareX";

        private static readonly string ApplicationExec = DesktopEntryHelpers.QuoteExecArgument(Environment.ProcessPath);
        private static readonly string UploadMenuExec = $"{ApplicationExec} %F";
        private static readonly string EditMenuExec = $"{ApplicationExec} -ImageEditor %f";

        private static string DataHome => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        private static string ApplicationsFolder => Path.Combine(DataHome, "applications");
        private static string MimeFolder => Path.Combine(DataHome, "mime");
        private static string InstalledIconsFolder => Path.Combine(DataHome, "icons", "hicolor");
        private static string BundledIconsFolder => FileHelpers.GetAbsolutePath("icons/hicolor");
        private static string UploadMenuPath => Path.Combine(DataHome, "kio", "servicemenus", "ShareX-upload.desktop");
        private static string EditMenuPath => Path.Combine(DataHome, "kio", "servicemenus", "ShareX-edit.desktop");

        private static readonly string ChromeNativeMessagingHosts = @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.getsharex.sharex";
        private static readonly string FirefoxNativeMessagingHosts = @"SOFTWARE\Mozilla\NativeMessagingHosts\ShareX";
        private static readonly string ChromeHostManifestFilePath = FileHelpers.GetAbsolutePath("host-manifest-chrome.json");
        private static readonly string FirefoxHostManifestFilePath = FileHelpers.GetAbsolutePath("host-manifest-firefox.json");

        public static bool CheckShellContextMenuButton()
        {
            return DesktopEntryHelpers.HasExecLine(UploadMenuPath, UploadMenuExec);
        }

        public static void CreateShellContextMenuButton(bool create)
        {
            try
            {
                SetUploadMenu(create);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        public static bool CheckEditShellContextMenuButton()
        {
            return DesktopEntryHelpers.HasExecLine(EditMenuPath, EditMenuExec);
        }

        public static void CreateEditShellContextMenuButton(bool create)
        {
            try
            {
                SetEditMenu(create);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void SetUploadMenu(bool create)
        {
            SetServiceMenu(create, UploadMenuPath, "all/all", Strings.IntegrationHelpers_UploadWithShareX, UploadMenuExec);
        }

        private static void SetEditMenu(bool create)
        {
            SetServiceMenu(create, EditMenuPath, "image/*", Strings.IntegrationHelpers_EditWithShareX, EditMenuExec);
        }

        private static void SetServiceMenu(bool create, string path, string mimeType, string name, string exec)
        {
            if (create)
            {
                // KIO ignores a service menu in the user's folder unless it is executable.
                DesktopEntryHelpers.Write(path, DesktopEntryHelpers.Format(
                    "[Desktop Entry]",
                    "Type=Service",
                    $"MimeType={mimeType};",
                    "Actions=ShareX;",
                    "X-KDE-Priority=TopLevel",
                    "",
                    "[Desktop Action ShareX]",
                    $"Name={name}",
                    $"Icon={IconName}",
                    $"Exec={exec}"), executable: true);
            }
            else
            {
                File.Delete(path);
            }
        }

        /// <summary>
        /// The <c>-install</c> command: places the launcher, file types, icons and context menu entries in the user's XDG folders,
        /// pointing at this binary. Autostart stays the settings toggle's job, off until the user asks for it.
        /// </summary>
        public static void Install()
        {
            // The main entry claims no file types: opening a .sxcu must import it, and a plain %F argument would upload it.
            DesktopEntryHelpers.Write(Path.Combine(ApplicationsFolder, DesktopFileId), DesktopEntryHelpers.Format(
                "[Desktop Entry]",
                "Type=Application",
                "Name=ShareX",
                "GenericName=Screenshot Tool",
                "Comment=Screen capture, file sharing and productivity tool",
                $"Exec={ApplicationExec} %F",
                $"Icon={IconName}",
                "Terminal=false",
                "StartupWMClass=ShareX",
                "Categories=Utility;",
                "Keywords=screenshot;screen capture;screen recording;upload;"));
            WriteFileTypeEntry(CustomUploaderDesktopFile, CustomUploaderMimeType, "-CustomUploader");
            WriteFileTypeEntry(ImageEffectDesktopFile, ImageEffectMimeType, "-ImageEffect");

            DesktopEntryHelpers.Write(Path.Combine(MimeFolder, "packages", "ShareX.xml"), DesktopEntryHelpers.Format(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">",
                $"  <mime-type type=\"{CustomUploaderMimeType}\">",
                "    <comment>ShareX custom uploader</comment>",
                "    <sub-class-of type=\"application/json\"/>",
                "    <icon name=\"ShareX-file\"/>",
                "    <glob pattern=\"*.sxcu\"/>",
                "  </mime-type>",
                $"  <mime-type type=\"{ImageEffectMimeType}\">",
                "    <comment>ShareX image effect</comment>",
                "    <sub-class-of type=\"application/zip\"/>",
                "    <icon name=\"ShareX-file\"/>",
                "    <glob pattern=\"*.sxie\"/>",
                "  </mime-type>",
                "</mime-info>"));

            foreach (string icon in BundledIcons())
            {
                string target = Path.Combine(InstalledIconsFolder, Path.GetRelativePath(BundledIconsFolder, icon));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(icon, target, true);
            }

            SetUploadMenu(true);
            SetEditMenu(true);
            RefreshDatabases();
        }

        private static void WriteFileTypeEntry(string fileName, string mimeType, string command)
        {
            // No StartupWMClass here: KWin gives a window the first entry claiming its class, and the window must resolve to the main entry.
            DesktopEntryHelpers.Write(Path.Combine(ApplicationsFolder, fileName), DesktopEntryHelpers.Format(
                "[Desktop Entry]",
                "Type=Application",
                "Name=ShareX",
                $"Exec={ApplicationExec} {command} %f",
                $"Icon={IconName}",
                "Terminal=false",
                "NoDisplay=true",
                $"MimeType={mimeType};"));
        }

        private static IEnumerable<string> BundledIcons()
        {
            return Directory.EnumerateFiles(BundledIconsFolder, "*.png", SearchOption.AllDirectories);
        }

        private static void RefreshDatabases()
        {
            RunTool("update-mime-database", MimeFolder);
            RunTool("update-desktop-database", ApplicationsFolder);
        }

        private static void RunTool(string tool, string folder)
        {
            try
            {
                using Process process = Process.Start(new ProcessStartInfo(tool) { ArgumentList = { folder }, UseShellExecute = false });
                process.WaitForExit();
            }
            catch (Win32Exception e)
            {
                // Not fatal: the files are in place, and a desktop that needs the database can build it later.
                Console.Error.WriteLine($"{tool} could not run: {e.Message}");
            }
        }

        public static bool CheckChromeExtensionSupport()
        {
            try
            {
                return RegistryHelpers.CheckStringValue(ChromeNativeMessagingHosts, null, ChromeHostManifestFilePath) && File.Exists(ChromeHostManifestFilePath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static void CreateChromeExtensionSupport(bool create)
        {
            try
            {
                if (create)
                {
                    UnregisterChromeExtensionSupport();
                    RegisterChromeExtensionSupport();
                }
                else
                {
                    UnregisterChromeExtensionSupport();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void RegisterChromeExtensionSupport()
        {
            RegistryHelpers.CreateRegistry(ChromeNativeMessagingHosts, ChromeHostManifestFilePath);
        }

        private static void UnregisterChromeExtensionSupport()
        {
            RegistryHelpers.RemoveRegistry(ChromeNativeMessagingHosts);
        }

        public static bool CheckFirefoxAddonSupport()
        {
            try
            {
                return RegistryHelpers.CheckStringValue(FirefoxNativeMessagingHosts, null, FirefoxHostManifestFilePath) && File.Exists(FirefoxHostManifestFilePath);
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }

            return false;
        }

        public static void CreateFirefoxAddonSupport(bool create)
        {
            try
            {
                if (create)
                {
                    UnregisterFirefoxAddonSupport();
                    RegisterFirefoxAddonSupport();
                }
                else
                {
                    UnregisterFirefoxAddonSupport();
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private static void RegisterFirefoxAddonSupport()
        {
            RegistryHelpers.CreateRegistry(FirefoxNativeMessagingHosts, FirefoxHostManifestFilePath);
        }

        private static void UnregisterFirefoxAddonSupport()
        {
            RegistryHelpers.RemoveRegistry(FirefoxNativeMessagingHosts);
        }

        public static bool CheckSteamShowInApp()
        {
            return File.Exists(AppPaths.SteamInAppFilePath);
        }

        public static void SteamShowInApp(bool showInApp)
        {
            string path = AppPaths.SteamInAppFilePath;

            try
            {
                if (showInApp)
                {
                    FileHelpers.CreateEmptyFile(path);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                e.ShowError();
                return;
            }

            MessageBox.Show(Strings.ApplicationSettingsForm_cbSteamShowInApp_CheckedChanged_For_settings_to_take_effect_ShareX_needs_to_be_reopened_from_Steam_,
                "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Uninstall()
        {
            StartupManager.State = StartupState.Disabled;
            SetUploadMenu(false);
            SetEditMenu(false);
            File.Delete(Path.Combine(ApplicationsFolder, DesktopFileId));
            File.Delete(Path.Combine(ApplicationsFolder, CustomUploaderDesktopFile));
            File.Delete(Path.Combine(ApplicationsFolder, ImageEffectDesktopFile));
            File.Delete(Path.Combine(MimeFolder, "packages", "ShareX.xml"));

            foreach (string icon in BundledIcons())
            {
                File.Delete(Path.Combine(InstalledIconsFolder, Path.GetRelativePath(BundledIconsFolder, icon)));
            }

            RefreshDatabases();
        }
    }
}
