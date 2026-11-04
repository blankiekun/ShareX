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
using ShareX.HistoryLib;
using ShareX.Localization;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShareX
{
    // What the user has to know after an import. The text is logged as well as shown, so it holds names and paths, never a secret.
    internal sealed class BackupImportReport
    {
        private const int MaxSkippedEntriesShown = 20;

        public BackupMigrationReport Migration { get; } = new BackupMigrationReport();
        public List<string> SkippedEntries { get; set; } = new List<string>();
        public HistoryMergeResult? History { get; set; }
        public string HistoryBackupPath { get; set; }
        public string HistoryError { get; set; }

        public string ToText()
        {
            StringBuilder text = new StringBuilder();

            AddSection(text, Strings.SettingManager_ImportReportSignInAgain, Migration.SignInAgain);
            AddSection(text, Strings.SettingManager_ImportReportSecretsImported, Migration.SecretsImported);
            AddSection(text, Strings.SettingManager_ImportReportPathsMapped, Migration.PathsMapped.OrderBy(path => path.Setting).ThenBy(path => path.From).Select(path => $"{path.Setting}: {path.From} -> {path.To}"));
            AddSection(text, Strings.SettingManager_ImportReportPathsReset, Migration.PathsReset.OrderBy(path => path.Setting).ThenBy(path => path.From).Select(path => $"{path.Setting}: {path.From}"));
            AddSection(text, Strings.SettingManager_ImportReportTurnedOff, Migration.TurnedOff);
            AddSection(text, Strings.SettingManager_ImportReportDroppedEffects, Migration.DroppedEffects);

            if (History is HistoryMergeResult history)
            {
                text.AppendLine(string.Format(Strings.SettingManager_ImportReportHistory, history.Added, history.AlreadyPresent, history.Unreadable));
            }

            if (HistoryError != null)
            {
                text.AppendLine(string.Format(Strings.SettingManager_ImportReportHistoryFailed, HistoryError));
            }

            if (HistoryBackupPath != null)
            {
                text.AppendLine(string.Format(Strings.SettingManager_ImportReportHistoryBackup, HistoryBackupPath));
            }

            if (History != null || HistoryError != null)
            {
                text.AppendLine();
            }

            AddSection(text, Strings.SettingManager_ImportReportSkipped, SkippedEntries.Take(MaxSkippedEntriesShown));

            if (Migration.HasPlaintextSecrets)
            {
                text.AppendLine(Strings.SettingManager_ImportReportPlaintextWarning);
            }

            string report = text.ToString().TrimEnd();

            return report.Length > 0 ? report : Strings.SettingManager_ImportReportTitle;
        }

        private static void AddSection(StringBuilder text, string title, IEnumerable<string> lines)
        {
            List<string> items = lines.ToList();

            if (items.Count > 0)
            {
                text.AppendLine(title);
                items.ForEach(item => text.AppendLine("  " + item));
                text.AppendLine();
            }
        }
    }
}
