using System.Globalization;
using System.Text.RegularExpressions;

namespace DataAnalyzer.Core.Data;

/// <summary>
/// CSV 파서. HTML 'CSV 시계열 차트 뷰어'의 parseCSV와 같은 규칙을 따른다.
/// <list type="bullet">
/// <item>구분자: 첫 줄에서 ',', ';', 탭 중 가장 많이 나뉘는 것</item>
/// <item>열 이름의 [단위] 표기, 또는 2행이 단위 행(숫자·날짜가 하나도 없는 행)이면 단위로 사용</item>
/// <item>숫자가 아닌 값은 날짜로 읽어 보고, 성공하면 Unix epoch 기준 ms 값으로 저장</item>
/// <item>이름에 'action'이 들어간 열의 문자열은 코드로 바꾼다 (Dig=1, Haul=2, Dump=3, Return=4, 그 외 5부터)</item>
/// </list>
/// </summary>
public static class CsvParser
{
    static readonly Regex UnitInName = new(@"\[([^\]]*)\]", RegexOptions.Compiled);
    static readonly Regex Digit = new(@"\d", RegexOptions.Compiled);
    /// <summary>날짜(2026-01-02, 2026/1/2, 02.01.2026) 또는 시각(12:30) 모양이 있어야 날짜로 본다.</summary>
    static readonly Regex DateLike = new(@"\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}|\d{1,2}:\d{2}", RegexOptions.Compiled);
    static readonly Regex ActionName = new("action", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Action 문자열 → 고정 코드. 순서대로 검사한다.</summary>
    static readonly (Regex Rx, int Code)[] KnownActions =
    {
        (new Regex("dig|굴착|굴삭", RegexOptions.IgnoreCase), 1),
        (new Regex("haul|운반", RegexOptions.IgnoreCase), 2),
        (new Regex("dump|덤|적재|방토", RegexOptions.IgnoreCase), 3),
        (new Regex("return|복귀|귀환", RegexOptions.IgnoreCase), 4),
    };

    public static RawTable ParseFile(string path) => Parse(File.ReadAllText(path));

    public static RawTable Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var lines = Regex.Split(text, "\r?\n").Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) throw new FormatException("빈 파일입니다.");

        char del = ',';
        int best = -1;
        foreach (var d in new[] { ',', ';', '\t' })
        {
            int k = lines[0].Split(d).Length;
            if (k > best) { best = k; del = d; }
        }
        string[] Sp(string l) => l.Split(del).Select(Clean).ToArray();

        var names = Sp(lines[0]);
        int nc = names.Length;
        var isDate = new bool[nc];
        var units = names.Select(n => { var m = UnitInName.Match(n); return m.Success ? CleanUnit(m.Groups[1].Value) : ""; }).ToArray();

        // 2행이 단위 행인가: 비어 있지 않은 칸이 있고, 그 중 숫자/날짜로 읽히는 칸이 하나도 없으면 단위 행
        int start = 1;
        bool unitRow = false;
        if (lines.Count > 1)
        {
            var r2 = Sp(lines[1]);
            int ne = 0, num = 0;
            foreach (var v in r2)
            {
                if (v.Length == 0) continue;
                ne++;
                if ((TryNumber(v, out var x) && double.IsFinite(x)) || (Digit.IsMatch(v) && TryDate(v, out _))) num++;
            }
            if (ne > 0 && num == 0)
            {
                unitRow = true;
                start = 2;
                for (int j = 0; j < nc; j++)
                {
                    var u = j < r2.Length ? CleanUnit(r2[j]) : "";
                    if (u.Length > 0) units[j] = u;
                }
            }
        }

        int n = lines.Count - start;
        var cols = new double[nc][];
        for (int j = 0; j < nc; j++) cols[j] = new double[n];
        var actionCols = Enumerable.Range(0, nc).Where(j => ActionName.IsMatch(names[j])).ToArray();
        var actionText = actionCols.ToDictionary(j => j, _ => new string?[n]);

        for (int i = 0; i < n; i++)
        {
            var r = Sp(lines[i + start]);
            for (int j = 0; j < nc; j++)
            {
                string? v = j < r.Length ? r[j] : null;
                double x;
                if (string.IsNullOrEmpty(v)) x = double.NaN;
                else if (!TryNumber(v, out x))
                {
                    x = double.NaN;
                    if (TryDate(v, out var d)) { x = d; isDate[j] = true; }
                }
                cols[j][i] = x;
                if (actionText.TryGetValue(j, out var at)) at[i] = v;
            }
        }

        var cats = new Dictionary<string, SortedDictionary<int, string>>();
        foreach (var j in actionCols)
        {
            if (isDate[j]) continue;
            var raw = actionText[j];
            int tx = raw.Count(v => !string.IsNullOrEmpty(v) && !TryNumber(v, out _));
            if (tx == 0) continue;
            var lab = new SortedDictionary<int, string>();
            var code = new Dictionary<string, int>();
            int next = 5;
            for (int i = 0; i < n; i++)
            {
                var v = raw[i];
                if (string.IsNullOrEmpty(v)) { cols[j][i] = double.NaN; continue; }
                if (!code.TryGetValue(v, out var c))
                {
                    var k = KnownActions.FirstOrDefault(a => a.Rx.IsMatch(v));
                    c = k.Rx != null ? k.Code : next++;
                    code[v] = c;
                    lab.TryAdd(c, v);
                }
                cols[j][i] = c;
            }
            cats[names[j]] = lab;
        }

        return new RawTable(names, cols, isDate, units, unitRow, cats);
    }

    /// <summary>앞뒤 공백과 따옴표 한 쌍을 제거 (HTML과 동일: 앞의 " 하나, 뒤의 " 하나)</summary>
    static string Clean(string s)
    {
        s = s.Trim();
        if (s.StartsWith('"')) s = s[1..];
        if (s.EndsWith('"')) s = s[..^1];
        return s;
    }

    public static string CleanUnit(string? u)
    {
        var s = (u ?? "").Trim();
        s = Regex.Replace(s, @"^[\[(]\s*|\s*[\])]$", "");
        return s.Trim();
    }

    public static bool TryNumber(string v, out double x) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x);

    /// <summary>날짜 문자열 → Unix epoch 기준 ms (현지 시각으로 해석). 날짜·시각 모양이 없으면 날짜로 보지 않는다.</summary>
    public static bool TryDate(string v, out double ms)
    {
        ms = double.NaN;
        if (!DateLike.IsMatch(v)) return false;
        if (!DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var dto))
            return false;
        ms = (dto.UtcDateTime - DateTime.UnixEpoch).TotalMilliseconds;
        return true;
    }
}
