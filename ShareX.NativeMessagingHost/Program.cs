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
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ShareX.NativeMessagingHost
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            // A browser always passes the extension's origin or id.
            if (args.Length == 0)
            {
                Console.Error.WriteLine("This executable is used to receive data from browser addon and send it to ShareX.");
                return 1;
            }

            try
            {
                HelpersLib.NativeMessagingHost host = new(Console.OpenStandardInput(), Console.OpenStandardOutput());
                string input = host.Read();

                if (!string.IsNullOrEmpty(input))
                {
                    host.Write(input);
                    StartShareX(WriteInputFile(input));
                }

                return 0;
            }
            catch (Exception e)
            {
                // The browser keeps a host's stderr in its own log. A window would open from the browser with no context.
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        // Page text and addresses can be private and the temp folder is shared, so only this user can read the file,
        // and CreateNew won't follow a link someone placed at that name.
        private static string WriteInputFile(string input)
        {
            string path = Path.Combine(Path.GetTempPath(), $"ShareX-{Guid.NewGuid():N}.json");

            using FileStream stream = new(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            stream.Write(new UTF8Encoding(false).GetBytes(input));

            return path;
        }

        // A ShareX started here can outlive the host. setsid takes it out of the host's process group, which the browser may kill, and the
        // redirects keep it off the host's stdin and stdout, which are the browser's connection.
        private static void StartShareX(string inputPath)
        {
            ProcessStartInfo startInfo = new("setsid")
            {
                ArgumentList = { FileHelpers.GetAbsolutePath("ShareX"), "-NativeMessagingInput", inputPath },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                using Process process = Process.Start(startInfo);
            }
            catch
            {
                File.Delete(inputPath);
                throw;
            }
        }
    }
}
