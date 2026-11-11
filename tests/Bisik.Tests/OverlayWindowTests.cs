using Bisik.UI;

namespace Bisik.Tests;

public class OverlayWindowTests
{
    [Fact]
    public void Quiet_speech_has_visible_range_while_silence_stays_flat()
    {
        Assert.Equal(0, OverlayWindow.DisplayLevel(0));
        Assert.Equal(0, OverlayWindow.DisplayLevel(0.0001));
        Assert.InRange(OverlayWindow.DisplayLevel(0.01), 0.4, 0.5);
        Assert.InRange(OverlayWindow.DisplayLevel(0.03), 0.6, 0.8);
        Assert.Equal(1, OverlayWindow.DisplayLevel(1));
        Assert.Equal(0, OverlayWindow.DisplayLevel(double.NaN));
    }
}
