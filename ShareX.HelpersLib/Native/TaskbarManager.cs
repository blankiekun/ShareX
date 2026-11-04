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
using Tmds.DBus.Protocol;

namespace ShareX.HelpersLib
{
    public enum TaskbarProgressBarStatus
    {
        NoProgress,
        Normal
    }

    /// <summary>
    /// Shows progress on ShareX's task manager button through the launcher entry signal that Plasma reads. It names the installed
    /// desktop entry, so nothing shows without one. Plasma drops the bar when this connection closes, so a crash can't leave it behind.
    /// </summary>
    public static class TaskbarManager
    {
        private const string DesktopEntryUri = "application://ShareX.desktop";
        private const string ObjectPath = "/com/canonical/unity/launcherentry/sharex";
        private const string Interface = "com.canonical.Unity.LauncherEntry";

        private static readonly object syncLock = new object();

        private static Task<DBusConnection>? connecting;
        private static bool enabled;
        private static double progress;
        private static bool progressVisible;

        public static bool Enabled
        {
            get => enabled;
            set
            {
                // Tasks finishing later won't report while this is off, so a bar showing now has to go now.
                if (!value)
                {
                    Update(0, false);
                }

                enabled = value;
            }
        }

        public static void SetProgressValue(int currentValue, int maximumValue = 100)
        {
            if (Enabled)
            {
                Update((double)currentValue.Clamp(0, maximumValue) / maximumValue, true);
            }
        }

        public static void SetProgressState(TaskbarProgressBarStatus state)
        {
            if (Enabled)
            {
                bool visible = state == TaskbarProgressBarStatus.Normal;
                Update(visible ? progress : 0, visible);
            }
        }

        private static void Update(double newProgress, bool visible)
        {
            lock (syncLock)
            {
                if (newProgress == progress && visible == progressVisible)
                {
                    return;
                }

                progress = newProgress;
                progressVisible = visible;

                connecting ??= ConnectAsync();
                connecting.ContinueWith(SendCurrentState, TaskScheduler.Default);
            }
        }

        private static async Task<DBusConnection> ConnectAsync()
        {
            DBusConnection connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("There is no D-Bus session bus."));
            await connection.ConnectAsync().ConfigureAwait(false);
            return connection;
        }

        // Runs after the connection is up, maybe out of order, so it sends whatever is newest rather than the value that queued it.
        private static void SendCurrentState(Task<DBusConnection> connected)
        {
            lock (syncLock)
            {
                if (connected.IsFaulted)
                {
                    if (enabled)
                    {
                        enabled = false;
                        DebugHelper.WriteException(connected.Exception, "Taskbar progress is off");
                    }

                    return;
                }

                DBusConnection connection = connected.Result;

                using MessageWriter writer = connection.GetMessageWriter();
                writer.WriteSignalHeader(null, ObjectPath, Interface, "Update", "sa{sv}");
                writer.WriteString(DesktopEntryUri);
                ArrayStart properties = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString("progress");
                writer.WriteVariantDouble(progress);
                writer.WriteDictionaryEntryStart();
                writer.WriteString("progress-visible");
                writer.WriteVariantBool(progressVisible);
                writer.WriteDictionaryEnd(properties);
                connection.TrySendMessage(writer.CreateMessage());
            }
        }
    }
}
