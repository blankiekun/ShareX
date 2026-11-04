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

public sealed class KGlobalAccelHotkeyTests
{
    private const int Shift = 0x02000000, Ctrl = 0x04000000, Alt = 0x08000000, Meta = 0x10000000, Keypad = 0x20000000;
    private const int QtPrint = 0x01000009, QtPrinter = 0x01020002;

    // The numbers come from Qt's qnamespace.h. A wrong one registers a shortcut for some other key without any error.
    [Theory]
    [InlineData(InputKey.A, false, 0x41)]
    [InlineData(InputKey.Z, false, 0x5a)]
    [InlineData(InputKey.D0, false, 0x30)]
    [InlineData(InputKey.F1, false, 0x01000030)]
    [InlineData(InputKey.F12, false, 0x0100003b)]
    [InlineData(InputKey.F24, false, 0x01000047)]
    [InlineData(InputKey.Return, false, 0x01000004)]
    [InlineData(InputKey.PageDown, false, 0x01000017)]
    [InlineData(InputKey.Scroll, false, 0x01000026)]
    [InlineData(InputKey.OemQuestion, false, '/')]
    [InlineData(InputKey.Control | InputKey.PrintScreen, false, Ctrl | QtPrint)]
    [InlineData(InputKey.Alt | InputKey.PrintScreen, false, Alt | QtPrint)]
    [InlineData(InputKey.Control | InputKey.Shift | InputKey.PrintScreen, false, Ctrl | Shift | QtPrint)]
    [InlineData(InputKey.Control | InputKey.F, true, Ctrl | Meta | 0x46)]
    public void Maps_hotkey_to_qt_key(InputKey hotkey, bool win, int expected)
    {
        Assert.Equal(expected, QtKeys.FromHotkey(hotkey, win));
    }

    // Catches treating VK_PRINT as the Print Screen key: its InputKey is called Print, and Qt's Print is VK_SNAPSHOT.
    [Fact]
    public void Print_screen_is_not_the_printer_key()
    {
        Assert.Equal(QtPrint, QtKeys.FromHotkey(InputKey.PrintScreen, false));
        Assert.Equal(QtPrinter, QtKeys.FromHotkey(InputKey.Print, false));
    }

    // Catches a Shift bit on shifted symbols. KWin 6.8 fired Ctrl+! for Ctrl+Shift+1 and never fired Ctrl+Shift+1 or Ctrl+Shift+!,
    // so such a hotkey would register fine and do nothing.
    [Theory]
    [InlineData(InputKey.Control | InputKey.Shift | InputKey.D1, Ctrl | '!')]
    [InlineData(InputKey.Control | InputKey.Shift | InputKey.OemSemicolon, Ctrl | ':')]
    [InlineData(InputKey.Shift | InputKey.Oemplus, '+')]
    public void Shifted_symbols_lose_the_shift_bit(InputKey hotkey, int expected)
    {
        Assert.Equal(expected, QtKeys.FromHotkey(hotkey, false));
    }

    // Catches the opposite mistake: letters and named keys do fire with Shift, and dropping it would take the unshifted key.
    [Theory]
    [InlineData(InputKey.Control | InputKey.Shift | InputKey.K, Ctrl | Shift | 0x4b)]
    [InlineData(InputKey.Control | InputKey.Shift | InputKey.F5, Ctrl | Shift | 0x01000034)]
    public void Letters_and_named_keys_keep_shift(InputKey hotkey, int expected)
    {
        Assert.Equal(expected, QtKeys.FromHotkey(hotkey, false));
    }

    // Catches numpad keys mapped like the top row: KWin only fires them with the keypad bit, and the top-row key would be taken instead.
    [Theory]
    [InlineData(InputKey.NumPad1, Keypad | '1')]
    [InlineData(InputKey.Add, Keypad | '+')]
    public void Numpad_keys_carry_the_keypad_bit(InputKey hotkey, int expected)
    {
        Assert.Equal(expected, QtKeys.FromHotkey(hotkey, false));
    }

    // Catches modifiers reaching KGlobalAccel with no key: it would store a shortcut made of Ctrl alone. 0 makes the hotkey Failed.
    [Theory]
    [InlineData(InputKey.Control | InputKey.ControlKey)]
    [InlineData(InputKey.LWin)]
    [InlineData(InputKey.LButton)]
    [InlineData(InputKey.None)]
    public void Keys_with_no_qt_form_give_zero(InputKey hotkey)
    {
        Assert.Equal(0, QtKeys.FromHotkey(hotkey, false));
    }

    // Catches trusting the read-back alone: KGlobalAccel 6.8 returned the requested key to a second action while the
    // first kept it and kept firing, so a stolen-looking key would show as Registered and never trigger.
    [Fact]
    public void Read_back_is_not_enough_when_another_action_holds_the_key()
    {
        KeyHolder[] holders = [new("org.kde.spectacle.desktop", "_launch"), new("sharex", "hotkey-1")];

        Assert.False(KGlobalAccelHotkeyHost.HoldsKeyAlone([QtPrint], QtPrint, holders, "sharex", "hotkey-1"));
    }

    // Catches ignoring what came back: a daemon that drops the key must not count as success just because nobody else holds it.
    [Fact]
    public void Key_missing_from_the_read_back_is_a_failure()
    {
        KeyHolder[] holders = [new("sharex", "hotkey-1")];

        Assert.False(KGlobalAccelHotkeyHost.HoldsKeyAlone([], QtPrint, holders, "sharex", "hotkey-1"));
        Assert.True(KGlobalAccelHotkeyHost.HoldsKeyAlone([QtPrint], QtPrint, holders, "sharex", "hotkey-1"));
    }

    // Catches a limiter that drops the first press. On Windows the timer started with the host; a press soon after start must still work.
    [Fact]
    public void First_press_is_allowed_at_any_time()
    {
        Assert.True(new HotkeyRepeatLimiter().Allow(0, 500));
    }

    // Catches a limiter that restarts its window on dropped presses, which would lock out a steady hold or fast tapping forever.
    [Fact]
    public void Dropped_presses_do_not_extend_the_limit()
    {
        HotkeyRepeatLimiter limiter = new();

        Assert.True(limiter.Allow(1000, 500));
        Assert.False(limiter.Allow(1200, 500));
        Assert.False(limiter.Allow(1499, 500));
        Assert.True(limiter.Allow(1500, 500));
    }
}
