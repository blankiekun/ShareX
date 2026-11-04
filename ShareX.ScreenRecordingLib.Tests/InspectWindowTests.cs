using ShareX.HelpersLib;
using ShareX.Tools;
using System.Drawing;
using Xunit;

namespace ShareX.ScreenRecordingLib.Tests;

public class InspectWindowTests
{
    // Catches: a field with no value on this window, such as the role of most windows, showing as an empty box.
    [Fact]
    public void BuildDetails_LeavesOutEveryFieldWithoutAValue()
    {
        Rectangle frame = new(0, 0, 400, 300);
        KWinWindowDetails window = new("{id}", "Title", "class", "", "", "", 5, frame, frame, "", "normalWindow", 1, false, []);

        IReadOnlyList<InspectWindowProperty> rows = InspectWindowViewModel.BuildDetails(window, "", "");

        Assert.All(rows, x => Assert.False(string.IsNullOrWhiteSpace(x.Value)));
        Assert.Contains(rows, x => x.Value == "Title");
    }
}
