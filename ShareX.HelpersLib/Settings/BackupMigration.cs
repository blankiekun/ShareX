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

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ShareX.HelpersLib
{
    public readonly record struct MigratedPath(string Setting, string From, string To);

    // Names and paths only: nothing here may ever hold a secret or a blob, because it is shown to the user and logged.
    public sealed class BackupMigrationReport
    {
        public SortedSet<string> SignInAgain { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> SecretsImported { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<MigratedPath> PathsMapped { get; } = new();
        public HashSet<MigratedPath> PathsReset { get; } = new();
        public SortedSet<string> TurnedOff { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> DroppedEffects { get; } = new(StringComparer.Ordinal);

        // The backup held a secret in the clear, so the file itself is sensitive.
        public bool HasPlaintextSecrets { get; set; }

        // Saving the imported settings has to encrypt something, so it can't go ahead without the wallet.
        public bool NeedsSecretStorage { get; set; }
    }

    public sealed record LinuxFolders(string Home, string Documents, string Pictures, string Desktop, string Music, string Videos, string Downloads, string ShareX)
    {
        public static LinuxFolders Current(string personalFolder)
        {
            string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);

            string home = Folder(Environment.SpecialFolder.UserProfile);

            return new LinuxFolders(home, Folder(Environment.SpecialFolder.MyDocuments), Folder(Environment.SpecialFolder.MyPictures),
                Folder(Environment.SpecialFolder.DesktopDirectory), Folder(Environment.SpecialFolder.MyMusic), Folder(Environment.SpecialFolder.MyVideos),
                Path.Combine(home, "Downloads"), personalFolder);
        }
    }

    // Turns the settings JSON of a backup from another machine into something safe to load here.
    public static class BackupMigration
    {
        private const string WindowsEncryptedTag = "$DPAPIEncrypted$";
        private const string ShareXEncryptedTag = "$ShareXEncrypted$";
        // Base64 of the version and provider GUID every DPAPI blob starts with, for blobs that lost their tag.
        private const string DpapiBlobHeader = "AQAAANCMnd8BFdER";
        // OAuth 2 and OAuth 1 client secrets ship with the app, so carrying one says nothing about the user being signed in.
        private static readonly HashSet<string> appClientSecretProperties = new(StringComparer.Ordinal) { "Client_Secret", "ConsumerSecret" };

        private static readonly Regex drivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);
        private static readonly Regex uncPath = new(@"^\\\\[^\\/]+[\\/]", RegexOptions.CultureInvariant);
        private static readonly Regex variablePath = new(@"^%([^%\\/\s]+)%(?:([\\/]).*)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex shareXTypeName = new(@"^ShareX\.[\w.+]+, *ShareX\.[\w.]+(,.*)?$", RegexOptions.CultureInvariant);

        // These don't resolve to anything useful on Linux even when written with a forward slash.
        private static readonly HashSet<string> windowsOnlyVariables = new(StringComparer.OrdinalIgnoreCase)
        {
            "windir", "SystemRoot", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
            "APPDATA", "LOCALAPPDATA", "ProgramData", "ALLUSERSPROFILE", "PUBLIC", "HOMEDRIVE", "HOMEPATH", "TEMP", "TMP",
            "ApplicationData", "LocalApplicationData", "CommonApplicationData", "Windows", "System", "SystemX86", "ProgramFilesX86"
        };

        // Environment.SpecialFolder names and Windows variables, as the folder under the user's profile they stand for.
        private static readonly Dictionary<string, string> profileFolderVariables = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MyDocuments"] = "Documents",
            ["Personal"] = "Documents",
            ["MyPictures"] = "Pictures",
            ["Desktop"] = "Desktop",
            ["DesktopDirectory"] = "Desktop",
            ["MyMusic"] = "Music",
            ["MyVideos"] = "Videos"
        };

        // Profiles under C:\Users that aren't the person's own, so their folders have no equivalent here.
        private static readonly HashSet<string> sharedProfiles = new(StringComparer.OrdinalIgnoreCase) { "Public", "Default", "Default User", "All Users" };

        // Run the program, so a backup from another machine must not be able to choose them.
        private static readonly HashSet<string> executablePathProperties = new(StringComparer.Ordinal) { "BrowserPath", "CLIPath" };

        // Records of what happened or what is sent to a server, not paths this machine should use.
        private static readonly HashSet<string> untouchedSubtrees = new(StringComparer.Ordinal) { "RecentTasks", "CustomUploadersList" };

        // Settings property prefix to the name the user knows the destination by.
        private static readonly (string Prefix, string Name)[] destinations =
        {
            ("Imgur", "Imgur"), ("ImageShack", "ImageShack"), ("Flickr", "Flickr"), ("Photobucket", "Photobucket"), ("Chevereto", "Chevereto"),
            ("Vgyme", "vgy.me"), ("Pastebin", "Pastebin"), ("Paste_ee", "Paste.ee"), ("Gist", "GitHub Gist"), ("Upaste", "uPaste"),
            ("OneTimeSecret", "OneTimeSecret"), ("PrivateBin", "PrivateBin"), ("Dropbox", "Dropbox"), ("FTPAccount", "FTP"), ("OneDrive", "OneDrive"),
            ("GoogleDrive", "Google Drive"), ("Puush", "puush"), ("SendSpace", "SendSpace"), ("Box", "Box"), ("Mega", "MEGA"),
            ("LocalhostAccount", "Shared folder"), ("Localhostr", "Localhostr"), ("Email", "Email"), ("AmazonS3", "Amazon S3"),
            ("OwnCloud", "ownCloud / Nextcloud"), ("MediaFire", "MediaFire"), ("Pushbullet", "Pushbullet"), ("Lambda", "Lambda"), ("ImgFish", "img.fish"),
            ("Lithiio", "LobFile"), ("Pomf", "Pomf"), ("Sul", "s-ul"), ("Seafile", "Seafile"), ("Streamable", "Streamable"),
            ("AzureStorage", "Azure Storage"), ("B2", "Backblaze B2"), ("Plik", "Plik"), ("YouTube", "YouTube"),
            ("GoogleCloudStorage", "Google Cloud Storage"), ("Immich", "Immich"), ("Bitly", "bit.ly"), ("Yourls", "YOURLS"), ("Polr", "Polr"),
            ("Firebase", "Firebase Dynamic Links"), ("Kutt", "Kutt"), ("ZeroWidthShortener", "Zero Width Shortener"),
            ("CustomUploaders", "Custom uploader"), ("Proxy", "Proxy"), ("OpenAI", "OpenAI"), ("Gemini", "Gemini"), ("OpenRouter", "OpenRouter")
        };

        public static bool IsDpapiBlob(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                (value.StartsWith(WindowsEncryptedTag, StringComparison.Ordinal) || value.StartsWith(DpapiBlobHeader, StringComparison.Ordinal));
        }

        public static HashSet<string> GetSecretPropertyNames(params Assembly[] assemblies)
        {
            return assemblies.Distinct().SelectMany(assembly => assembly.GetTypes())
                .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(property => property.IsDefined(typeof(JsonEncryptAttribute), true))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
        }

        public static bool IsWindowsPath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (drivePath.IsMatch(value) || uncPath.IsMatch(value))
            {
                return true;
            }

            Match match = variablePath.Match(value);

            // Linux ShareX writes "%MyPictures%/x" itself and it resolves here, so only a backslash or a Windows-only name counts.
            return match.Success && (match.Groups[2].Value == "\\" || windowsOnlyVariables.Contains(match.Groups[1].Value));
        }

        // Returns the Linux folder a Windows path obviously means, or null when there isn't one.
        public static string MapWindowsPath(string value, LinuxFolders folders)
        {
            string[] parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            // A path that climbs isn't obviously anywhere.
            if (parts.Any(part => part is "." or ".."))
            {
                return null;
            }

            Match variable = variablePath.Match(value);

            if (variable.Success)
            {
                string name = variable.Groups[1].Value;
                string[] rest = parts.Skip(1).ToArray();

                if (name.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase))
                {
                    return MapProfilePath(rest, folders);
                }

                if (name.Equals("ShareX", StringComparison.OrdinalIgnoreCase))
                {
                    return Join(folders.ShareX, rest);
                }

                return profileFolderVariables.TryGetValue(name, out string folder) ? MapProfilePath(rest.Prepend(folder).ToArray(), folders) : null;
            }

            if (drivePath.IsMatch(value) && parts.Length >= 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase) && !sharedProfiles.Contains(parts[2]))
            {
                return MapProfilePath(parts.Skip(3).ToArray(), folders);
            }

            return null;
        }

        private static string MapProfilePath(string[] parts, LinuxFolders folders)
        {
            if (parts.Length == 0)
            {
                return folders.Home;
            }

            // Windows often redirects these into OneDrive, as "OneDrive" or "OneDrive - Company".
            if (parts.Length > 1 && parts[0].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                parts = parts.Skip(1).ToArray();
            }

            string[] rest = parts.Skip(1).ToArray();

            if (parts[0].Equals("Documents", StringComparison.OrdinalIgnoreCase) && rest.Length > 0 && rest[0].Equals("ShareX", StringComparison.OrdinalIgnoreCase))
            {
                return Join(folders.ShareX, rest.Skip(1).ToArray());
            }

            string folder = parts[0].ToLowerInvariant() switch
            {
                "documents" => folders.Documents,
                "pictures" => folders.Pictures,
                "desktop" => folders.Desktop,
                "music" => folders.Music,
                "videos" => folders.Videos,
                "downloads" => folders.Downloads,
                _ => null
            };

            return folder == null ? null : Join(folder, rest);
        }

        private static string Join(string folder, string[] rest)
        {
            return Path.Combine(rest.Prepend(folder).ToArray());
        }

        public static string GetDestinationName(JToken token)
        {
            List<string> properties = token.AncestorsAndSelf().OfType<JProperty>().Select(property => property.Name).ToList();
            string name = properties.Select(property => destinations.FirstOrDefault(d => property.StartsWith(d.Prefix, StringComparison.Ordinal)).Name)
                .FirstOrDefault(match => match != null) ?? properties.FirstOrDefault() ?? "?";

            // Accounts sit in lists, and the list's name alone doesn't say which FTP login it was.
            JObject listItem = token.AncestorsAndSelf().OfType<JObject>().FirstOrDefault(item => item.Parent is JArray);
            string accountName = (listItem?["Name"] as JValue)?.Value as string;

            return string.IsNullOrEmpty(accountName) ? name : $"{name} \"{accountName}\"";
        }

        // canDecrypt says whether this machine's wallet opens a $ShareXEncrypted$ value. A failure to reach the wallet at all should throw from it.
        public static void Migrate(JObject root, IReadOnlySet<string> secretProperties, LinuxFolders folders, Func<string, bool> canDecrypt, BackupMigrationReport report)
        {
            RemoveUnknownTypes(root, report);
            TurnOffExternalPrograms(root, report);

            foreach (JValue value in root.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
            {
                string text = (string)value;

                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                string property = (value.Parent as JProperty)?.Name;
                bool encryptedHere = text.StartsWith(ShareXEncryptedTag, StringComparison.Ordinal);

                if (IsDpapiBlob(text) || (encryptedHere && !canDecrypt(text)))
                {
                    report.SignInAgain.Add(GetDestinationName(value));
                    value.Value = "";
                }
                else if (encryptedHere)
                {
                    report.NeedsSecretStorage = true;
                }
                else if (property != null && secretProperties.Contains(property))
                {
                    report.NeedsSecretStorage = true;
                    report.HasPlaintextSecrets = true;

                    if (!appClientSecretProperties.Contains(property))
                    {
                        report.SecretsImported.Add(GetDestinationName(value));
                    }
                }
                else if (property != null && executablePathProperties.Contains(property))
                {
                    report.PathsReset.Add(new MigratedPath(property, text, null));
                    value.Parent.Remove();
                }
                else if (IsWindowsPath(text) && !IsInUntouchedSubtree(value))
                {
                    RewritePath(value, text, property, folders, report);
                }
            }

            // Windows ShareX encrypts OAuth 1's UserSecret but not its UserToken, so the same account would show up in both lists.
            report.SecretsImported.ExceptWith(report.SignInAgain);
        }

        private static void RewritePath(JValue value, string text, string property, LinuxFolders folders, BackupMigrationReport report)
        {
            string setting = property ?? value.AncestorsAndSelf().OfType<JProperty>().FirstOrDefault()?.Name ?? "?";
            string mapped = MapWindowsPath(text, folders);

            if (mapped != null)
            {
                report.PathsMapped.Add(new MigratedPath(setting, text, mapped));
                value.Value = mapped;
            }
            else
            {
                report.PathsReset.Add(new MigratedPath(setting, text, null));

                // Removing it leaves the setting at the default its class sets, which is the Linux default.
                if (value.Parent is JProperty holder)
                {
                    holder.Remove();
                }
                else
                {
                    value.Remove();
                }
            }
        }

        private static bool IsInUntouchedSubtree(JToken token)
        {
            return token.AncestorsAndSelf().OfType<JProperty>().Any(property => untouchedSubtrees.Contains(property.Name));
        }

        // Json.NET resolves $type before it checks what the type is, and one it can't resolve fails the whole file.
        // Only ShareX's own types are kept, so neither a hostile type name nor an effect this build doesn't have gets that far.
        private static void RemoveUnknownTypes(JObject root, BackupMigrationReport report)
        {
            List<JProperty> typeProperties = root.Descendants().OfType<JProperty>().Where(property => property.Name == "$type").ToList();

            foreach (JProperty typeProperty in typeProperties)
            {
                string typeName = (typeProperty.Value as JValue)?.Value as string;

                if (typeName != null && shareXTypeName.IsMatch(typeName) && Type.GetType(typeName, false) != null)
                {
                    continue;
                }

                JToken holder = typeProperty.Parent;

                // An earlier removal may have taken this one with it.
                if (holder.Parent != null)
                {
                    report.DroppedEffects.Add(typeName ?? "?");

                    if (holder.Parent is JProperty holderProperty)
                    {
                        holderProperty.Remove();
                    }
                    else
                    {
                        holder.Remove();
                    }
                }
            }
        }

        private static void TurnOffExternalPrograms(JObject root, BackupMigrationReport report)
        {
            IEnumerable<JObject> programs = root.Descendants().OfType<JProperty>().Where(property => property.Name == "ExternalPrograms")
                .SelectMany(property => property.Value is JArray list ? list.OfType<JObject>() : Enumerable.Empty<JObject>());

            foreach (JObject program in programs)
            {
                if (program["IsActive"] is JValue { Type: JTokenType.Boolean, Value: true })
                {
                    program["IsActive"] = false;
                    report.TurnedOff.Add((program["Name"] as JValue)?.Value as string ?? "?");
                }
            }
        }
    }
}
