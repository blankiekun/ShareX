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
using System.IO;
using System.Linq;

namespace ShareX
{
    public static class StartupManager
    {
        private static readonly string Exec = DesktopEntryHelpers.QuoteExecArgument(Environment.ProcessPath) + " -silent";

        private static string AutostartPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "autostart", IntegrationHelpers.DesktopFileId);

        public static StartupState State
        {
            get
            {
                // Hidden=true is how a desktop switches off an entry without deleting it. An entry for another copy of ShareX counts as off too.
                return DesktopEntryHelpers.HasExecLine(AutostartPath, Exec) && !File.ReadLines(AutostartPath).Contains("Hidden=true") ?
                    StartupState.Enabled : StartupState.Disabled;
            }
            set
            {
                if (value == StartupState.Enabled)
                {
                    DesktopEntryHelpers.Write(AutostartPath, DesktopEntryHelpers.Format(
                        "[Desktop Entry]",
                        "Type=Application",
                        "Name=ShareX",
                        $"Exec={Exec}",
                        "Icon=ShareX",
                        "Terminal=false",
                        "StartupWMClass=ShareX"));
                }
                else if (value == StartupState.Disabled)
                {
                    DesktopEntryHelpers.Delete(AutostartPath);
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
        }
    }
}
