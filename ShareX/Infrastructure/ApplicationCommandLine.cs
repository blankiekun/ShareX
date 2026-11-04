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

using System;
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

        if (args.Length == 0)
        {
            MainWindowIntegration.Activate();
        }
        else if (MainWindowIntegration.IsVisible)
        {
            MainWindowIntegration.Activate();
        }

        ShareXCLIManager commandLine = new(args);
        commandLine.ParseCommands();
        await commandLine.UseCommandLineArgs();
    }
}
