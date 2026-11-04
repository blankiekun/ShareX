using ShareX.HelpersLib;
using System.Drawing;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class KWinDesktopTests
{
    private const int ShareXPid = 100;

    private static KWinWindow Window(string id, int pid = 200, bool normal = true, bool keepAbove = false, string caption = "Window")
    {
        Rectangle frame = new(0, 0, 400, 300);
        return new KWinWindow(id, caption, "class", pid, frame, frame, normal, false, keepAbove);
    }

    private static string? LastUsed(params KWinWindow[] topOfStackFirst)
    {
        KWinOutput[] outputs = [new KWinOutput("A", new Rectangle(0, 0, 1280, 720), 1)];
        KWinDesktop desktop = new()
        {
            Outputs = outputs,
            Windows = topOfStackFirst,
            Cursor = Point.Empty,
            Coordinates = DesktopCoordinates.ForDesktop(outputs, false)
        };

        return desktop.LastUsedWindow(ShareXPid)?.Id;
    }

    // Catches: Capture > Active window from ShareX's menu capturing ShareX or its viewer, which sit above the window the user was in.
    [Fact]
    public void LastUsedWindow_SkipsEveryShareXWindow()
    {
        Assert.Equal("user", LastUsed(Window("main", ShareXPid), Window("viewer", ShareXPid), Window("user"), Window("older")));
    }

    // Catches: picking a panel or a tool window the user can't have been working in, which KWin stacks above normal windows.
    [Fact]
    public void LastUsedWindow_SkipsWindowsThatAreNotOrdinary()
    {
        Assert.Equal("user", LastUsed(Window("panel", normal: false), Window("untitled", caption: ""), Window("user")));
    }

    // Catches: an always-on-top window that nobody is using winning every capture, just because it is always on top of the stack.
    [Fact]
    public void LastUsedWindow_PrefersAWindowNotKeptAbove()
    {
        Assert.Equal("user", LastUsed(Window("pinned", keepAbove: true), Window("user")));
    }

    // Catches: the kept-above rule discarding the only window there is, which would report that there is nothing to capture.
    [Fact]
    public void LastUsedWindow_FallsBackToAKeptAboveWindow()
    {
        Assert.Equal("pinned", LastUsed(Window("main", ShareXPid), Window("pinned", keepAbove: true)));
    }

    // Catches: a capture of nothing, or of ShareX, when only ShareX's own windows are open. The caller has to be able to tell.
    [Fact]
    public void LastUsedWindow_IsNullWithOnlyShareXWindows()
    {
        Assert.Null(LastUsed(Window("main", ShareXPid), Window("viewer", ShareXPid)));
    }
}
