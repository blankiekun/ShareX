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
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class CLIManagerTests
{
    private static bool AsksForHelp(params string[] arguments)
    {
        CLIManager manager = new(arguments);
        Assert.True(manager.ParseCommands());
        // What ApplicationCommandLine.TryHandleHelp asks. --help parses to a command named "-help" and the lookup strips one dash, so the two have to agree.
        return manager.IsCommandExist("--help", "-h");
    }

    // Catches: the two-dash form not matching, which would start the whole app, as it did.
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--HELP")]
    public void Help_IsFound(string argument)
    {
        Assert.True(AsksForHelp(argument));
    }

    [Fact]
    public void Help_IsFoundAfterOtherArguments()
    {
        Assert.True(AsksForHelp("-silent", "--help"));
    }

    // Catches: a real start printing help instead. A file or an option's value named h or help isn't a request for help, nor is a job whose name starts with h.
    [Theory]
    [InlineData("h")]
    [InlineData("help")]
    [InlineData("-help")]
    [InlineData("-HashCheck")]
    public void Help_IsNotFound(string argument)
    {
        Assert.False(AsksForHelp(argument));
    }

    [Fact]
    public void Help_IsNotFoundInAnOptionsValue()
    {
        Assert.False(AsksForHelp("-task", "h"));
    }
}
