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

using System.IO;
using System.Linq;
using System.Text;

namespace ShareX.HelpersLib
{
    public static class DesktopEntryHelpers
    {
        /// <summary>
        /// Quotes one argument of a desktop entry's Exec line. The file format unescapes a backslash before Exec's own quoting is read,
        /// so each backslash Exec needs is written twice, and a literal percent sign is doubled so it isn't read as a field code.
        /// </summary>
        public static string QuoteExecArgument(string argument)
        {
            StringBuilder quoted = new StringBuilder("\"");

            foreach (char c in argument)
            {
                switch (c)
                {
                    case '\\':
                        quoted.Append(@"\\\\");
                        break;
                    case '"':
                    case '`':
                    case '$':
                        quoted.Append(@"\\").Append(c);
                        break;
                    case '%':
                        quoted.Append("%%");
                        break;
                    default:
                        quoted.Append(c);
                        break;
                }
            }

            return quoted.Append('"').ToString();
        }

        /// <summary>Joins with LF whatever line endings the source file has, and ends the file with one.</summary>
        public static string Format(params string[] lines)
        {
            return string.Join('\n', lines) + "\n";
        }

        public static bool HasExecLine(string path, string exec)
        {
            return File.Exists(path) && File.ReadLines(path).Contains("Exec=" + exec);
        }

        /// <summary>File.Delete throws when the folder is missing as well, which is the usual state before anything was installed.</summary>
        public static void Delete(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static void Write(string path, string contents, bool executable = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);

            if (executable)
            {
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            }
        }
    }
}
