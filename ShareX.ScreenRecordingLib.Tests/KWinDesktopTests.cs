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

    // Catches: the keep-above hotkey skipping the window it just kept above, so a second press changes another window instead.
    [Fact]
    public void ActiveOrLastUsedWindow_TakesAnActiveKeptAboveWindow()
    {
        KWinOutput[] outputs = [new KWinOutput("A", new Rectangle(0, 0, 1280, 720), 1)];
        KWinDesktop desktop = new()
        {
            Outputs = outputs,
            Windows = [Window("pinned", keepAbove: true), Window("other")],
            Cursor = Point.Empty,
            Coordinates = DesktopCoordinates.ForDesktop(outputs, false),
            ActiveWindowId = "pinned"
        };

        Assert.Equal("pinned", desktop.ActiveOrLastUsedWindow(ShareXPid)?.Id);
    }

    // Catches: a hotkey sent while ShareX has the focus stripping or pinning ShareX's own window.
    [Fact]
    public void ActiveOrLastUsedWindow_SkipsActiveShareXWindow()
    {
        KWinOutput[] outputs = [new KWinOutput("A", new Rectangle(0, 0, 1280, 720), 1)];
        KWinDesktop desktop = new()
        {
            Outputs = outputs,
            Windows = [Window("main", ShareXPid), Window("user")],
            Cursor = Point.Empty,
            Coordinates = DesktopCoordinates.ForDesktop(outputs, false),
            ActiveWindowId = "main"
        };

        Assert.Equal("user", desktop.ActiveOrLastUsedWindow(ShareXPid)?.Id);
    }

    private static string? FindByTitle(string title, params KWinWindow[] topOfStackFirst)
    {
        KWinOutput[] outputs = [new KWinOutput("A", new Rectangle(0, 0, 1280, 720), 1)];
        KWinDesktop desktop = new()
        {
            Outputs = outputs,
            Windows = topOfStackFirst,
            Cursor = Point.Empty,
            Coordinates = DesktopCoordinates.ForDesktop(outputs, false)
        };

        return desktop.FindWindowByTitle(title, ShareXPid)?.Id;
    }

    // Catches: a borderless toggle hitting a window that merely contains the title, because it is higher in the stack, when another has exactly that title.
    [Fact]
    public void FindWindowByTitle_PrefersAnExactMatch()
    {
        Assert.Equal("exact", FindByTitle("Notes", Window("partial", caption: "Notes - Editor"), Window("exact", caption: "Notes")));
    }

    // Catches: a typed title having to match case and all, which the Windows version didn't ask for.
    [Fact]
    public void FindWindowByTitle_FallsBackToAnyCaseContains()
    {
        Assert.Equal("partial", FindByTitle("notes", Window("other", caption: "Terminal"), Window("partial", caption: "My Notes - Editor")));
    }

    // Catches: the tool stripping ShareX's own window or a panel when their title happens to match.
    [Fact]
    public void FindWindowByTitle_SkipsShareXAndWindowsThatAreNotOrdinary()
    {
        Assert.Null(FindByTitle("Notes", Window("own", ShareXPid, caption: "Notes"), Window("panel", normal: false, caption: "Notes")));
    }

    // Catches: a window that closed between the list and the click reading as a crash in the parser. The script reports null for it.
    [Fact]
    public void ParseWindowDetails_ReadsAClosedWindowAsNull()
    {
        Assert.Null(KWinDesktop.ParseWindowDetails("null"));
    }

    // Catches: a window without a role or desktop file failing to parse, since KWin leaves those out or empty.
    [Fact]
    public void ParseWindowDetails_ReadsAWindowWithFieldsMissing()
    {
        KWinWindowDetails? details = KWinDesktop.ParseWindowDetails(
            """{"id":"{a}","caption":"c","pid":5,"frame":{"x":1,"y":2,"width":3,"height":4},"client":{"x":1,"y":2,"width":3,"height":4},"opacity":0.5,"keepAbove":true,"state":["keepAbove"]}""");

        Assert.NotNull(details);
        Assert.Equal("", details.WindowRole);
        Assert.Equal(0.5, details.Opacity);
        Assert.Equal(["keepAbove"], details.State);
    }

    private static Rectangle? Fit(int x, int y, int width, int height)
    {
        // A short monitor beside a tall one, so part of the desktop's bounding box is on no monitor.
        KWinOutput[] outputs = [new KWinOutput("Tall", new Rectangle(0, 0, 1920, 1080), 1), new KWinOutput("Short", new Rectangle(1920, 0, 1280, 720), 1)];
        KWinDesktop desktop = new()
        {
            Outputs = outputs,
            Windows = [],
            Cursor = Point.Empty,
            Coordinates = DesktopCoordinates.ForDesktop(outputs, false)
        };

        return desktop.FitToOneOutput(new Rectangle(x, y, width, height));
    }

    // Catches: a window that fits being changed on its way to the recorder.
    [Fact]
    public void FitToOneOutput_KeepsAWindowOnOneOutput()
    {
        Assert.Equal(new Rectangle(2000, 100, 800, 500), Fit(2000, 100, 800, 500));
    }

    // Catches: a maximized window being refused for touching the next monitor, as Intersect gives a rectangle of no width there that IsEmpty doesn't count as empty.
    [Fact]
    public void FitToOneOutput_KeepsAWindowFillingAnOutputBesideAnother()
    {
        Assert.Equal(new Rectangle(1920, 0, 1280, 720), Fit(1920, 0, 1280, 720));
    }

    // Catches: a window hanging off the desktop being refused, or asking the recorder for pixels no monitor has.
    [Theory]
    [InlineData(-150, 100, 800, 500, 0, 100, 650, 500)]
    [InlineData(3000, 100, 800, 500, 3000, 100, 200, 500)]
    [InlineData(100, -50, 800, 500, 100, 0, 800, 450)]
    public void FitToOneOutput_CutsWhatHangsOffTheDesktop(int x, int y, int width, int height, int cutX, int cutY, int cutWidth, int cutHeight)
    {
        Assert.Equal(new Rectangle(cutX, cutY, cutWidth, cutHeight), Fit(x, y, width, height));
    }

    // Catches: clipping to the desktop's bounding box instead of the monitor, which keeps the part below the short monitor, where nothing is drawn.
    [Fact]
    public void FitToOneOutput_CutsToTheMonitorNotTheDesktopsBoundingBox()
    {
        Assert.Equal(new Rectangle(2000, 600, 800, 120), Fit(2000, 600, 800, 300));
    }

    // Catches: recording only one side of a window across two monitors without saying so.
    [Fact]
    public void FitToOneOutput_RefusesAWindowOnTwoOutputs()
    {
        Assert.Null(Fit(1800, 100, 800, 500));
    }

    // Catches: an empty rectangle reaching the recorder.
    [Fact]
    public void FitToOneOutput_RefusesAWindowOnNoOutput()
    {
        Assert.Null(Fit(4000, 100, 800, 500));
    }
}
