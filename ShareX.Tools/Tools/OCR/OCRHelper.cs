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

using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX.Tools;

// shortcut: OCR always fails because Windows.Media.Ocr does not exist on Linux, replace with Tesseract in M7.
public static class OCRHelper
{
    private const string Unavailable = "OCR is not available on Linux yet.";

    public static bool IsSupported => false;

    public static OCRLanguageOption[] AvailableLanguages => throw new InvalidOperationException(Unavailable);

    public static void ThrowIfNotSupported() => throw new InvalidOperationException(Unavailable);

    public static Task<string> OCR(Bitmap bitmap, string languageTag = "en", float scaleFactor = 1f,
        bool singleLine = false) => Task.FromException<string>(new InvalidOperationException(Unavailable));
}
