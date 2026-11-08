namespace DataAnalyzer.Core.Data;

/// <summary>
/// 시간 열을 정한 뒤의 데이터. 시간 값이 유효한 행만 남기고 시간 순으로 정렬한다.
/// </summary>
public sealed class DataSet
{
    public required double[] Time { get; init; }
    /// <summary>시간 열을 뺀 신호 이름 (CSV 순서)</summary>
    public required List<string> Names { get; init; }
    public required Dictionary<string, double[]> Signals { get; init; }
    public required Dictionary<string, string> Units { get; init; }
    public required bool IsDate { get; init; }
    public required string TimeName { get; init; }
    public string TimeUnit { get; init; } = "";
    public IReadOnlyDictionary<string, SortedDictionary<int, string>> Categories { get; init; } =
        new Dictionary<string, SortedDictionary<int, string>>();

    public int Count => Time.Length;
    public double Start => Time.Length > 0 ? Time[0] : 0;
    public double End => Time.Length > 0 ? Time[^1] : 0;

    public static DataSet FromRaw(RawTable raw, int timeColumn)
    {
        if (timeColumn < 0 || timeColumn >= raw.Names.Length) throw new ArgumentOutOfRangeException(nameof(timeColumn));
        var tc = raw.Columns[timeColumn];
        var idx = new List<int>(tc.Length);
        for (int i = 0; i < tc.Length; i++) if (double.IsFinite(tc[i])) idx.Add(i);
        bool sorted = true;
        for (int k = 1; k < idx.Count && sorted; k++) if (tc[idx[k]] < tc[idx[k - 1]]) sorted = false;
        if (!sorted) idx = idx.OrderBy(i => tc[i]).ToList(); // 안정 정렬

        double[] Pick(double[] a) { var o = new double[idx.Count]; for (int k = 0; k < o.Length; k++) o[k] = a[idx[k]]; return o; }

        var names = new List<string>();
        var sig = new Dictionary<string, double[]>();
        var units = new Dictionary<string, string>();
        for (int j = 0; j < raw.Names.Length; j++)
        {
            if (j == timeColumn) continue;
            var nm = raw.Names[j];
            if (sig.ContainsKey(nm)) continue; // 같은 이름의 열은 처음 것만 사용
            names.Add(nm);
            sig[nm] = Pick(raw.Columns[j]);
            units[nm] = raw.Units[j];
        }
        return new DataSet
        {
            Time = Pick(tc),
            Names = names,
            Signals = sig,
            Units = units,
            IsDate = raw.IsDate[timeColumn],
            TimeName = raw.Names[timeColumn],
            TimeUnit = raw.Units[timeColumn],
            Categories = raw.Categories,
        };
    }

    /// <summary>시간축을 초 단위로. 날짜 시간축(ms)이거나 숫자 시간이 ms 단위면 1/1000.</summary>
    public double[] TimeSeconds(bool numericIsMs = false)
    {
        double k = IsDate || numericIsMs ? 1e-3 : 1;
        return k == 1 ? Time : Time.Select(x => x * k).ToArray();
    }

    /// <summary>계산 결과 신호를 추가하거나 바꾼다.</summary>
    public void Put(string name, double[] values, string unit = "")
    {
        if (values.Length != Time.Length) throw new ArgumentException("길이가 시간 열과 다릅니다.", nameof(values));
        if (!Signals.ContainsKey(name)) Names.Add(name);
        Signals[name] = values;
        Units[name] = unit;
    }
}
