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

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ShareX.HelpersLib
{
    public static class ZipManager
    {
        private const int MaxForeignArchiveEntries = 1000;

        public static void Extract(string archivePath, string destination, bool retainDirectoryStructure = true, Func<ZipArchiveEntry, bool> filter = null,
            long maxUncompressedSize = 0)
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                if (maxUncompressedSize > 0)
                {
                    long totalUncompressedSize = archive.Entries.Sum(entry => entry.Length);

                    if (totalUncompressedSize > maxUncompressedSize)
                    {
                        throw new Exception(Localization.Strings.ZipManager_Archive_too_large + "\r\n\r\n" +
                            string.Format(Localization.Strings.ZipManager_Archive_uncompressed_file_size, totalUncompressedSize.ToSizeString()) + "\r\n" +
                            string.Format(Localization.Strings.ZipManager_Maximum_allowed_file_size, maxUncompressedSize.ToSizeString()));
                    }
                }

                string fullName = Directory.CreateDirectory(Path.GetFullPath(destination)).FullName;
                // Include the separator so a sibling with the same name prefix is not treated as a child.
                string fullNameWithSeparator = Path.EndsInDirectorySeparator(fullName) ? fullName : fullName + Path.DirectorySeparatorChar;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (filter != null && !filter(entry))
                    {
                        continue;
                    }

                    string entryName;

                    if (retainDirectoryStructure)
                    {
                        entryName = entry.FullName;
                    }
                    else
                    {
                        entryName = entry.Name;
                    }

                    if (Path.IsPathRooted(entryName))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(Path.Combine(fullName, entryName));

                    if (fullPath.StartsWith(fullNameWithSeparator, StringComparison.Ordinal))
                    {
                        if (Path.GetFileName(fullPath).Length == 0)
                        {
                            if (entry.Length == 0)
                            {
                                Directory.CreateDirectory(fullPath);
                            }
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                            ExtractToFile(entry, fullPath, true);
                        }
                    }
                }
            }
        }

        // For an archive from another machine. Only entries named exactly in allowedEntries reach the reader, each capped at its limit
        // however large its header claims to be. Nothing is written from an entry's own name, so a traversal name has nowhere to go.
        // Returns the names of the entries it passed over.
        public static List<string> ReadEntries(string archivePath, IReadOnlyDictionary<string, long> allowedEntries, Action<string, Stream> read)
        {
            // Compressed data is never larger than this, give or take the zip's own headers.
            long maxArchiveSize = allowedEntries.Values.Sum() + 1_000_000;

            if (new FileInfo(archivePath).Length > maxArchiveSize)
            {
                throw new InvalidDataException(Localization.Strings.ZipManager_Archive_too_large);
            }

            List<string> skipped = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > MaxForeignArchiveEntries)
                {
                    throw new InvalidDataException(Localization.Strings.ZipManager_Archive_too_large);
                }

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!allowedEntries.TryGetValue(entry.FullName, out long maxSize))
                    {
                        skipped.Add(entry.FullName);
                        continue;
                    }

                    if (!seen.Add(entry.FullName))
                    {
                        throw new InvalidDataException($"The archive has more than one {entry.FullName}.");
                    }

                    if (entry.Length > maxSize)
                    {
                        throw new InvalidDataException(Localization.Strings.ZipManager_Archive_too_large + "\r\n\r\n" + entry.FullName + "\r\n" +
                            string.Format(Localization.Strings.ZipManager_Maximum_allowed_file_size, maxSize.ToSizeString()));
                    }

                    using (MaxLengthStream stream = new MaxLengthStream(entry.Open(), maxSize))
                    {
                        read(entry.FullName, stream);
                    }
                }
            }

            return skipped;
        }

        private static void ExtractToFile(ZipArchiveEntry source, string destinationFileName, bool overwrite)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (destinationFileName == null)
            {
                throw new ArgumentNullException(nameof(destinationFileName));
            }

            FileMode fMode = overwrite ? FileMode.Create : FileMode.CreateNew;

            using (FileStream fs = new FileStream(destinationFileName, fMode, FileAccess.Write, FileShare.None, bufferSize: 0x1000, useAsync: false))
            using (Stream es = source.Open())
            using (MaxLengthStream maxLengthStream = new MaxLengthStream(es, source.Length))
            {
                maxLengthStream.CopyTo(fs);
            }

            File.SetLastWriteTime(destinationFileName, source.LastWriteTime.DateTime);
        }

        public static void Compress(string source, string archivePath, CompressionLevel compression = CompressionLevel.Optimal)
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            ZipFile.CreateFromDirectory(source, archivePath, compression, false);
        }

        // unixCreateMode is applied at creation, so the file is never readable by others while it is being written.
        public static void Compress(string archivePath, List<ZipEntryInfo> entries, CompressionLevel compression = CompressionLevel.Optimal, UnixFileMode? unixCreateMode = null)
        {
            FileHelpers.CreateDirectoryFromFilePath(archivePath);

            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            FileStreamOptions options = new FileStreamOptions() { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = unixCreateMode };

            using (FileStream stream = new FileStream(archivePath, options))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (ZipEntryInfo entry in entries)
                {
                    archive.CreateEntry(entry, compression);
                }
            }
        }

        private static ZipArchiveEntry CreateEntry(this ZipArchive archive, ZipEntryInfo entryInfo, CompressionLevel compressionLevel)
        {
            if (entryInfo == null)
            {
                throw new ArgumentNullException(nameof(entryInfo));
            }

            if (entryInfo.Data != null)
            {
                using (entryInfo.Data)
                {
                    return archive.CreateEntryFromStream(entryInfo.Data, entryInfo.EntryName, compressionLevel);
                }
            }
            else if (!string.IsNullOrEmpty(entryInfo.SourcePath) && File.Exists(entryInfo.SourcePath))
            {
                return archive.CreateEntryFromFile(entryInfo.SourcePath, entryInfo.EntryName, compressionLevel);
            }

            return null;
        }

        private static ZipArchiveEntry CreateEntryFromStream(this ZipArchive archive, Stream stream, string entryName, CompressionLevel compressionLevel)
        {
            if (archive == null)
            {
                throw new ArgumentNullException(nameof(archive));
            }

            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (entryName == null)
            {
                throw new ArgumentNullException(nameof(entryName));
            }

            ZipArchiveEntry entry = archive.CreateEntry(entryName, compressionLevel);
            entry.LastWriteTime = DateTime.Now;

            using (Stream entryStream = entry.Open())
            {
                stream.Position = 0;
                stream.CopyTo(entryStream);
            }

            return entry;
        }
    }
}
