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
using System.Collections.Concurrent;
using System.IO.Pipes;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

// Real pipes and a real mutex. A named mutex belongs to one thread, so another thread can play the second process.
public sealed class SingleInstanceManagerTests : IDisposable
{
    private const int Patience = 5000;

    private readonly string _mutexName = "sharex-test-" + Guid.NewGuid().ToString("N");
    private readonly string _pipeName = "sharex-test-" + Guid.NewGuid().ToString("N");
    private readonly BlockingCollection<string[]> _received = [];
    private readonly ManualResetEventSlim _started = new();
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _firstInstance;

    public SingleInstanceManagerTests()
    {
        _firstInstance = new Thread(() =>
        {
            using SingleInstanceManager manager = new(_mutexName, _pipeName, []);
            manager.ArgumentsReceived += _received.Add;
            _started.Set();
            _stop.Wait();
        });
        _firstInstance.Start();
        Assert.True(_started.Wait(Patience));
    }

    public void Dispose()
    {
        _stop.Set();
        _firstInstance.Join();
    }

    private void RunSecondInstance(params string[] args)
    {
        using SingleInstanceManager manager = new(_mutexName, _pipeName, args);
        Assert.False(manager.IsFirstInstance);
    }

    private NamedPipeClientStream ConnectClient()
    {
        NamedPipeClientStream client = new(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(Patience);
        return client;
    }

    private string[] NextArguments(int timeout = Patience)
    {
        Assert.True(_received.TryTake(out string[]? args, timeout), "No arguments arrived.");
        return args!;
    }

    // Catches: the listener ending or wedging after its first client, so that only the first command from a launcher works,
    // and arguments changing on the way through, such as spaces splitting a file name.
    [Fact]
    public void Arguments_ArriveUnchanged_FromOneConnectionAfterAnother()
    {
        string[][] rounds =
        [
            ["-ScreenRecorder"],
            ["/home/user/My Pictures/スクリーン ショット.png", "", "-silent"],
            []
        ];

        foreach (string[] round in rounds)
        {
            RunSecondInstance(round);
            Assert.Equal(round, NextArguments());
        }
    }

    // Catches: the listening socket closing between clients. The queued ones have already written and exited by then, so they
    // are lost with no error anywhere. A one-server-at-a-time loop delivered 1 of 20 here. A file manager can start one process per file.
    [Fact]
    public void Arguments_AllArrive_FromProcessesStartedTogether()
    {
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, i => RunSecondInstance(i.ToString()));

        string[] arrived = Enumerable.Range(0, 20).Select(_ => NextArguments()[0]).Order().ToArray();
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()).Order(), arrived);
    }

    // Catches: the limit on the argument count going missing or moving, since this pipe is how other programs reach ShareX.
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void BadArgumentCount_IsRefused_AndTheListenerGoesOn(int count)
    {
        try
        {
            using BinaryWriter writer = new(ConnectClient());
            writer.Write(count);
            for (int i = 0; i < 101; i++) writer.Write("x");
        }
        catch (IOException)
        {
            // The listener hanging up partway through is the refusal this test wants.
        }

        Thread.Sleep(200);
        RunSecondInstance("after");
        Assert.Equal(["after"], NextArguments());
        Assert.Empty(_received);
    }

    // Catches: the limit refusing what it should allow.
    [Fact]
    public void MostArgumentsAllowed_Arrive()
    {
        string[] args = Enumerable.Repeat("x", 100).ToArray();
        RunSecondInstance(args);
        Assert.Equal(args, NextArguments());
    }

    // Catches: a client dying partway ending the listener.
    [Fact]
    public void ClientThatDiesPartway_DoesNotEndTheListener()
    {
        ConnectClient().Dispose();

        using (BinaryWriter writer = new(ConnectClient()))
        {
            writer.Write(2);
            writer.Write("only one of two");
        }

        RunSecondInstance("after");
        Assert.Equal(["after"], NextArguments());
        Assert.Empty(_received);
    }

    // Catches: a client that connects and sends nothing blocking everyone behind it, and holding its read forever.
    [Fact]
    public void ClientThatSendsNothing_DoesNotBlockOthers_AndGetsDropped()
    {
        using NamedPipeClientStream stalled = ConnectClient();

        RunSecondInstance("after");
        Assert.Equal(["after"], NextArguments(2000));

        Thread.Sleep(5500);
        Assert.ThrowsAny<IOException>(() =>
        {
            stalled.WriteByte(0);
            stalled.Flush();
        });
    }
}
