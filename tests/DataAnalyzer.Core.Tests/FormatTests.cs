using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(1.23456789, "1.23457")]
    [InlineData(100, "100")]
    [InlineData(-0.000123456789, "-0.000123457")]
    [InlineData(0, "0")]
    public void Value_uses_six_significant_digits(double v, string s) => Assert.Equal(s, Format.Value(v));

    [Fact]
    public void Value_NaN_is_dash() => Assert.Equal("—", Format.Value(double.NaN));

    [Theory]
    [InlineData(0.13, 0.1)]
    [InlineData(0.25, 0.2)]
    [InlineData(4, 5)]
    [InlineData(80, 100)]
    public void Nice_steps(double s, double expected) => Assert.Equal(expected, Format.Nice(s), 9);

    [Fact]
    public void Time_for_dates_respects_span()
    {
        var ms = (new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Local).ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        Assert.Equal("2026-03-04 05:06:07.089", Format.Time(ms, true));
        Assert.Equal("05:06:07.089", Format.Time(ms, true, 5000));
        Assert.Equal("05:06:07", Format.Time(ms, true, 60000));
        Assert.Equal("12.5", Format.Time(12.5, false));
    }
}
