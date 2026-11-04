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

using System.Diagnostics;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

/// <summary>Startup recovery, on the files a killed ShareX leaves behind. A real SIGKILL through the app is in the e2e harness.</summary>
public sealed class RecordingJournalTests : IDisposable
{
    private const string Video = "the recording";
    private const string UsersFile = "the user's file";
    // Above the kernel's highest possible pid, so this owner can't be running.
    private const int DeadPid = 4194305;

    private readonly string root, journal, temporary, reservation, final;

    public RecordingJournalTests()
    {
        root = Directory.CreateTempSubdirectory("sxjournal-").FullName;
        journal = Path.Combine(root, "journal");
        temporary = Path.Combine(root, ".clip-recording-1-2.mp4");
        reservation = Path.Combine(root, "clip-recording-1.mp4");
        final = Path.Combine(root, "clip.mp4");
    }

    public void Dispose()
    {
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>What a killed ShareX leaves: an entry, the temporary file with this content, its marker, and the empty reservation.</summary>
    private string Leftover(string? content = Video, int ownerPid = DeadPid, long ownerStart = 1, int recorderPid = 0, long recorderStart = 0)
    {
        if (content != null) File.WriteAllText(temporary, content);
        File.WriteAllText(temporary + ".ts", "");
        File.WriteAllText(reservation, "");
        string entry = Path.Combine(journal, $"{Guid.NewGuid():N}.json");
        RecordingJournal.Write(entry, new RecordingJournal.Entry(temporary, reservation, final, ownerPid, ownerStart, recorderPid, recorderStart));
        return entry;
    }

    // Catches: a recording staying hidden after a kill, or the temporary file, marker, reservation or entry being left behind
    // for every later start to trip over.
    [Fact]
    public void Leftover_MovesToTheFinalPathAndCleansUp()
    {
        string entry = Leftover();

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Equal([final], recovery.Recovered);
        Assert.Equal(Video, File.ReadAllText(final));
        Assert.False(File.Exists(entry));
        Assert.Equal([final], Directory.GetFiles(root));
    }

    // Catches: recovery refusing an empty reservation as if it were a real file, when there's only one output name.
    [Fact]
    public void EmptyReservationAtTheFinalPath_IsReplaced()
    {
        File.WriteAllText(temporary, Video);
        File.WriteAllText(final, "");
        RecordingJournal.Write(Path.Combine(journal, "entry.json"), new RecordingJournal.Entry(temporary, final, final, DeadPid, 1, 0, 0));

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Equal([final], recovery.Recovered);
        Assert.Equal(Video, File.ReadAllText(final));
    }

    // Catches: recovery destroying a real file that has since taken the name, which would lose one file to save another. A taken
    // numbered name has to be skipped too.
    [Fact]
    public void RealFileAtTheFinalPath_IsNeverOverwritten()
    {
        string first = Path.Combine(root, "clip (1).mp4"), second = Path.Combine(root, "clip (2).mp4");
        File.WriteAllText(final, UsersFile);
        File.WriteAllText(first, UsersFile);
        Leftover();

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Equal([second], recovery.Recovered);
        Assert.Equal(Video, File.ReadAllText(second));
        Assert.Equal(UsersFile, File.ReadAllText(final));
        Assert.Equal(UsersFile, File.ReadAllText(first));
    }

    // Catches: a second ShareX taking the file out from under an instance that's still recording, here this very process.
    [Fact]
    public void LiveOwner_IsLeftAlone()
    {
        string entry = Leftover(ownerPid: Environment.ProcessId, ownerStart: RecordingJournal.StartTimeOf(Environment.ProcessId));

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Empty(recovery.Recovered);
        Assert.Equal(1, recovery.StillRecording);
        Assert.Equal(Video, File.ReadAllText(temporary));
        Assert.True(File.Exists(reservation));
        Assert.True(File.Exists(entry));
        Assert.False(File.Exists(final));
    }

    // Catches: treating a dead owner as the end of writing. After a kill gsr gets SIGINT and goes on saving, so taking the file then
    // could present a half-converted MP4. Once that process is gone, the same entry has to be recovered.
    [Fact]
    public void LiveRecorder_IsLeftAloneUntilItIsGone()
    {
        using Process recorder = Process.Start("/usr/bin/sleep", "60");
        string entry = Leftover(recorderPid: recorder.Id, recorderStart: RecordingJournal.StartTimeOf(recorder.Id));

        RecordingRecovery live = RecordingJournal.Recover(journal);

        Assert.Equal(1, live.StillRecording);
        Assert.True(File.Exists(entry));
        Assert.False(File.Exists(final));

        recorder.Kill();
        recorder.WaitForExit();
        RecordingRecovery after = RecordingJournal.Recover(journal);

        Assert.Equal([final], after.Recovered);
    }

    // Catches: a reused pid making a dead recording look alive forever, so it's never recovered.
    [Fact]
    public void ReusedPid_DoesNotCountAsAlive()
    {
        Leftover(ownerPid: Environment.ProcessId, ownerStart: RecordingJournal.StartTimeOf(Environment.ProcessId) - 1);

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Equal([final], recovery.Recovered);
    }

    // Catches: an empty temporary file being reported as a recovered recording, or replacing what's at the final path.
    [Fact]
    public void EmptyTemporaryFile_IsDeletedAndNotReported()
    {
        File.WriteAllText(final, UsersFile);
        string entry = Leftover(content: "");

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Empty(recovery.Recovered);
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(reservation));
        Assert.False(File.Exists(entry));
        Assert.Equal(UsersFile, File.ReadAllText(final));
    }

    // Catches: deleting a finished recording because its entry outlived the move: a kill between the two leaves the file under the
    // reservation's name, with no temporary file left.
    [Fact]
    public void FileAtTheReservation_StaysWhenThereIsNoTemporaryFile()
    {
        string entry = Leftover(content: null);
        File.WriteAllText(reservation, Video);

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Empty(recovery.Recovered);
        Assert.Equal(Video, File.ReadAllText(reservation));
        Assert.False(File.Exists(entry));
    }

    // Catches: one damaged entry stopping recovery of the others at every start, or crashing startup.
    [Fact]
    public void DamagedEntry_IsDroppedAndDoesNotStopTheOthers()
    {
        Leftover();
        string damaged = Path.Combine(journal, "damaged.json");
        File.WriteAllText(damaged, "{ not json");

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        Assert.Equal([final], recovery.Recovered);
        Assert.Contains(recovery.Problems, problem => problem.Contains(damaged));
        Assert.False(File.Exists(damaged));
    }

    // Catches: an entry being dropped when the move failed, which would leave the recording hidden with nothing pointing at it.
    [Fact]
    public void MoveThatFails_KeepsTheEntryAndTheFile()
    {
        string entry = Leftover();
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        RecordingRecovery recovery = RecordingJournal.Recover(journal);

        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Empty(recovery.Recovered);
        Assert.Single(recovery.Problems);
        Assert.True(File.Exists(entry));
        Assert.Equal(Video, File.ReadAllText(temporary));
    }

    // Catches: a stop during the encode deleting the MP4 and leaving a short GIF as the result, the second lost-recording route.
    // A GIF that's missing or empty despite a clean exit counts as unfinished too.
    [Theory]
    [InlineData(false, "part of a gif")]
    [InlineData(false, null)]
    [InlineData(true, "")]
    [InlineData(true, null)]
    public void UnfinishedGif_KeepsTheMp4AndRemovesTheGif(bool encodedCompletely, string? gifContent)
    {
        string mp4 = Path.Combine(root, "clip.mp4"), gif = Path.Combine(root, "clip.gif");
        File.WriteAllText(mp4, Video);
        if (gifContent != null) File.WriteAllText(gif, gifContent);

        Assert.False(RecordingFiles.SettleGifEncode(mp4, gif, encodedCompletely));

        Assert.Equal(Video, File.ReadAllText(mp4));
        Assert.False(File.Exists(gif));
    }

    // Catches: keeping both files after a good encode, which would leave an MP4 behind every GIF recording.
    [Fact]
    public void FinishedGif_ReplacesTheMp4()
    {
        string mp4 = Path.Combine(root, "clip.mp4"), gif = Path.Combine(root, "clip.gif");
        File.WriteAllText(mp4, Video);
        File.WriteAllText(gif, "a whole gif");

        Assert.True(RecordingFiles.SettleGifEncode(mp4, gif, encodedCompletely: true));

        Assert.False(File.Exists(mp4));
        Assert.Equal("a whole gif", File.ReadAllText(gif));
    }
}
