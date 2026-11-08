using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataAnalyzer.Core.Config;

/// <summary>선 스타일. HTML 뷰어의 sty 객체와 같은 필드.</summary>
public sealed class LineStyle
{
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = 1.4;
    /// <summary>solid / dash / dot / dashdot</summary>
    [JsonPropertyName("dash")] public string Dash { get; set; } = "solid";
    /// <summary>XY 차트: line / both / marker</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "line";
    /// <summary>XY 마커: circle / square / triangle / diamond / x</summary>
    [JsonPropertyName("shape")] public string Shape { get; set; } = "circle";
    [JsonPropertyName("msz")] public double MarkerSize { get; set; } = 3;

    public LineStyle Clone() => (LineStyle)MemberwiseClone();
}

public sealed class TimeChartConfig
{
    [JsonPropertyName("sigs")] public List<string> Signals { get; set; } = new();
    [JsonPropertyName("norm")] public bool Normalized { get; set; }
    [JsonPropertyName("ymin")] public double? YMin { get; set; }
    [JsonPropertyName("ymax")] public double? YMax { get; set; }
    [JsonPropertyName("sty")] public Dictionary<string, LineStyle> Styles { get; set; } = new();
}

public sealed class XYChartConfig
{
    [JsonPropertyName("x")] public string? X { get; set; }
    [JsonPropertyName("ys")] public List<string> Ys { get; set; } = new();
    /// <summary>true = 전체 구간, false = 시계열 차트의 표시 구간만</summary>
    [JsonPropertyName("all")] public bool All { get; set; }
    [JsonPropertyName("xmin")] public double? XMin { get; set; }
    [JsonPropertyName("xmax")] public double? XMax { get; set; }
    [JsonPropertyName("ymin")] public double? YMin { get; set; }
    [JsonPropertyName("ymax")] public double? YMax { get; set; }
    [JsonPropertyName("sty")] public Dictionary<string, LineStyle> Styles { get; set; } = new();
}

/// <summary>
/// 차트 구성. HTML 뷰어의 '구성·스타일 저장/불러오기' JSON(app = sigchart, v = 1)과 호환된다.
/// </summary>
public sealed class ChartConfig
{
    [JsonPropertyName("app")] public string App { get; set; } = "sigchart";
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("charts")] public List<TimeChartConfig>? Charts { get; set; } = new();
    [JsonPropertyName("xy")] public List<XYChartConfig>? XY { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>JSON → 구성. charts 배열이 없으면 형식 오류.</summary>
    public static ChartConfig FromJson(string json)
    {
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("charts", out var ch) || ch.ValueKind != JsonValueKind.Array)
                throw new FormatException("형식 오류");
        }
        var c = JsonSerializer.Deserialize<ChartConfig>(json, Json) ?? throw new FormatException("형식 오류");
        c.Charts ??= new();
        c.XY ??= new();
        return c;
    }

    /// <summary>
    /// 구성의 신호 이름을 데이터의 실제 이름으로 바꾼다 (앞뒤 공백·대소문자 무시).
    /// 데이터에 없는 신호는 빼고, 뺀 개수를 돌려준다.
    /// </summary>
    public ChartConfig MapTo(IEnumerable<string> names, out int missing)
    {
        var map = new Dictionary<string, string>();
        foreach (var n in names) map.TryAdd(n.Trim().ToLowerInvariant(), n);
        string? M(string? s) => s != null && map.TryGetValue(s.Trim().ToLowerInvariant(), out var m) ? m : null;
        int miss = 0;
        Dictionary<string, LineStyle> MapSty(Dictionary<string, LineStyle> sty, List<string> src, List<string> dst)
        {
            var o = new Dictionary<string, LineStyle>();
            for (int i = 0; i < src.Count; i++)
            {
                var m = M(src[i]);
                if (m == null) { miss++; continue; }
                if (!dst.Contains(m)) dst.Add(m);
                if (sty.TryGetValue(src[i], out var st)) o[m] = st.Clone();
            }
            return o;
        }
        var res = new ChartConfig();
        foreach (var k in Charts ?? new())
        {
            var c = new TimeChartConfig { Normalized = k.Normalized, YMin = k.YMin, YMax = k.YMax };
            c.Styles = MapSty(k.Styles, k.Signals, c.Signals);
            res.Charts!.Add(c);
        }
        foreach (var k in XY ?? new())
        {
            var x = M(k.X);
            if (x == null) miss++;
            var c = new XYChartConfig { X = x, All = k.All, XMin = k.XMin, XMax = k.XMax, YMin = k.YMin, YMax = k.YMax };
            c.Styles = MapSty(k.Styles, k.Ys, c.Ys);
            res.XY!.Add(c);
        }
        missing = miss;
        return res;
    }
}
