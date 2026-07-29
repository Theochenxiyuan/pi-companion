using PiCompanion.Core.Activation;

namespace PiCompanion.Core.Tests;

public sealed class WindowPlacementCalculatorTests
{
    [Fact]
    public void ClampToWorkArea_ProtectsEveryEdge()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        Assert.Equal(
            new PixelRect(0, 0, 440, 620),
            WindowPlacementCalculator.ClampToWorkArea(
                new PixelRect(-200, -100, 240, 520),
                workArea));
        Assert.Equal(
            new PixelRect(1480, 420, 1920, 1040),
            WindowPlacementCalculator.ClampToWorkArea(
                new PixelRect(1800, 900, 2240, 1520),
                workArea));
    }

    [Fact]
    public void ClampToWorkArea_PreservesNegativeCoordinateSecondaryMonitor()
    {
        var result = WindowPlacementCalculator.ClampToWorkArea(
            new PixelRect(-2200, 800, -1760, 1420),
            new PixelRect(-1920, 0, 0, 1040));

        Assert.Equal(new PixelRect(-1920, 420, -1480, 1040), result);
    }

    [Fact]
    public void PlaceNearPoint_FlipsLeftAndUpAtBottomRightEdge()
    {
        var result = WindowPlacementCalculator.PlaceNearPoint(
            new ScreenPoint(1900, 1040),
            new PixelSize(660, 670),
            new PixelRect(0, 0, 1920, 1080),
            18);

        Assert.Equal(new PixelRect(1222, 352, 1882, 1022), result);
    }

    [Fact]
    public void PlaceNearPoint_ClampsToNegativeCoordinateWorkArea()
    {
        var result = WindowPlacementCalculator.PlaceNearPoint(
            new ScreenPoint(-1915, -10),
            new PixelSize(800, 700),
            new PixelRect(-1920, 0, 0, 1040),
            18);

        Assert.Equal(-1897, result.Left);
        Assert.Equal(8, result.Top);
        Assert.True(result.Right <= 0);
        Assert.True(result.Bottom <= 1040);
    }

    [Fact]
    public void PlaceNearPoint_ShrinksOversizedWindowToWorkArea()
    {
        var workArea = new PixelRect(100, 50, 500, 350);

        var result = WindowPlacementCalculator.PlaceNearPoint(
            new ScreenPoint(250, 150),
            new PixelSize(900, 800),
            workArea,
            18);

        Assert.Equal(workArea, result);
    }
}
