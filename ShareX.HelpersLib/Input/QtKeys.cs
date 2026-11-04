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

#nullable enable

namespace ShareX.HelpersLib;

/// <summary>Turns a ShareX hotkey into the Qt key number KGlobalAccel stores (Qt::Key plus Qt::KeyboardModifier bits).</summary>
public static class QtKeys
{
    private const int Shift = 0x02000000;
    private const int Control = 0x04000000;
    private const int Alt = 0x08000000;
    private const int Meta = 0x10000000;
    private const int Keypad = 0x20000000;

    /// <summary>The Qt key for this hotkey, or 0 when it has no Qt form and so can't be registered.</summary>
    public static int FromHotkey(InputKey hotkey, bool win)
    {
        InputKey keyCode = hotkey & InputKey.KeyCode;
        bool shift = hotkey.HasFlag(InputKey.Shift);
        int modifiers = (hotkey.HasFlag(InputKey.Control) ? Control : 0) | (hotkey.HasFlag(InputKey.Alt) ? Alt : 0) | (win ? Meta : 0);

        // KWin matches a shifted symbol by the character it types, with no Shift bit: it fired Ctrl+! for
        // Ctrl+Shift+1 and never Ctrl+Shift+1 (KWin 6.8). Letters and named keys keep Shift.
        // shortcut: this table assumes a US layout, so on another layout such a hotkey registers but doesn't fire.
        if (shift)
        {
            int symbol = ShiftedSymbol(keyCode);
            if (symbol != 0) return modifiers | symbol;
        }

        int key = KeyCodeToQt(keyCode);
        return key == 0 ? 0 : modifiers | (shift ? Shift : 0) | key;
    }

    private static int KeyCodeToQt(InputKey key)
    {
        if (key is >= InputKey.A and <= InputKey.Z or >= InputKey.D0 and <= InputKey.D9) return (int)key;
        if (key is >= InputKey.F1 and <= InputKey.F24) return 0x01000030 + (key - InputKey.F1);
        if (key is >= InputKey.NumPad0 and <= InputKey.NumPad9) return Keypad | ('0' + (key - InputKey.NumPad0));

        return key switch
        {
            InputKey.Escape => 0x01000000,
            InputKey.Tab => 0x01000001,
            InputKey.Back => 0x01000003,
            InputKey.Return => 0x01000004,
            InputKey.Insert => 0x01000006,
            InputKey.Delete => 0x01000007,
            InputKey.Pause => 0x01000008,
            InputKey.PrintScreen => 0x01000009,
            InputKey.Clear => 0x0100000b,
            InputKey.Home => 0x01000010,
            InputKey.End => 0x01000011,
            InputKey.Left => 0x01000012,
            InputKey.Up => 0x01000013,
            InputKey.Right => 0x01000014,
            InputKey.Down => 0x01000015,
            InputKey.PageUp => 0x01000016,
            InputKey.PageDown => 0x01000017,
            InputKey.CapsLock => 0x01000024,
            InputKey.NumLock => 0x01000025,
            InputKey.Scroll => 0x01000026,
            InputKey.Apps => 0x01000055,
            InputKey.Help => 0x01000058,
            InputKey.BrowserBack => 0x01000061,
            InputKey.BrowserForward => 0x01000062,
            InputKey.BrowserStop => 0x01000063,
            InputKey.BrowserRefresh => 0x01000064,
            InputKey.VolumeDown => 0x01000070,
            InputKey.VolumeMute => 0x01000071,
            InputKey.VolumeUp => 0x01000072,
            InputKey.MediaStop => 0x01000081,
            InputKey.MediaPreviousTrack => 0x01000082,
            InputKey.MediaNextTrack => 0x01000083,
            InputKey.MediaPlayPause => 0x01000086,
            InputKey.BrowserHome => 0x01000090,
            InputKey.BrowserFavorites => 0x01000091,
            InputKey.BrowserSearch => 0x01000092,
            InputKey.LaunchMail => 0x010000a0,
            InputKey.SelectMedia => 0x010000a1,
            InputKey.LaunchApplication1 => 0x010000a2,
            InputKey.LaunchApplication2 => 0x010000a3,
            InputKey.Select => 0x01010000,
            InputKey.Print => 0x01020002,
            InputKey.Execute => 0x01020003,
            InputKey.Sleep => 0x01020004,
            InputKey.Play => 0x01020005,
            InputKey.Zoom => 0x01020006,
            InputKey.Space => ' ',
            InputKey.Multiply => Keypad | '*',
            InputKey.Add => Keypad | '+',
            InputKey.Separator => Keypad | ',',
            InputKey.Subtract => Keypad | '-',
            InputKey.Decimal => Keypad | '.',
            InputKey.Divide => Keypad | '/',
            InputKey.OemSemicolon => ';',
            InputKey.Oemplus => '=',
            InputKey.Oemcomma => ',',
            InputKey.OemMinus => '-',
            InputKey.OemPeriod => '.',
            InputKey.OemQuestion => '/',
            InputKey.Oemtilde => '`',
            InputKey.OemOpenBrackets => '[',
            InputKey.OemPipe => '\\',
            InputKey.OemCloseBrackets => ']',
            InputKey.OemQuotes => '\'',
            _ => 0
        };
    }

    private static int ShiftedSymbol(InputKey key) => key switch
    {
        InputKey.D1 => '!',
        InputKey.D2 => '@',
        InputKey.D3 => '#',
        InputKey.D4 => '$',
        InputKey.D5 => '%',
        InputKey.D6 => '^',
        InputKey.D7 => '&',
        InputKey.D8 => '*',
        InputKey.D9 => '(',
        InputKey.D0 => ')',
        InputKey.OemSemicolon => ':',
        InputKey.Oemplus => '+',
        InputKey.Oemcomma => '<',
        InputKey.OemMinus => '_',
        InputKey.OemPeriod => '>',
        InputKey.OemQuestion => '?',
        InputKey.Oemtilde => '~',
        InputKey.OemOpenBrackets => '{',
        InputKey.OemPipe => '|',
        InputKey.OemCloseBrackets => '}',
        InputKey.OemQuotes => '"',
        _ => 0
    };
}
