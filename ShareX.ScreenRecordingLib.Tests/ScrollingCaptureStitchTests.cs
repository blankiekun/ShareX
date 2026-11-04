using ShareX.ScreenCaptureLib;
using SkiaSharp;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class ScrollingCaptureStitchTests
{
    private const int Width = 320;
    private const int Height = 180;
    private const int LineHeight = 20;
    private const int InkRows = 14;
    private const int FirstBit = 60;
    private const int BitWidth = 12;

    private static readonly SKColor Footer = new(30, 90, 200);
    private static readonly SKColor Header = new(30, 160, 60);

    /// <summary>A view of a page of numbered lines. Each line's number is drawn as bars, so a stitched image can be read back exactly.</summary>
    private static SKBitmap Frame(int top, int header = 0, int footer = 0, int lineHeight = LineHeight, int scrollbarWidth = 0, int width = Width)
    {
        SKBitmap bitmap = new(new SKImageInfo(width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        for (int y = 0; y < Height; y++)
        {
            int line = (top + y) / lineHeight;
            bool inkRow = (top + y) % lineHeight < InkRows;

            for (int x = 0; x < width; x++)
            {
                SKColor color = SKColors.White;

                if (y < header)
                {
                    color = Header;
                }
                else if (y >= Height - footer)
                {
                    color = Footer;
                }
                else if (inkRow && x >= FirstBit - 8 && x < FirstBit)
                {
                    color = SKColors.Black;
                }
                else if (inkRow && x >= FirstBit && x < FirstBit + BitWidth * 14 && ((line >> ((x - FirstBit) / BitWidth)) & 1) == 1)
                {
                    color = SKColors.Black;
                }
                else if (scrollbarWidth > 0 && x >= width - scrollbarWidth && y >= top / 20 && y < top / 20 + 40)
                {
                    color = SKColors.Gray;
                }

                bitmap.SetPixel(x, y, color);
            }
        }

        return bitmap;
    }

    private static ScrollingCaptureManager Stitch(IEnumerable<int> tops, int header = 0, int footer = 0, int lineHeight = LineHeight, int maxHeight = 16384, int scrollbarWidth = 0)
    {
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions { MaxStitchedHeight = maxHeight });

        foreach (int top in tops)
        {
            using SKBitmap frame = Frame(top, header, footer, lineHeight, scrollbarWidth);
            manager.AddFrame(frame);
        }

        return manager;
    }

    /// <summary>The line numbers in an image, top to bottom. A line cut short anywhere but at the very end means rows were lost or doubled.</summary>
    private static List<int> ReadLines(SKBitmap image)
    {
        List<(int Line, int Rows)> lines = [];

        for (int y = 0; y < image.Height; y++)
        {
            if (image.GetPixel(FirstBit - 4, y) != SKColors.Black)
            {
                continue;
            }

            int line = 0;

            for (int bit = 0; bit < 14; bit++)
            {
                if (image.GetPixel(FirstBit + bit * BitWidth + BitWidth / 2, y) == SKColors.Black)
                {
                    line |= 1 << bit;
                }
            }

            if (lines.Count > 0 && lines[^1].Line == line)
            {
                lines[^1] = (line, lines[^1].Rows + 1);
            }
            else
            {
                lines.Add((line, 1));
            }
        }

        for (int i = 1; i < lines.Count - 1; i++)
        {
            Assert.True(lines[i].Rows == InkRows, $"line {lines[i].Line} has {lines[i].Rows} rows, not {InkRows}");
        }

        return lines.Select(x => x.Line).ToList();
    }

    private static void AssertLines(SKBitmap image, int first, int last)
    {
        Assert.Equal(Enumerable.Range(first, last - first + 1), ReadLines(image));
    }

    // Catches: a stitcher that assumes every step is about the same, which is what a program sending wheel clicks produces and a hand doesn't.
    [Fact]
    public void UnevenStepsGiveEveryLineOnce()
    {
        using ScrollingCaptureManager manager = Stitch([0, 3, 40, 41, 120, 127, 130, 200, 203, 290]);

        AssertLines(manager.Result, 0, (290 + Height - 1) / LineHeight);
    }

    // Catches: the old search taking the first row that matched, so a small step back repeated rows.
    [Fact]
    public void ASmallStepBackRepeatsNothing()
    {
        using ScrollingCaptureManager manager = Stitch([0, 60, 120, 100, 160]);

        AssertLines(manager.Result, 0, (160 + Height - 1) / LineHeight);
    }

    // Catches: a stitched image that shrinks when the view moves up, which loses the rows already seen if the user stops there.
    [Fact]
    public void StoppingAfterAStepBackKeepsTheLowestRows()
    {
        using ScrollingCaptureManager manager = Stitch([0, 60, 120, 90]);

        AssertLines(manager.Result, 0, (120 + Height - 1) / LineHeight);
    }

    // Catches: the old best guess, which reused the last step when a frame did not match, and doubled 25 lines in this sequence.
    [Fact]
    public void ALargeStepBackRepeatsNothingAndContinues()
    {
        using ScrollingCaptureManager manager = Stitch([0, 80, 160, 240, 320, 0, 80, 160, 240, 320, 400]);

        AssertLines(manager.Result, 0, (400 + Height - 1) / LineHeight);
    }

    // Catches: stitching a frame that doesn't overlap, which leaves a silent gap. It has to wait for the user to come back to the last rows.
    [Fact]
    public void AStepPastTheFrameLeavesNoGapAndCanBeRecovered()
    {
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions());
        using SKBitmap first = Frame(0), far = Frame(600), back = Frame(60), next = Frame(140);

        Assert.Equal(ScrollingFrameResult.Added, manager.AddFrame(first));
        Assert.Equal(ScrollingFrameResult.NoOverlap, manager.AddFrame(far));
        AssertLines(manager.Result, 0, (Height - 1) / LineHeight);

        Assert.Equal(ScrollingFrameResult.Added, manager.AddFrame(back));
        Assert.Equal(ScrollingFrameResult.Added, manager.AddFrame(next));
        AssertLines(manager.Result, 0, (140 + Height - 1) / LineHeight);

        manager.Dispose();
    }

    // Catches: the same frame counting as a step: rows cut from the end and put back, or a pause taken as the end of the page, as it was.
    [Fact]
    public void AFrameWithoutMovementAddsNothing()
    {
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions());
        using SKBitmap first = Frame(0), same = Frame(0);

        manager.AddFrame(first);
        int height = manager.Result.Height;

        Assert.Equal(ScrollingFrameResult.NoNewRows, manager.AddFrame(same));
        Assert.Equal(height, manager.Result.Height);

        manager.Dispose();
    }

    // Catches: blank rows matching any other blank rows. The old search took a single matching row as enough, and put frames in the wrong place.
    [Fact]
    public void BlankRowsAloneDoNotPlaceAFrame()
    {
        const int sparse = Height * 2;
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions());
        using SKBitmap first = Frame(0, lineHeight: sparse), next = Frame(sparse / 2, lineHeight: sparse);

        manager.AddFrame(first);

        Assert.Equal(ScrollingFrameResult.NoOverlap, manager.AddFrame(next));
        Assert.Equal(Height, manager.Result.Height);

        manager.Dispose();
    }

    // Catches: a sticky footer pasted into the middle of the image each step. Only the last frame's footer may remain.
    [Fact]
    public void AFixedFooterIsOnlyAtTheBottom()
    {
        using ScrollingCaptureManager manager = Stitch([0, 37, 70, 100], footer: 24);
        SKBitmap image = manager.Result;
        int footerRows = Enumerable.Range(0, image.Height).Count(y => image.GetPixel(10, y) == Footer);

        Assert.Equal(24, footerRows);
        Assert.Equal(Footer, image.GetPixel(10, image.Height - 1));
        AssertLines(image, 0, (100 + Height - 24 - 1) / LineHeight);
    }

    // Catches: a fixed header being stitched in again, as the frame's top rows differ from the page's.
    [Fact]
    public void AFixedHeaderIsOnlyAtTheTop()
    {
        using ScrollingCaptureManager manager = Stitch([0, 37, 70, 130], header: 24);
        SKBitmap image = manager.Result;
        int headerRows = Enumerable.Range(0, image.Height).Count(y => image.GetPixel(10, y) == Header);

        Assert.Equal(24, headerRows);
        AssertLines(image, 24 / LineHeight, (130 + Height - 1) / LineHeight);
    }

    // Catches: comparing the whole width. A scrollbar thumb moves with each step, so no row would ever match.
    [Fact]
    public void AMovingScrollbarDoesNotStopTheMatch()
    {
        using ScrollingCaptureManager manager = Stitch([0, 40, 100, 150], scrollbarWidth: 12);

        AssertLines(manager.Result, 0, (150 + Height - 1) / LineHeight);
    }

    // Catches: no limit on the stitched height, which is what keeps memory safe on a page that never ends.
    [Fact]
    public void StopsAtTheHeightLimitWithTheImageSoFar()
    {
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions { MaxStitchedHeight = 300 });
        ScrollingFrameResult last = ScrollingFrameResult.Added;

        for (int top = 0; top < 1000 && last != ScrollingFrameResult.HeightLimitReached; top += 40)
        {
            using SKBitmap frame = Frame(top);
            last = manager.AddFrame(frame);
        }

        Assert.Equal(ScrollingFrameResult.HeightLimitReached, last);
        Assert.InRange(manager.Result.Height, 260, 300);
        Assert.Equal(0, ReadLines(manager.Result)[0]);

        manager.Dispose();
    }

    // Catches: reading rows at the old width's stride after a window is resized, which reads outside the bitmap.
    [Fact]
    public void AResizedWindowIsNotCompared()
    {
        ScrollingCaptureManager manager = new(new ScrollingCaptureOptions());
        using SKBitmap first = Frame(0), wider = Frame(40, width: Width + 40);

        manager.AddFrame(first);

        Assert.Equal(ScrollingFrameResult.NoOverlap, manager.AddFrame(wider));
        Assert.Equal(Width, manager.Result.Width);

        manager.Dispose();
    }
}
