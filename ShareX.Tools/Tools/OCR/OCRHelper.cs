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
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bitmap = SkiaSharp.SKBitmap;
using ImageFormat = SkiaSharp.SKEncodedImageFormat;

namespace ShareX.Tools;

/// <summary>Text recognition through the host's tesseract command, a host dependency like ffmpeg and gpu-screen-recorder.</summary>
public static class OCRHelper
{
    private const string ExecutableName = "tesseract";
    private const string FallbackLanguage = "eng";

    // Not a language: orientation and script detection data, which recognizes nothing.
    private const string DetectionData = "osd";

    private const float MaximumScale = 4f;

    // A 4x OCR of a multi-monitor desktop would otherwise allocate gigabytes. This is 256 MB of pixels.
    private const long MaximumPixels = 64_000_000;

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RecognizeTimeout = TimeSpan.FromMinutes(2);

    public static bool IsSupported => FindTool(Environment.GetEnvironmentVariable("PATH")) != null;

    public static OCRLanguageOption[] AvailableLanguages => ListLanguages(RequireTool(Environment.GetEnvironmentVariable("PATH")));

    public static void ThrowIfNotSupported() => RequireTool(Environment.GetEnvironmentVariable("PATH"));

    public static async Task<string> OCR(Bitmap bitmap, string languageTag = FallbackLanguage, float scaleFactor = 1f,
        bool singleLine = false)
    {
        string tool = RequireTool(Environment.GetEnvironmentVariable("PATH"));
        byte[] image = await Task.Run(() => EncodeImage(bitmap, scaleFactor));
        return await Recognize(tool, image, NormalizeLanguage(languageTag), singleLine);
    }

    /// <summary>The saved language if it's installed, otherwise English, otherwise whatever is. Null when nothing is installed.</summary>
    public static OCRLanguageOption? PickLanguage(IEnumerable<OCRLanguageOption> languages, string? saved)
    {
        string wanted = NormalizeLanguage(saved);

        return languages.FirstOrDefault(x => x.LanguageTag.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault(x => x.LanguageTag == FallbackLanguage)
            ?? languages.FirstOrDefault();
    }

    /// <summary>
    /// Settings saved before Tesseract hold Windows tags such as "en" or "ja-JP", where Tesseract wants "eng" or "jpn".
    /// shortcut: Chinese maps to "zho", but Tesseract calls it chi_sim and chi_tra, so an old Chinese setting reports missing data.
    /// </summary>
    internal static string NormalizeLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return FallbackLanguage;
        }

        if (tag.Length != 2 && !tag.Contains('-'))
        {
            return tag;
        }

        try
        {
            return new CultureInfo(tag).ThreeLetterISOLanguageName;
        }
        catch (CultureNotFoundException)
        {
            return tag;
        }
    }

    /// <summary>The path of the first executable named tesseract in a PATH value. The working directory is never searched.</summary>
    internal static string? FindTool(string? searchPath)
    {
        foreach (string directory in (searchPath ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, ExecutableName);

            if (File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    internal static string RequireTool(string? searchPath)
    {
        return FindTool(searchPath) ?? throw new InvalidOperationException(Localization.Strings.OCRHelper_Tesseract_missing);
    }

    internal static OCRLanguageOption[] ListLanguages(string tool)
    {
        // Task.Run because this is called on the UI thread, where waiting for an await that needs the same thread would deadlock.
        (int exitCode, string output, string error) = Task.Run(() => RunAsync(tool, ["--list-langs"], null, ListTimeout)).GetAwaiter().GetResult();

        if (exitCode != 0)
        {
            throw new InvalidOperationException(string.Format(Localization.Strings.OCRHelper_Failed, exitCode, error.Trim()));
        }

        OCRLanguageOption[] languages = ParseLanguages(output).Select(x => new OCRLanguageOption(x, x)).ToArray();

        return languages.Length > 0 ? languages : throw new InvalidOperationException(Localization.Strings.OCRHelper_No_languages);
    }

    /// <summary>Reads the output of --list-langs: a header line, then one language per line.</summary>
    internal static string[] ParseLanguages(string output)
    {
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SkipWhile(x => !x.StartsWith("List of available languages", StringComparison.Ordinal))
            .Skip(1)
            .Where(x => x != DetectionData)
            .ToArray();
    }

    internal static async Task<string> Recognize(string tool, byte[] image, string language, bool singleLine, TimeSpan? timeout = null)
    {
        (int exitCode, string output, string error) = await RunAsync(tool, ["stdin", "stdout", "-l", language], image, timeout ?? RecognizeTimeout);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(IsLanguageDataError(error)
                ? string.Format(Localization.Strings.OCRHelper_Language_data_missing, language)
                : string.Format(Localization.Strings.OCRHelper_Failed, exitCode, error.Trim()));
        }

        return ParseText(output, language, singleLine);
    }

    private static bool IsLanguageDataError(string error)
    {
        return error.Contains("Failed loading language", StringComparison.Ordinal) ||
            error.Contains("couldn't load any languages", StringComparison.Ordinal);
    }

    /// <summary>Tesseract ends each page with a form feed and separates paragraphs with blank lines. Neither belongs in the text.</summary>
    internal static string ParseText(string output, string language, bool singleLine)
    {
        IEnumerable<string> lines = output.Split(['\n', '\f'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Chinese and Japanese are written without spaces, but Tesseract puts them between characters.
        if (language.StartsWith("jpn", StringComparison.Ordinal) || language.StartsWith("chi_", StringComparison.Ordinal))
        {
            lines = lines.Select(x => x.Replace(" ", ""));
        }

        return string.Join(singleLine ? " " : Environment.NewLine, lines);
    }

    private static byte[] EncodeImage(Bitmap bitmap, float scaleFactor)
    {
        double scale = Math.Min(Math.Clamp(scaleFactor, 1f, MaximumScale), Math.Sqrt(MaximumPixels / (double)(bitmap.Width * bitmap.Height)));
        using MemoryStream stream = new();

        if (scale > 1)
        {
            using Bitmap scaled = SkiaImageHelpers.ScaleImageFast(bitmap, scale);
            scaled.Save(stream, ImageFormat.Png);
        }
        else
        {
            bitmap.Save(stream, ImageFormat.Png);
        }

        return stream.ToArray();
    }

    // shortcut: a rerun leaves the previous run going until it finishes, because OCRRecognitionHandler has no cancellation token.
    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string tool, string[] arguments, byte[]? input, TimeSpan timeout)
    {
        ProcessStartInfo startInfo = new(tool, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // A host binary links host libraries, so it must not inherit an AppImage's library path.
        startInfo.Environment.Remove("LD_LIBRARY_PATH");

        using Process process = new() { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException(string.Format(Localization.Strings.OCRHelper_Failed, e.NativeErrorCode, e.Message), e);
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource watchdog = new(timeout);

        try
        {
            try
            {
                if (input != null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, watchdog.Token);
                }

                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Tesseract exits without reading the image when the language data is missing. The exit code and stderr say why.
            }

            await process.WaitForExitAsync(watchdog.Token);
            return (process.ExitCode, await output.WaitAsync(watchdog.Token), await error.WaitAsync(watchdog.Token));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException(string.Format(Localization.Strings.OCRHelper_Timed_out, timeout.TotalSeconds));
        }
    }
}
