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

using ShareX.AvaloniaUI.Integration;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

#if MicrosoftStore
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#endif

namespace ShareX;

internal static class Program
{
    private static Stopwatch _startTimer = null!;

    // The current log tightens itself, but older months' logs are never opened again. Runs with logging off too, as the old ones are still there.
    private static void MakeLogsPrivate()
    {
        string folder = AppPaths.LogsFolder;

        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.GetFiles(folder))
            {
                FileHelpers.TryMakePrivate(file);
            }

            FileHelpers.TryMakePrivate(folder, FileHelpers.PrivateFolderMode);
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationDiagnostics.Initialize();
        _startTimer = Stopwatch.StartNew();
        ApplicationCommandLine.Initialize(args);

        if (ApplicationCommandLine.TryHandleHelp() || ApplicationCommandLine.TryHandleInstall() || ApplicationCommandLine.TryHandleUninstall())
        {
            return;
        }

        SystemOptions.UpdateSystemOptions();
        PersonalPathManager.Initialize();
        DebugHelper.Init(AppPaths.LogsFilePath);
        MakeLogsPrivate();

        if (SystemOptions.ReadError != null)
        {
            DebugHelper.WriteLine(SystemOptions.ReadError);
        }

        StartupOptions.IsAdmin = Helpers.IsAdministrator();
        StartupOptions.MultiInstance = ApplicationCommandLine.IsCommandPresent("multi", "m");

        using (SingleInstanceManager singleInstanceManager = new(
            ApplicationInfo.MutexName, ApplicationInfo.PipeName, !StartupOptions.MultiInstance, args))
        {
            if (!singleInstanceManager.IsSingleInstance || singleInstanceManager.IsFirstInstance)
            {
                singleInstanceManager.ArgumentsReceived += SingleInstanceCommandRouter.ArgumentsReceived;
                Run(args);
            }
        }

        ApplicationLifecycle.RestartIfRequested();
        DebugHelper.Flush();
    }

    private static void Run(string[] args)
    {
        DebugHelper.WriteLine("ShareX starting.");
        DebugHelper.WriteLine("Version: " + ApplicationInfo.VersionText);
        DebugHelper.WriteLine("Build: " + ApplicationInfo.Build);
        DebugHelper.WriteLine("Command line: " + Environment.CommandLine);
        DebugHelper.WriteLine("Personal path: " + AppPaths.PersonalFolder);
        if (!string.IsNullOrEmpty(PersonalPathManager.DetectionMethod))
        {
            DebugHelper.WriteLine("Personal path detection method: " + PersonalPathManager.DetectionMethod);
        }
        DebugHelper.WriteLine("Operating system: " + Helpers.GetOperatingSystemProductName(true));
        DebugHelper.WriteLine(".NET version: " + Environment.Version);
        DebugHelper.WriteLine("Running as elevated process: " + StartupOptions.IsAdmin);

        StartupOptions.SilentRun = ApplicationCommandLine.IsCommandPresent("silent", "s");
#if MicrosoftStore
        StartupOptions.SilentRun = StartupOptions.SilentRun || AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
#endif
        StartupOptions.IgnoreHotkeyWarning = ApplicationCommandLine.IsCommandPresent("NoHotkeys");

        PersonalPathManager.CreateApplicationFolders();
        ApplicationDiagnostics.WriteStartupFlags();

        DebugHelper.WriteLine("Avalonia application initializing.");
        AvaloniaBootstrapper.Initialize(args, StartApplicationAsync, ApplicationLifecycle.OnAvaloniaStopped);
        ApplicationDiagnostics.InitializeAvalonia();

        SettingManager.LoadInitialSettings();
        ApplicationState.UpdateManager = new ShareXUpdateManager();
        LanguageHelper.ChangeLanguage(ApplicationState.Settings.Language);
        CleanupManager.CleanupAsync();

        ApplicationLifecycle.RegisterSignalHandlers();
        ShareX.AvaloniaUI.Windows.ScreenColorPickerWindow.CaptureScreenshotAsync = TaskHelpers.CaptureScreenForToolAsync;

        DebugHelper.WriteLine("Avalonia application starting.");
        AvaloniaBootstrapper.Run();

        ApplicationLifecycle.CloseSequence();
    }

    private static async Task StartApplicationAsync()
    {
        ImageEditorIntegration.Initialize();

        // A silent start is usually the autostart entry, with nobody there to answer it. The screen waits for the next plain start.
        if (ApplicationState.Settings.ShowStartScreen && !StartupOptions.SilentRun)
        {
            DebugHelper.WriteLine("Start screen opening.");
            StartScreenWindow startScreen = new();
            await startScreen.ShowAsync();
            DebugHelper.WriteLine("Start screen closed.");
        }

        DebugHelper.WriteLine("Hotkey host init started.");
        MainForm hotkeyHost = new();
        ApplicationLifecycle.AttachHost(hotkeyHost);
        hotkeyHost.Initialize();

        await ApplicationSettingsRuntime.InitializeAsync(hotkeyHost);

        bool showMainWindow = !(StartupOptions.SilentRun || ApplicationState.Settings.SilentRun) ||
            !ApplicationState.Settings.ShowTray;
        MainWindowIntegration.Initialize(hotkeyHost.TrayIconService, showMainWindow);
        ScreenRecordManager.RecoverInterruptedRecordings();
        SingleInstanceCommandRouter.MarkReady();

        ShareX.Tools.MouseHighlighterManager.ActivateOnStartup(
            ApplicationState.DefaultTaskSettings.ToolsSettings.MouseHighlighterOptions);

        if (showMainWindow)
        {
            MainWindowIntegration.Activate();
        }

        DebugHelper.WriteLine("Startup time: {0} ms", _startTimer.ElapsedMilliseconds);
        await ApplicationCommandLine.ExecuteInitialAsync();

        if (ApplicationState.Settings.ActionsToolbarRunAtStartup)
        {
            TaskHelpers.OpenActionsToolbar();
        }

        DebugHelper.WriteLine("Hotkey host init finished.");
    }
}
