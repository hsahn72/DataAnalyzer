using DataAnalyzer.Core.Analysis;

namespace DataAnalyzer.Core.Tests;

public class CycleViewTests
{
    //                      0  1  2  3  4  5  6  7  8  9
    static readonly double[] T = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
    static readonly double[] Cyc = { 0, 1, 1, 1, 0, 2, 2, 2, 2, 0 };
    static readonly double[] Act = { 0, 1, 2, 3, 0, 1, 1, 2, 3, 0 };

    [Fact]
    public void Finds_columns_and_values()
    {
        Assert.Equal("Cycle_No", CycleView.FindCycleColumn(new[] { "a", "Cycle_No", "Cycle" }));
        Assert.Equal("Action", CycleView.FindActionColumn(new[] { "Actions", "Action" }));
        Assert.Null(CycleView.FindCycleColumn(new[] { "Cycles" }));
        Assert.Equal(new[] { 1, 2 }, CycleView.CycleValues(Cyc));
        Assert.Equal(new[] { 0, 1, 2, 3 }, CycleView.ActionValues(Act));
    }

    [Fact]
    public void Mask_selects_cycle_and_action()
    {
        var m = CycleView.Mask(10, Cyc, Act, 2, 1);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 1, 1, 0, 0, 0 }, m);
        Assert.True(double.IsNaN(CycleView.ApplyMask(T, m)[0]));
        Assert.Equal(5, CycleView.ApplyMask(T, m)[5]);
    }

    [Fact]
    public void Groups_start_each_cycle_at_zero()
    {
        var (list, mx) = CycleView.Groups(T, Cyc, Act, null, null, 1);
        Assert.Equal(new[] { 1, 2 }, list.Select(g => g.Cycle));
        Assert.Equal(new double[] { 0, 1, 2 }, list[0].Rel);
        Assert.Equal(3, mx);
        var (dig, mx2) = CycleView.Groups(T, Cyc, Act, null, 1, 1);
        Assert.Equal(new[] { 6 }, dig[1].Index.Skip(1));
        Assert.Equal(1, mx2);
    }

    [Fact]
    public void Band_of_identical_cycles_has_zero_spread()
    {
        double[] t = { 0, 1, 2, 3, 4, 5 }, c = { 1, 1, 1, 2, 2, 2 }, y = { 0, 10, 20, 0, 10, 20 };
        var (g, mx) = CycleView.Groups(t, c, null, null, null, 1);
        var b = CycleView.Band(y, g, mx, 5);
        Assert.Equal(new double[] { 0, 5, 10, 15, 20 }, b.Mean);
        Assert.All(b.Sd, s => Assert.Equal(0, s, 9));
        Assert.All(b.Count, n => Assert.Equal(2, n));
    }
}
