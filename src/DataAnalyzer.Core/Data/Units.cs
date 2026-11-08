using System.Text.RegularExpressions;

namespace DataAnalyzer.Core.Data;

/// <summary>단위 문자열 정규화와 환산 (HTML의 nu / PUF).</summary>
public static class Units
{
    /// <summary>단위 이름 정규화: deg, rad, mm, cm, m, dps(deg/s), rps(rad/s), rpm, bar, mpa, kpa, pa, psi, s, ms. 그 외는 소문자 그대로.</summary>
    public static string Normalize(string? u)
    {
        var s = Regex.Replace((u ?? "").Trim().ToLowerInvariant(), @"[\s\[\]()]", "");
        s = Regex.Replace(s, "°|º|˚", "deg").Replace("㎜", "mm");
        if (s.Length == 0) return "";
        if (Regex.IsMatch(s, "^(deg|degree|degrees|도)$")) return "deg";
        if (Regex.IsMatch(s, "^(rad|rads|radian|radians)$")) return "rad";
        if (Regex.IsMatch(s, "^(mm|millimeter|millimeters)$")) return "mm";
        if (s == "cm") return "cm";
        if (Regex.IsMatch(s, "^(m|meter|meters)$")) return "m";
        if (Regex.IsMatch(s, @"^(deg/s|deg/sec|dps|deg/second|deg\.s-1)$")) return "dps";
        if (Regex.IsMatch(s, "^(rad/s|rad/sec|rps)$")) return "rps";
        if (Regex.IsMatch(s, "^(rpm|r/min)$")) return "rpm";
        if (s is "bar" or "mpa" or "kpa" or "pa" or "psi") return s;
        if (Regex.IsMatch(s, "^(s|sec|second|seconds)$")) return "s";
        if (Regex.IsMatch(s, "^(ms|msec)$")) return "ms";
        return s;
    }

    /// <summary>압력 단위 → Pa 배율. 압력 단위가 아니면 null.</summary>
    public static double? PressureToPa(string? unit) => Normalize(unit) switch
    {
        "bar" => 1e5,
        "mpa" => 1e6,
        "kpa" => 1e3,
        "pa" => 1,
        "psi" => 6894.757,
        _ => null,
    };

    /// <summary>길이 단위 → m 배율 (mm, cm, 그 외 1).</summary>
    public static double LengthToM(string? unit) => Normalize(unit) switch { "mm" => .001, "cm" => .01, _ => 1 };

    /// <summary>
    /// 신호 이름을 낱말로 나눈다 (camelCase, 영문-숫자 경계, 기호 기준). 예: "BoomCylHeadP1" → boom, cyl, head, p, 1
    /// </summary>
    public static string[] Tokens(string name)
    {
        var s = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2");
        s = Regex.Replace(s, "([A-Za-z])(\\d)", "$1 $2");
        s = Regex.Replace(s, "(\\d)([A-Za-z])", "$1 $2");
        return Regex.Split(s.ToLowerInvariant(), "[^a-z0-9가-힣]+").Where(x => x.Length > 0).ToArray();
    }
}
