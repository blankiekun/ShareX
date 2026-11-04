using ShareX.HelpersLib;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class DesktopEntryHelpersTests
{
    // Catches: an install folder with a space splitting the Exec line into two arguments, so the launcher and autostart run nothing.
    [Fact]
    public void QuoteExecArgument_KeepsASpacedPathInOneArgument()
    {
        Assert.Equal("\"/opt/My Apps/ShareX\"", DesktopEntryHelpers.QuoteExecArgument("/opt/My Apps/ShareX"));
    }

    // Catches: writing only one backslash for Exec's quoting. The file format consumes it first, and GLib then rejects the whole entry
    // for an invalid escape; a shell-style reader would expand $HOME inside the path instead.
    [Theory]
    [InlineData("/a$b", "\"/a\\\\$b\"")]
    [InlineData("/a\"b", "\"/a\\\\\"b\"")]
    [InlineData("/a`b", "\"/a\\\\`b\"")]
    [InlineData("/a\\b", "\"/a\\\\\\\\b\"")]
    public void QuoteExecArgument_EscapesTwiceForExecAndForTheFileFormat(string path, string expected)
    {
        Assert.Equal(expected, DesktopEntryHelpers.QuoteExecArgument(path));
    }

    // Catches: a percent sign in the path being read as a field code, which launchers replace or reject.
    [Fact]
    public void QuoteExecArgument_DoublesAPercentSign()
    {
        Assert.Equal("\"/a%%F/ShareX\"", DesktopEntryHelpers.QuoteExecArgument("/a%F/ShareX"));
    }
}
