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
using System.Drawing;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class PortalPrintTests
{
    // Catches treating the portal's millimetres as points or hundredths of an inch, which would print A4 at the wrong scale.
    [Fact]
    public void A4InMillimetresBecomesHundredthsOfAnInch()
    {
        Assert.Equal(new Size(827, 1169), PortalPrint.PageSize(210, 297, "portrait"));
    }

    // Catches a landscape page that stays portrait when the backend reports paper as portrait plus an orientation, or
    // that turns sideways when the backend has already swapped the two.
    [Theory]
    [InlineData(210, 297, "landscape")]
    [InlineData(297, 210, "landscape")]
    [InlineData(210, 297, "reverse_landscape")]
    public void LandscapePageIsWiderThanTall(double width, double height, string orientation)
    {
        Assert.Equal(new Size(1169, 827), PortalPrint.PageSize(width, height, orientation));
    }

    // Catches a zero-sized PDF page, which Skia would refuse, when a backend leaves the size out of its answer.
    [Fact]
    public void MissingSizeFallsBackToTheDefault()
    {
        Assert.Equal(PortalPrint.DefaultPageSize, PortalPrint.PageSize(null, 297, null));
        Assert.Equal(PortalPrint.DefaultPageSize, PortalPrint.PageSize(0, 0, "portrait"));
    }
}
