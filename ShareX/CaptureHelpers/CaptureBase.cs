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
using ShareX.Localization;
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX
{
    public abstract class CaptureBase
    {
        // KWin fades the closed menu out for about 410 ms at the default animation speed, so a shorter wait leaves it in the screenshot.
        // Spectacle's own start covers the rest: 350 was clean in testing and 300 was not, so this leaves a 50 ms margin.
        private const int HideDelay = 400;

        public bool AllowAutoHideForm { get; set; } = true;
        public bool AllowAnnotation { get; set; } = true;

        /// <summary>
        /// Set by ShareX's own menu on an active window or monitor capture: ShareX is the active window by then, so this hands
        /// activation back to the window the user was last in. A hotkey doesn't set it, as there ShareX being active means the user is in it.
        /// </summary>
        public bool ActivateLastUsedWindow { get; init; }

        public void Capture(bool autoHideForm)
        {
            Capture(null, autoHideForm);
        }

        public void Capture(TaskSettings taskSettings = null, bool autoHideForm = false)
        {
            if (taskSettings == null) taskSettings = TaskSettings.GetDefaultTaskSettings();

            _ = CaptureAsync(taskSettings, autoHideForm);
        }

        protected abstract TaskMetadata Execute(TaskSettings taskSettings);

        protected virtual Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            // Spectacle takes a few hundred ms and up to its watchdog's timeout, so not on the UI thread.
            return Task.Run(() => Execute(taskSettings));
        }

        private async Task CaptureAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            try
            {
                if (taskSettings.CaptureSettings.ScreenshotDelay > 0)
                {
                    int delay = (int)(taskSettings.CaptureSettings.ScreenshotDelay * 1000);
                    await Task.Delay(delay);
                }

                await CaptureInternalAsync(taskSettings, autoHideForm);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
            }
        }

        private async Task CaptureInternalAsync(TaskSettings taskSettings, bool autoHideForm)
        {
            bool wait = false;
            bool showDesktopIcons = false;
            bool showMainForm = false;

            if (taskSettings.GeneralSettings.ToastWindowAutoHide)
            {
                await NotificationWindow.CloseActiveWindowAsync();
            }

            if (ActivateLastUsedWindow)
            {
                try
                {
                    ReturnToLastUsedWindow();
                    wait = true;
                }
                catch (Exception ex) when (ex is ScreenCaptureException or KWinScriptException)
                {
                    DebugHelper.WriteException(ex);
                    ex.ShowError(false);
                    return;
                }
            }

            if (taskSettings.CaptureSettings.CaptureAutoHideDesktopIcons && !CaptureHelpers.IsActiveWindowFullscreen() && DesktopIconManager.AreDesktopIconsVisible())
            {
                DesktopIconManager.SetDesktopIconsVisibility(false);
                showDesktopIcons = true;
                wait = true;
            }

            if (autoHideForm && AllowAutoHideForm)
            {
                MainWindowIntegration.Hide();
                showMainForm = true;
                wait = true;
            }

            if (wait)
            {
                await Task.Delay(HideDelay);
            }

            TaskMetadata metadata = null;
            Exception captureError = null;

            try
            {
                AllowAnnotation = true;
                metadata = await ExecuteAsync(taskSettings);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
                captureError = ex;
            }
            finally
            {
                if (showDesktopIcons)
                {
                    DesktopIconManager.SetDesktopIconsVisibility(true);
                }

                if (showMainForm)
                {
                    MainWindowIntegration.Activate();
                }

                AfterCapture(metadata, taskSettings);
            }

            // After the main window is back, so that the message has something to return to.
            if (captureError is ScreenCaptureException or KWinScriptException)
            {
                captureError.ShowError(false);
            }
        }

        // Before the main window hides: KWin then activates a window of its own choosing, which in testing was the next one on
        // ShareX's monitor, not the last one used. After the screenshot delay, as the user may have picked a window during it.
        private void ReturnToLastUsedWindow()
        {
            KWinWindow window = KWinDesktop.Query().LastUsedWindow(Environment.ProcessId);

            // With no window at all, a monitor capture still has the desktop to show.
            if ((window == null || !KWinDesktop.ActivateWindow(window.Id)) && this is CaptureActiveWindow)
            {
                throw new ScreenCaptureException(Strings.CaptureBase_NoOtherWindow);
            }
        }

        private void AfterCapture(TaskMetadata metadata, TaskSettings taskSettings)
        {
            if (metadata != null && metadata.Image != null)
            {
                TaskHelpers.PlayNotificationSoundAsync(NotificationSound.Capture, taskSettings);

                if (taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage) && !AllowAnnotation)
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AnnotateImage);
                }

                if (taskSettings.ImageSettings.ImageEffectOnlyRegionCapture &&
                    GetType() != typeof(CaptureRegion) && GetType() != typeof(CaptureLastRegion))
                {
                    taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.AddImageEffects);
                }

                UploadManager.RunImageTask(metadata, taskSettings);
            }
        }

        protected TaskMetadata CreateMetadata()
        {
            return CreateMetadata(Rectangle.Empty);
        }

        /// <summary>Uses the active window for the file name, if it contains <paramref name="insideRect"/>.</summary>
        protected TaskMetadata CreateMetadata(Rectangle insideRect)
        {
            TaskMetadata metadata = new TaskMetadata();

            try
            {
                KWinDesktop desktop = KWinDesktop.Query();
                KWinWindow window = desktop.ActiveWindow;

                if (window != null && (insideRect.IsEmpty || desktop.Coordinates.LogicalToX11(window.FrameGeometry).Contains(insideRect)))
                {
                    metadata.UpdateInfo(window);
                }
            }
            catch (KWinScriptException e)
            {
                // Only the file name's window title is lost, so the capture goes on.
                DebugHelper.WriteException(e);
            }

            return metadata;
        }
    }
}
