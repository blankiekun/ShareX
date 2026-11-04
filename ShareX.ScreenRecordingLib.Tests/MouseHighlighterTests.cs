using ShareX.HelpersLib;
using ShareX.Tools;
using System.Drawing;
using System.Globalization;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class MouseHighlighterTests
{
    // Catches: reading KWin's fractional position with the desktop's culture. "539.5" fails to parse under a comma-decimal
    // locale, or reads as 5395, and the highlight stops following or jumps off the screen.
    [Fact]
    public void TryParsePosition_IgnoresTheDesktopCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.True(KWinCursorWatcher.TryParsePosition("539.5,-400", out Point position));
            Assert.Equal(new Point(540, -400), position);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Catches: settings saved when transparent was the default, with only clicks drawn. Those would now start the tool and draw nothing.
    [Fact]
    public void Validate_ReplacesATransparentColorFromOldSettings()
    {
        MouseHighlighterOptions options = new() { AlwaysColor = Color.Transparent };

        options.Validate();

        Assert.NotEqual(0, options.AlwaysColor.A);
    }
}
