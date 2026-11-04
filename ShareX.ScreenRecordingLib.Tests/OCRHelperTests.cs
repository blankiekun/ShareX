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

using ShareX.Tools;
using System.Diagnostics;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>The fake tesseract is passed by path and calls tools by full path, because FakeHost points the process-wide PATH at its own folder while these tests run.</summary>
public sealed class OCRHelperTests : IDisposable
{
    private const string RealMissingLanguageError = """
        Error opening data file /usr/share/tessdata/jpn.traineddata
        Please make sure the TESSDATA_PREFIX environment variable is set to your "tessdata" directory.
        Failed loading language 'jpn'
        Tesseract couldn't load any languages!
        Could not initialize tesseract.
        """;

    private readonly string _folder = Directory.CreateTempSubdirectory("sharex-ocr-test").FullName;

    public void Dispose() => Directory.Delete(_folder, true);

    private string FakeTesseract(string script)
    {
        string path = Path.Combine(_folder, "tesseract");
        // .cs files check out as CRLF, and a raw string keeps that. A shell reads the CR as part of the line, so > 'file' would write to "file\r".
        File.WriteAllText(path, "#!/bin/sh\n" + script.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // Catches the header being offered as a language, and osd being offered: it is detection data, and picking it gives garbage.
    [Fact]
    public void ParseLanguages_ReadsRealOutput_WithoutHeaderOrDetectionData()
    {
        string output = "List of available languages in \"/usr/share/tessdata/\" (3):\nafr\neng\nosd\n";

        Assert.Equal(["afr", "eng"], OCRHelper.ParseLanguages(output));
    }

    // Catches a warning printed before the header turning into a language in the list.
    [Fact]
    public void ParseLanguages_IgnoresLinesBeforeHeader()
    {
        Assert.Equal(["eng"], OCRHelper.ParseLanguages("Warning: something\nList of available languages (1):\neng\n"));
    }

    // Catches the form feed Tesseract ends every page with reaching the clipboard, and blank lines between paragraphs.
    [Fact]
    public void ParseText_DropsFormFeedAndBlankLines()
    {
        Assert.Equal("one" + Environment.NewLine + "two", OCRHelper.ParseText("one\n\ntwo\n\f", "eng", false));
    }

    // Catches the single line option being ignored, which the Windows path honored by joining lines with spaces.
    [Fact]
    public void ParseText_JoinsLinesWhenSingleLine()
    {
        Assert.Equal("one two", OCRHelper.ParseText("one\ntwo\n\f", "eng", true));
    }

    // Catches Japanese coming back as "日 本 語": Tesseract spaces out the characters, and the Windows path removed these too.
    [Fact]
    public void ParseText_RemovesSpacesFromJapaneseOnly()
    {
        Assert.Equal("日本語", OCRHelper.ParseText("日 本 語\n", "jpn", false));
        Assert.Equal("a b", OCRHelper.ParseText("a b\n", "eng", false));
    }

    // Catches saved Windows tags going to tesseract as they are, where -l en fails with a misleading "install tesseract-data-en".
    [Theory]
    [InlineData("en", "eng")]
    [InlineData("ja-JP", "jpn")]
    [InlineData("de", "deu")]
    [InlineData("eng", "eng")]
    [InlineData("chi_sim", "chi_sim")]
    [InlineData("", "eng")]
    public void NormalizeLanguage_MapsWindowsTagsAndKeepsTesseractCodes(string tag, string expected)
    {
        Assert.Equal(expected, OCRHelper.NormalizeLanguage(tag));
    }

    // Catches the old "first in the list" fallback. Arch's tesseract pulls in tesseract-data-afr by default, so a saved "en" became Afrikaans.
    [Fact]
    public void PickLanguage_FallsBackToEnglishRatherThanFirstInList()
    {
        OCRLanguageOption afr = new("afr", "afr"), eng = new("eng", "eng"), jpn = new("jpn", "jpn");

        Assert.Equal(eng, OCRHelper.PickLanguage([afr, eng], "en"));
        Assert.Equal(eng, OCRHelper.PickLanguage([afr, eng], "jpn"));
        Assert.Equal(jpn, OCRHelper.PickLanguage([afr, eng, jpn], "jpn"));
        Assert.Equal(afr, OCRHelper.PickLanguage([afr], "jpn"));
    }

    // Catches a data file named tesseract, or an empty PATH, being treated as an installed tool.
    [Fact]
    public void RequireTool_NamesThePackages_WhenNothingExecutableIsOnPath()
    {
        File.WriteAllText(Path.Combine(_folder, "tesseract"), "not executable");
        File.SetUnixFileMode(Path.Combine(_folder, "tesseract"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Assert.Contains("pacman -S tesseract", Assert.Throws<InvalidOperationException>(() => OCRHelper.RequireTool(_folder)).Message);
        Assert.Throws<InvalidOperationException>(() => OCRHelper.RequireTool(null));
    }

    // Catches a blank window with nothing to pick: tesseract exits 0 with an empty list when no data package is installed.
    [Fact]
    public void ListLanguages_SaysWhatToInstall_WhenThereAreNone()
    {
        string tool = FakeTesseract("echo 'List of available languages in \"/x/\" (0):'");

        Assert.Contains("tesseract-data-eng", Assert.Throws<InvalidOperationException>(() => OCRHelper.ListLanguages(tool)).Message);
    }

    // Catches the image arriving damaged, the language not reaching -l, and a shell interpreting the language.
    [Fact]
    public async Task Recognize_PassesImageOnStdinAndArgumentsVerbatim()
    {
        string received = Path.Combine(_folder, "received"), arguments = Path.Combine(_folder, "arguments");
        string tool = FakeTesseract($"""
            printf '%s\n' "$@" > '{arguments}'
            /usr/bin/cat > '{received}'
            printf 'hello\n\f'
            """);
        byte[] image = new byte[1 << 20];
        Random.Shared.NextBytes(image);

        string text = await OCRHelper.Recognize(tool, image, "eng; touch pwned", false);

        Assert.Equal("hello", text);
        Assert.Equal(image, File.ReadAllBytes(received));
        Assert.Equal(["stdin", "stdout", "-l", "eng; touch pwned"], File.ReadAllLines(arguments));
    }

    // Catches the broken pipe from a tesseract that quits without reading the image hiding the reason, which only stderr has.
    // The image is larger than a pipe's buffer, so the write must fail or block if this is handled wrongly.
    [Fact]
    public async Task Recognize_SaysWhichPackage_WhenLanguageDataIsMissing()
    {
        string tool = FakeTesseract($"/usr/bin/cat >&2 <<'END'\n{RealMissingLanguageError}\nEND\nexit 1");

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OCRHelper.Recognize(tool, new byte[1 << 20], "jpn", false));

        Assert.Contains("tesseract-data-jpn", e.Message);
    }

    // Catches other failures being blamed on language data, or losing tesseract's own reason.
    [Fact]
    public async Task Recognize_ReportsOtherFailures_WithTesseractsReason()
    {
        string tool = FakeTesseract("/usr/bin/cat > /dev/null; echo 'Error during processing.' >&2; exit 3");

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(() => OCRHelper.Recognize(tool, [1], "eng", false));

        Assert.Contains("Error during processing.", e.Message);
        Assert.DoesNotContain("tesseract-data", e.Message);
    }

    // Catches a hung tesseract leaving the OCR window busy forever, or surviving the timeout.
    [Fact]
    public async Task Recognize_StopsTesseract_WhenItHangs()
    {
        string pidFile = Path.Combine(_folder, "pid");
        string tool = FakeTesseract($"echo $$ > '{pidFile}'; exec /usr/bin/sleep 30");

        InvalidOperationException e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OCRHelper.Recognize(tool, [1], "eng", false, TimeSpan.FromMilliseconds(300)));

        Assert.Contains("didn't finish", e.Message);
        int pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }
}
