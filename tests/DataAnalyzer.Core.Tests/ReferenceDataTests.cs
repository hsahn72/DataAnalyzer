using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

public class ReferenceDataTests
{
    static (RawTable, DataSet) Current()
    {
        var raw = CsvParser.Parse("t,a\n10,1\n11,2\n12,3");
        return (raw, DataSet.FromRaw(raw, 0));
    }

    [Fact]
    public void Rejects_different_columns()
    {
        var (raw, d) = Current();
        var r = ReferenceData.Create(CsvParser.Parse("t,b\n0,1"), "ref.csv", raw, d, out var err);
        Assert.Null(r);
        Assert.Contains("기준에 없음: a", err);
        Assert.Contains("기준에만 있음: b", err);
    }

    [Fact]
    public void Offset_and_align()
    {
        var (raw, d) = Current();
        var r = ReferenceData.Create(CsvParser.Parse("t,a\n0,5\n1,6"), "ref.csv", raw, d, out var err)!;
        Assert.Equal("", err);
        Assert.True(double.IsNaN(r.ValNear("a", 10)));
        r.AlignStart(d);
        Assert.Equal(10, r.Offset);
        Assert.Equal(6, r.ValNear("a", 11));
        r.SetOffset(10.5);
        Assert.Equal(new[] { 10.5, 11.5 }, r.Time);
    }
}
