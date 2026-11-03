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
using System.Linq;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    public class CaptureCustomWindow : CaptureWindow
    {
        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            string windowTitle = taskSettings.CaptureSettings.CaptureCustomWindow;

            if (!string.IsNullOrEmpty(windowTitle))
            {
                KWinWindow window = KWinDesktop.Query().Windows.FirstOrDefault(x => x.Caption.Contains(windowTitle, StringComparison.OrdinalIgnoreCase));

                if (window == null)
                {
                    MessageBox.Show(Strings.UnableToFindAWindowWithSpecifiedWindowTitle, "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    WindowId = window.Id;

                    return base.Execute(taskSettings);
                }
            }

            return null;
        }
    }
}
