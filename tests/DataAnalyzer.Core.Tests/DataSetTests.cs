using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

public class DataSetTests
{
    [Fact]
    public void Drops_invalid_time_and_sorts()
    {
        var raw = CsvParser.Parse("a,t\n10,2\n11,\n12,0\n13,1");
        var d = DataSet.FromRaw(raw, 1);
        Assert.Equal(new double[] { 0, 1, 2 }, d.Time);
        Assert.Equal(new double[] { 12, 13, 10 }, d.Signals["a"]);
        Assert.Equal(new[] { "a" }, d.Names);
        Assert.Equal("t", d.TimeName);
    }

    [Fact]
    public void Put_adds_signal_once()
    {
        var d = DataSet.FromRaw(CsvParser.Parse("t,a\n0,1\n1,2"), 0);
        d.Put("b", new double[] { 5, 6 }, "bar");
        d.Put("b", new double[] { 7, 8 }, "bar");
        Assert.Equal(new[] { "a", "b" }, d.Names);
        Assert.Equal(8, d.Signals["b"][1]);
        Assert.Throws<ArgumentException>(() => d.Put("c", new double[] { 1 }));
    }
}
