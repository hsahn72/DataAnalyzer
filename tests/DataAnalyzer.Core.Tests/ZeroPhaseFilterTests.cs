using DataAnalyzer.Core.Analysis;

namespace DataAnalyzer.Core.Tests;

/// <summary>기대값은 HTML 뷰어의 flFill / flMA / flLP를 같은 입력으로 실행한 결과.</summary>
public class ZeroPhaseFilterTests
{
    static readonly int[] Idx = { 0, 5, 10, 11, 50, 119, 120, 121, 199 };

    static double[] Input()
    {
        var a = new double[200];
        for (int i = 0; i < a.Length; i++) a[i] = Math.Sin(i * 0.07) * 3 + ((i * 7919) % 13 - 6) * 0.2 + (i > 120 ? 2 : 0);
        a[10] = a[11] = double.NaN;
        return a;
    }

    static void Same(double[] expected, double[] actual)
    {
        for (int k = 0; k < Idx.Length; k++) Assert.Equal(expected[k], actual[Idx[k]], 9);
    }

    [Fact]
    public void Fill_matches_html() => Same(new[] { -1.2000000000000002, 1.8286934223663542, 2.1229326358553986, 2.6784309978839884, -0.4523496830688607, 2.2664795679134633, 2.563796724264842, 4.848556405559485, 5.335852704459615 },
        ZeroPhaseFilter.FillGaps(Input())!);

    [Fact]
    public void Moving_average_matches_html() => Same(new[] { -1.2000000000000002, 0.8573347709708495, 1.847491937693434, 1.9267290198900853, -1.1358447810887264, 3.4016240276465877, 3.410529600056904, 3.4071831270259962, 5.335852704459626 },
        ZeroPhaseFilter.MovingAverage(ZeroPhaseFilter.FillGaps(Input())!, 10));

    [Fact]
    public void Low_pass_matches_html() => Same(new[] { -1.2000000123790941, 1.2007359257896066, 1.958561346001832, 2.076773976635531, -1.0794906914650788, 3.1300480824979537, 3.4002195633451446, 3.681157190512714, 5.335852696871216 },
        ZeroPhaseFilter.LowPass(ZeroPhaseFilter.FillGaps(Input())!, 3, 50));

    [Fact]
    public void Apply_keeps_NaN_positions_and_names()
    {
        var y = ZeroPhaseFilter.Apply(Input(), FilterKind.MovingAverage, 10)!;
        Assert.True(double.IsNaN(y[10]));
        Assert.False(double.IsNaN(y[12]));
        Assert.Null(ZeroPhaseFilter.Apply(new[] { double.NaN }, FilterKind.MovingAverage, 3));
        Assert.Null(ZeroPhaseFilter.Apply(Input(), FilterKind.LowPass, 5, double.NaN));
        Assert.Equal("P_MA11", ZeroPhaseFilter.ResultName("P", FilterKind.MovingAverage, 10));
        Assert.Equal("P_LP2.5Hz", ZeroPhaseFilter.ResultName("P", FilterKind.LowPass, 2.5));
    }

    [Fact]
    public void Sample_rate_uses_median_step()
    {
        Assert.Equal(100, ZeroPhaseFilter.SampleRate(new[] { 0, .01, .02, .03, 1.0 }), 9);
        Assert.True(double.IsNaN(ZeroPhaseFilter.SampleRate(new[] { 1.0 })));
    }
}
