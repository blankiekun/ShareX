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
using System;
using System.IO;

namespace ShareX
{
    public static class SystemOptions
    {
        // Only root can write here, so these can't be undone by the user they apply to. The registry's per-user fallback had no such
        // protection and isn't kept.
        internal const string FilePath = "/etc/ShareX/SystemOptions.json";

        public static bool DisableUpdateCheck { get; private set; }
        public static bool DisableUpload { get; private set; }
        public static bool DisableLogging { get; private set; }
        public static string PersonalPath { get; private set; }

        /// <summary>Why the file couldn't be read. It is read before the log exists, so <see cref="Program"/> logs this afterward.</summary>
        public static string ReadError { get; private set; }

        public static void UpdateSystemOptions()
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            try
            {
                // Not JsonHelpers: it hides syntax errors and returns what it read so far, so a broken file would look like one that restricts nothing.
                Options options = JsonConvert.DeserializeObject<Options>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("The file is empty.");
                DisableUpdateCheck = options.DisableUpdateCheck;
                DisableUpload = options.DisableUpload;
                DisableLogging = options.DisableLogging;
                PersonalPath = options.PersonalPath;
            }
            catch (Exception e)
            {
                // Someone put this file here to restrict ShareX, so a mistake in it shouldn't lift the restrictions.
                ReadError = $"{FilePath} could not be read, so uploads and update checks are off: {e.Message}";
                DisableUpdateCheck = true;
                DisableUpload = true;
            }
        }

        private sealed class Options
        {
            public bool DisableUpdateCheck { get; set; }
            public bool DisableUpload { get; set; }
            public bool DisableLogging { get; set; }
            public string PersonalPath { get; set; }
        }
    }
}