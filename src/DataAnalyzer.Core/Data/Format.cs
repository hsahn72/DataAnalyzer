using System.Globalization;

namespace DataAnalyzer.Core.Data;

/// <summary>값·시각 표시 형식 (HTML의 fmtV / fmtT / nice).</summary>
public static class Format
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>유효숫자 6자리, 불필요한 0 제거. NaN이면 '—'.</summary>
    public static string Value(double v) => double.IsNaN(v) ? "—" : Precision(v, 6);

    /// <summary>JavaScript Number(v.toPrecision(p))를 문자열로 바꾼 것과 같은 결과.</summary>
    public static string Precision(double v, int p)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v > 0 ? "Infinity" : "-Infinity";
        if (v == 0) return "0";
        var r = double.Parse(v.ToString("E" + (p - 1), Inv), Inv);
        return r.ToString("R", Inv);
    }

    /// <summary>
    /// 시간축 값 표시. 숫자 시간은 유효숫자 10자리, 날짜는 현지 시각.
    /// span(ms)이 주어지면 2일 미만은 시:분:초(10초 미만이면 ms까지)만 표시.
    /// </summary>
    public static string Time(double x, bool isDate, double? span = null)
    {
        if (!isDate) return Precision(x, 10);
        if (!double.IsFinite(x)) return "—";
        var d = DateTime.UnixEpoch.AddMilliseconds(x).ToLocalTime();
        if (span is double s && s > 0 && s < 1728e5)
            return d.ToString(s < 1e4 ? "HH:mm:ss.fff" : "HH:mm:ss", Inv);
        return d.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);
    }

    /// <summary>눈금 간격용 '보기 좋은' 수 (1, 2, 5 × 10^n).</summary>
    public static double Nice(double s)
    {
        var p = Math.Pow(10, Math.Floor(Math.Log10(s)));
        var f = s / p;
        return (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) * p;
    }

    /// <summary>텍스트 입력 → 숫자 (빈 칸·잘못된 값은 null).</summary>
    public static double? ParseNullable(string? s) =>
        double.TryParse(s, NumberStyles.Float, Inv, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>시간축 입력 → 값 (날짜 시간축이면 날짜 문자열, 아니면 숫자).</summary>
    public static double ParseTime(string? s, bool isDate)
    {
        if (string.IsNullOrWhiteSpace(s)) return double.NaN;
        if (isDate) return CsvParser.TryDate(s.Trim(), out var ms) ? ms : double.NaN;
        return ParseNullable(s) ?? double.NaN;
    }
}
