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

using System.Text.Json;

namespace ShareX.ScreenRecordingLib;

/// <summary>What a startup recovery did. StillRecording counts entries left alone because they're still being written.</summary>
public sealed record RecordingRecovery(IReadOnlyList<string> Recovered, int StillRecording, IReadOnlyList<string> Problems);

/// <summary>
/// SIGKILL leaves a finished recording under a hidden temporary name, beside an empty reservation. Each recording notes those
/// names in a small file while it runs, and a later start uses what's left to put the recording where it belongs.
/// </summary>
public static class RecordingJournal
{
    /// <summary>A pid only counts together with its start time, because the kernel reuses pids.</summary>
    internal sealed record Entry(string Temporary, string Reservation, string FinalPath, int OwnerPid, long OwnerStart, int RecorderPid, long RecorderStart);

    internal static void Write(string file, string temporary, string reservation, string finalPath, int recorderPid)
    {
        int owner = Environment.ProcessId;
        Write(file, new Entry(temporary, reservation, finalPath, owner, StartTimeOf(owner), recorderPid, recorderPid > 0 ? StartTimeOf(recorderPid) : 0));
    }

    internal static void Write(string file, Entry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // A rename, so a kill during the write can't leave half an entry.
        string partial = file + ".tmp";
        File.WriteAllText(partial, JsonSerializer.Serialize(entry));
        File.Move(partial, file, overwrite: true);
    }

    /// <summary>
    /// shortcut: an entry that's still being written waits for the next start, so right after a kill the recording can stay hidden
    /// until then, because gsr can take a while to save after its SIGINT. Liveness also only sees this pid namespace: an instance in
    /// a different sandbox looks dead, though a move can't hurt it, since gsr keeps writing the same inode.
    /// </summary>
    public static RecordingRecovery Recover(string folder)
    {
        List<string> recovered = [], problems = [];
        int stillRecording = 0;
        if (!Directory.Exists(folder)) return new RecordingRecovery(recovered, 0, problems);

        foreach (string file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                Entry? entry = Read(file);
                if (entry == null)
                {
                    problems.Add($"'{file}' isn't a journal entry, so it was removed.");
                    File.Delete(file);
                }
                else if (IsRunning(entry.OwnerPid, entry.OwnerStart) || IsRunning(entry.RecorderPid, entry.RecorderStart))
                {
                    stillRecording++;
                }
                else
                {
                    if (Settle(entry) is string moved) recovered.Add(moved);
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The entry stays, so the next start tries again.
                problems.Add($"'{file}': {ex.Message}");
            }
        }
        return new RecordingRecovery(recovered, stillRecording, problems);
    }

    private static Entry? Read(string file)
    {
        try { return JsonSerializer.Deserialize<Entry>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Returns where the recording went, or null when there was nothing to recover.</summary>
    private static string? Settle(Entry entry)
    {
        string? recovered = null;
        FileInfo temporary = new(entry.Temporary);
        if (temporary.Exists)
        {
            if (temporary.Length > 0) recovered = RecordingFiles.MoveWithoutOverwrite(entry.Temporary, entry.FinalPath);
            else temporary.Delete();
        }
        // gsr's first-frame marker sits beside the temporary file, see ScreenRecorder.
        if (File.Exists(entry.Temporary + ".ts")) File.Delete(entry.Temporary + ".ts");
        FileInfo reservation = new(entry.Reservation);
        if (reservation.Exists && reservation.Length == 0) reservation.Delete();
        return recovered;
    }

    private static bool IsRunning(int pid, long startTime) => pid > 0 && startTime > 0 && StartTimeOf(pid) == startTime;

    /// <summary>The start time in /proc/pid/stat, or 0 if the process is gone. A zombie is gone: it can't write anything.</summary>
    internal static long StartTimeOf(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            // The command name can hold spaces and brackets, so count from its closing bracket: state is field 3 and starttime field 22.
            string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return fields[0] == "Z" ? 0 : long.Parse(fields[19]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return 0;
        }
    }
}

public static class RecordingFiles
{
    /// <summary>Moves a file and never replaces another one. Only a missing or empty preferred name is used as it is; otherwise the name gets a number.</summary>
    public static string MoveWithoutOverwrite(string source, string preferred)
    {
        if (source == preferred) return preferred;
        string folder = Path.GetDirectoryName(preferred)!;
        Directory.CreateDirectory(folder);
        FileInfo reservation = new(preferred);
        if (reservation.Exists && reservation.Length == 0) reservation.Delete();
        for (int number = 0; number < 1000; number++)
        {
            string candidate = number == 0 ? preferred : Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(preferred)} ({number}){Path.GetExtension(preferred)}");
            try
            {
                File.Move(source, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
            }
        }
        throw new IOException($"No free name was found for '{preferred}'.");
    }

    /// <summary>
    /// After a GIF encode, exactly one of the two files stays. The MP4 only goes when the encode finished completely and left a GIF,
    /// and a partial GIF goes so it can't be taken for the result. Returns whether the GIF is what's left.
    /// </summary>
    public static bool SettleGifEncode(string mp4, string gif, bool encodedCompletely)
    {
        FileInfo output = new(gif);
        if (encodedCompletely && output.Exists && output.Length > 0)
        {
            File.Delete(mp4);
            return true;
        }
        if (output.Exists) output.Delete();
        return false;
    }
}
