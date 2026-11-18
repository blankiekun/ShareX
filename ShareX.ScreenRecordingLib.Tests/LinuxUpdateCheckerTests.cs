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
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public sealed class LinuxUpdateCheckerTests
{
    // Exposes the protected parser; the releases mirror what release.yml publishes.
    private sealed class Probe : LinuxUpdateChecker
    {
        public bool Parse(string tag, params string[] assets) => UpdateReleaseInfo(new GitHubRelease
        {
            tag_name = tag,
            assets = Array.ConvertAll(assets, name => new GitHubAsset { name = name, browser_download_url = "https://example.invalid/" + name })
        }, true, true);
    }

    [Fact]
    public void ReadsTheForkRelease()
    {
        Probe probe = new();
        Assert.True(probe.Parse("v22.4.0-linux", "ShareX-22.4.0-linux-x64.tar.gz.sha256", "ShareX-22.4.0-linux-x64.tar.gz"));
        Assert.Equal(new Version(22, 4, 0), probe.LatestVersion);
        Assert.Equal("https://example.invalid/ShareX-22.4.0-linux-x64.tar.gz", probe.DownloadURL);
    }

    [Theory]
    [InlineData("v22.4.0")]         // an upstream tag, with Windows assets
    [InlineData("v22.4.0-linux2")]
    [InlineData("vnext-linux")]
    public void IgnoresOtherTags(string tag)
    {
        Assert.False(new Probe().Parse(tag, "ShareX-22.4.0-linux-x64.tar.gz"));
    }

    [Fact]
    public void IgnoresAReleaseWithoutTheTarball()
    {
        Assert.False(new Probe().Parse("v22.4.0-linux", "ShareX-22.4.0-setup-x64.exe"));
    }
}
