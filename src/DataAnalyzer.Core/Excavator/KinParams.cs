using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataAnalyzer.Core.Excavator;

/// <summary>
/// 굴착기 Kinematic·동역학 파라미터 (HTML의 K 객체). 숫자 값은 이름 → 값 사전으로 두고,
/// 문자열 설정(unit, IRef, SwAxis, pUnit …)과 점 역할(roles)은 따로 둔다.
/// JSON은 HTML의 'JSON 내보내기' 형식(평평한 객체)과 호환된다.
/// </summary>
public sealed class KinParams
{
    /// <summary>문자열로 다루는 키</summary>
    public static readonly HashSet<string> TextKeys = new() { "name", "desc", "unit", "IRef", "SwAxis", "pUnit" };

    public Dictionary<string, double> Values { get; } = new();
    public Dictionary<string, string> Text { get; } = new();
    /// <summary>역할 → 점 이름 (boomBase → P20 …). 비어 있으면 기본 배정.</summary>
    public Dictionary<string, string> Roles { get; } = new();

    public string Name { get => Text.GetValueOrDefault("name", ""); set => Text["name"] = value; }
    /// <summary>길이 단위: "m" 또는 그 외(mm)</summary>
    public string Unit { get => Text.GetValueOrDefault("unit", "mm"); set => Text["unit"] = value; }

    /// <summary>숫자 값 (없거나 숫자가 아니면 NaN)</summary>
    public double this[string key]
    {
        get => Values.TryGetValue(key, out var v) ? v : double.NaN;
        set { if (double.IsNaN(value)) Values.Remove(key); else Values[key] = value; }
    }

    public string Str(string key, string def = "") => Text.TryGetValue(key, out var s) ? s : def;

    public KinParams Clone()
    {
        var k = new KinParams();
        foreach (var (a, b) in Values) k.Values[a] = b;
        foreach (var (a, b) in Text) k.Text[a] = b;
        foreach (var (a, b) in Roles) k.Roles[a] = b;
        return k;
    }

    public static KinParams FromJson(string json) => FromNode(JsonNode.Parse(json) as JsonObject ?? throw new FormatException("JSON 객체가 아닙니다."));

    public static KinParams FromNode(JsonObject o)
    {
        var k = new KinParams();
        foreach (var (key, node) in o)
        {
            if (node == null) continue;
            if (key == "roles" && node is JsonObject ro)
            {
                foreach (var (rk, rv) in ro) if (rv != null) k.Roles[rk] = rv.ToString();
                continue;
            }
            if (node is not JsonValue v) continue;
            if (v.TryGetValue<double>(out var d)) { if (TextKeys.Contains(key)) k.Text[key] = d.ToString(CultureInfo.InvariantCulture); else k.Values[key] = d; }
            else if (v.TryGetValue<string>(out var s))
            {
                if (!TextKeys.Contains(key) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) k.Values[key] = x;
                else k.Text[key] = s;
            }
        }
        return k;
    }

    public string ToJson()
    {
        var o = new JsonObject();
        foreach (var (a, b) in Text) o[a] = b;
        foreach (var (a, b) in Values.OrderBy(x => x.Key, StringComparer.Ordinal)) o[a] = b;
        if (Roles.Count > 0) { var r = new JsonObject(); foreach (var (a, b) in Roles) r[a] = b; o["roles"] = r; }
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>화면 확인용 예시 20t급 (임의 값, m 단위). HTML의 DEMO_K와 같다.</summary>
    public static KinParams Demo()
    {
        var k = new KinParams();
        k.Text["name"] = "예시 20t급 (임의 값)";
        k.Text["desc"] = "화면 확인용 예시입니다. 실제 장비 값으로 교체하세요.";
        k.Text["unit"] = "mm";
        var mm = new Dictionary<string, double>
        {
            ["BPH"] = 1500, ["L1"] = 900, ["L2"] = 5700, ["L3"] = 2900, ["L4"] = 1450, ["Lgl"] = 659, ["Lpl"] = 522,
            ["P20x"] = 550, ["P20y"] = -550, ["P21x"] = 1600, ["P21y"] = -600, ["P30x"] = 3300, ["P30y"] = 900, ["P31x"] = 788, ["P31y"] = -139,
            ["P40x"] = 1112, ["P40y"] = 772, ["P41x"] = 177, ["P41y"] = 210, ["PGLx"] = 2732, ["PGLy"] = -126,
            ["BmLmin"] = 950, ["BmLmax"] = 1850, ["BmDt"] = 130, ["BmDr"] = 90, ["AmLmin"] = 2100, ["AmLmax"] = 3400, ["AmDt"] = 140, ["AmDr"] = 100,
            ["BkLmin"] = 1500, ["BkLmax"] = 2400, ["BkDt"] = 120, ["BkDr"] = 85,
            ["C1x"] = -400, ["C1y"] = 0, ["C1z"] = 1100, ["C2x"] = 2600, ["C2y"] = 150, ["C3x"] = 1400, ["C3y"] = 100, ["C4x"] = 600, ["C4y"] = -100,
        };
        foreach (var (a, b) in mm) k.Values[a] = b / 1000;
        var other = new Dictionary<string, double>
        {
            ["M1"] = 12000, ["M2"] = 2200, ["M3"] = 1100, ["M4"] = 700, ["I1xx"] = 9000, ["I1yy"] = 28000, ["I1zz"] = 30000,
            ["I2xx"] = 100, ["I2yy"] = 6100, ["I2zz"] = 6000, ["I3xx"] = 50, ["I3yy"] = 810, ["I3zz"] = 800, ["I4xx"] = 90, ["I4yy"] = 140, ["I4zz"] = 130,
            ["BmN"] = 2, ["AmN"] = 1, ["BkN"] = 1, ["SwDisp"] = 250, ["SwRatio"] = 25, ["SwEff"] = .9,
            ["sB"] = 1, ["sA"] = 1, ["sJ"] = -1, ["sP"] = -1,
        };
        foreach (var (a, b) in other) k.Values[a] = b;
        k.Text["unit"] = "m"; k.Text["IRef"] = "com"; k.Text["SwAxis"] = "zz"; k.Text["pUnit"] = "bar";
        foreach (var (a, b) in Kinematics.DefaultRoles) k.Roles[a] = b;
        return k;
    }
}
