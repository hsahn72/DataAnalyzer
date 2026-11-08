using DataAnalyzer.Core.Config;

namespace DataAnalyzer.Core.Tests;

public class ConfigTests
{
    // HTML 뷰어의 '파일로 내보내기' 결과와 같은 형식
    const string HtmlJson = """
    {"app":"sigchart","v":1,
     "charts":[{"sigs":["Pressure_bar"," flow_lpm ","Gone"],"norm":true,"ymin":null,"ymax":5,
                "sty":{"Pressure_bar":{"color":"#ff0000","width":2,"dash":"dash","mode":"line","shape":"circle","msz":3}}}],
     "xy":[{"x":"Position_mm","ys":["Motor_A"],"all":true,"xmin":null,"xmax":null,"ymin":0,"ymax":null,"sty":{}}]}
    """;

    [Fact]
    public void Reads_html_config_and_maps_names()
    {
        var c = ChartConfig.FromJson(HtmlJson);
        var m = c.MapTo(new[] { "Pressure_bar", "Flow_Lpm", "Position_mm", "Motor_A" }, out var missing);
        Assert.Equal(1, missing);
        var ch = Assert.Single(m.Charts!);
        Assert.Equal(new[] { "Pressure_bar", "Flow_Lpm" }, ch.Signals);
        Assert.True(ch.Normalized);
        Assert.Null(ch.YMin);
        Assert.Equal(5, ch.YMax);
        Assert.Equal("dash", ch.Styles["Pressure_bar"].Dash);
        var xy = Assert.Single(m.XY!);
        Assert.Equal("Position_mm", xy.X);
        Assert.True(xy.All);
        Assert.Equal(0, xy.YMin);
    }

    [Fact]
    public void Round_trips()
    {
        var c = ChartConfig.FromJson(HtmlJson);
        var back = ChartConfig.FromJson(c.ToJson());
        Assert.Equal(c.Charts![0].Signals, back.Charts![0].Signals);
        Assert.Contains("\"sigs\"", c.ToJson());
    }

    [Fact]
    public void Missing_charts_is_format_error() =>
        Assert.Throws<FormatException>(() => ChartConfig.FromJson("{\"app\":\"x\"}"));

    [Fact]
    public void Preset_store_saves_and_removes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var s = new PresetStore(path);
            s.Put("a", ChartConfig.FromJson(HtmlJson));
            Assert.Single(s.Load());
            s.Remove("a");
            Assert.Empty(s.Load());
        }
        finally { File.Delete(path); }
    }
}
