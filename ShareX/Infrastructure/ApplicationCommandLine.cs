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

using ShareX.HelpersLib;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShareX;

internal static class ApplicationCommandLine
{
    private static ShareXCLIManager? _initial;

    private static ShareXCLIManager Initial => _initial ??
        throw new InvalidOperationException("Command-line processing has not been initialized.");

    internal static void Initialize(string[] args)
    {
        _initial = new ShareXCLIManager(args);
        _initial.ParseCommands();
        StartupOptions.AutoClose = _initial.IsCommandExist("AutoClose");
    }

    internal static bool IsCommandPresent(params string[] commands) => Initial.IsCommandExist(commands);

    internal static bool HasInitialScreenJob() => Initial.HasScreenJob();

    // Before anything else starts: no log, settings, mutex or pipe, and a running ShareX is not told.
    internal static bool TryHandleHelp()
    {
        if (!IsCommandPresent("--help", "-h"))
        {
            return false;
        }

        Console.Out.Write(BuildUsage());
        return true;
    }

    private static string BuildUsage()
    {
        StringBuilder usage = new("""
            Usage: sharex [options] [file or URL ...]

            Files and URLs are uploaded. Options take one dash and ignore case. A word after an option is that option's value, not a file.

            Startup:
              -silent, -s                  Start with the main window hidden
              -multi, -m                   Allow more than one ShareX at a time
              -portable, -p                Keep settings in a ShareXData folder by the program
              -sandbox                     Don't load or save settings or history
              -NoHotkeys                   Don't warn about hotkeys that failed to register
              -AutoClose                   Exit once all tasks have finished
              -install                     Add the launcher, file types and context menu entries, then exit
              -uninstall                   Remove them, then exit
              --help, -h                   Show this, then exit

            Actions:
              -task <name>                 Use this hotkey's task settings for the files and URLs given
              -workflow <name>             Run the hotkey with this name
              -CustomUploader <file.sxcu>  Import a custom uploader
              -ImageEffect <file.sxie>     Import an image effect
              -NativeMessagingInput <file.json>  Handle a request from the browser extension
              -<job> [file]                Run a job, on the file if the job takes one

            Jobs:

            """.ReplaceLineEndings());

        const int lineWidth = 78;
        int column = 0;

        foreach (HotkeyType job in Helpers.GetEnums<HotkeyType>().Where(x => x != HotkeyType.None))
        {
            string name = job.ToString();

            if (column > 0 && column + name.Length + 1 > lineWidth)
            {
                usage.AppendLine();
                column = 0;
            }

            usage.Append(column == 0 ? "  " : " ").Append(name);
            column += name.Length + (column == 0 ? 2 : 1);
        }

        return usage.AppendLine().ToString();
    }

    internal static bool TryHandleInstall() => TryHandleCommand("install", IntegrationHelpers.Install);

    internal static bool TryHandleUninstall() => TryHandleCommand("uninstall", IntegrationHelpers.Uninstall);

    // Both run before the single-instance check, so a running ShareX is not asked to do them.
    private static bool TryHandleCommand(string command, Action action)
    {
        if (!IsCommandPresent(command))
        {
            return false;
        }

        try
        {
            action();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ShareX -{command} failed: {e.Message}");
            Environment.ExitCode = 1;
        }

        return true;
    }

    internal static Task ExecuteInitialAsync() => Initial.UseCommandLineArgs();

    internal static async Task ExecuteReceivedAsync(string[]? args)
    {
        args ??= [];

        ShareXCLIManager commandLine = new(args);
        commandLine.ParseCommands();

        if (args.Length == 0 || (MainWindowIntegration.IsVisible && !commandLine.HasScreenJob()))
        {
            MainWindowIntegration.Activate();
        }

        await commandLine.UseCommandLineArgs();
    }
}
