using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

public class SeriesTests
{
    static readonly double[] T = { 0, 1, 2, 3 };
    static readonly double[] A = { 0, 10, double.NaN, 30 };

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0.5, 5)]
    [InlineData(1, 10)]
    [InlineData(1.4, 10)]   // 한쪽이 NaN이면 가까운 샘플
    [InlineData(1.6, double.NaN)]
    [InlineData(9, 30)]
    public void ValAt_interpolates(double tm, double expected) =>
        Assert.Equal(expected, Series.ValAt(T, A, tm), 9);

    [Fact]
    public void ValNear_is_NaN_outside_range()
    {
        Assert.True(double.IsNaN(Series.ValNear(T, A, -0.1)));
        Assert.Equal(10, Series.ValNear(T, A, 0.6));
        Assert.Equal(0, Series.ValNear(T, A, 0.5)); // 정확히 가운데면 앞 샘플
    }

    [Fact]
    public void Snap_and_step()
    {
        Assert.Equal(1, Series.Snap(T, 1.2));
        Assert.Equal(3, Series.Snap(T, 10));
        Assert.Equal(2, Series.Step(T, 1, +1, 1));
        Assert.Equal(0, Series.Step(T, 1, -1, 10));
        Assert.Equal(2, Series.Step(T, 1.5, +1, 1)); // 샘플 사이에서는 다음 샘플로
        Assert.Equal(1, Series.Step(T, 1.5, -1, 1));
    }

    [Fact]
    public void Mean_skips_NaN()
    {
        Assert.Equal(20, Series.MeanBetween(T, A, 0.5, 3), 9);
        Assert.True(double.IsNaN(Series.MeanBetween(T, A, 5, 6)));
    }

    [Fact]
    public void Visible_range_and_pad()
    {
        Assert.Equal((1, 3), Series.VisibleRange(T, 1.5, 2.5));
        Assert.Equal((0, 3), Series.VisibleRange(T, -5, 9));
        Assert.Equal((-0.06, 1.06), Series.Pad(0, 1));
        Assert.Equal((0, 1), Series.Pad(double.PositiveInfinity, double.NegativeInfinity));
    }
}
