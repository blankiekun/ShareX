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
using ShareX.ScreenCaptureLib;
using System;
using System.Drawing;
using System.Threading.Tasks;

namespace ShareX
{
    public abstract class CaptureBase
    {
        public bool AllowAutoHideForm { get; set; } = true;
        public bool AllowAnnotation { get; set; } = true;

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
                await Task.Delay(250);
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
