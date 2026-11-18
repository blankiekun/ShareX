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
using System.Linq;
using System.Runtime.InteropServices;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Checks this fork's releases: tag v22.4.0-linux with a ShareX-22.4.0-linux-x64.tar.gz asset (release.yml).
    /// The update opens that download in the browser, because there is no Linux installer to run.
    /// </summary>
    public class LinuxUpdateChecker : GitHubUpdateChecker
    {
        private const string TagSuffix = "-linux";

        public LinuxUpdateChecker() : base("blankiekun", "ShareX")
        {
            // Portable makes DownloadUpdateAsync open DownloadURL instead of downloading and running a setup .exe.
            IsPortable = true;
            IgnoreRevision = true;
        }

        protected override bool UpdateReleaseInfo(GitHubRelease release, bool isPortable, bool isBrowserDownloadURL)
        {
            string tag = release?.tag_name;

            if (tag == null || !tag.StartsWith('v') || !tag.EndsWith(TagSuffix, StringComparison.Ordinal) ||
                !Version.TryParse(tag[1..^TagSuffix.Length], out Version version))
            {
                return false;
            }

            string suffix = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-linux-arm64.tar.gz" : "-linux-x64.tar.gz";
            GitHubAsset asset = release.assets?.FirstOrDefault(x => x?.name?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) == true);

            if (asset == null)
            {
                return false;
            }

            LatestVersion = version;
            DownloadURL = asset.browser_download_url;
            IsPreRelease = release.prerelease;
            return true;
        }
    }
}
