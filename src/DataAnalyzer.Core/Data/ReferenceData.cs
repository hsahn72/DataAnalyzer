namespace DataAnalyzer.Core.Data;

/// <summary>
/// 기준(비교) CSV. 열 구성이 현재 파일과 같아야 하며, 시간 오프셋(초)을 더해 겹쳐 그린다.
/// </summary>
public sealed class ReferenceData
{
    public RawTable Raw { get; }
    public string FileName { get; }
    /// <summary>시간 오프셋 [s]. 날짜 시간축이면 ms로 바꿔 더한다.</summary>
    public double Offset { get; private set; }
    public double[] Time0 { get; private set; } = Array.Empty<double>();
    public double[] Time { get; private set; } = Array.Empty<double>();
    public Dictionary<string, double[]> Signals { get; private set; } = new();
    public bool IsDate { get; private set; }

    ReferenceData(RawTable raw, string fileName) { Raw = raw; FileName = fileName; }

    /// <summary>현재 데이터에 맞춰 기준 데이터를 만든다. 실패하면 error에 이유.</summary>
    public static ReferenceData? Create(RawTable raw, string fileName, RawTable current, DataSet data, out string error)
    {
        var r = new ReferenceData(raw, fileName);
        error = r.Rebuild(current, data);
        return error.Length == 0 ? r : null;
    }

    /// <summary>열 구성이 같은지 검사. 같으면 빈 문자열.</summary>
    public static string CheckColumns(RawTable current, RawTable reference)
    {
        var a = current.Names; var b = reference.Names;
        var sa = new HashSet<string>(a); var sb = new HashSet<string>(b);
        var onlyCur = a.Where(n => !sb.Contains(n)).ToList();
        var onlyRef = b.Where(n => !sa.Contains(n)).ToList();
        if (onlyCur.Count == 0 && onlyRef.Count == 0 && a.Length == b.Length) return "";
        static string List(List<string> l) => string.Join(", ", l.Take(4)) + (l.Count > 4 ? $" 외 {l.Count - 4}" : "");
        var msg = "열이 현재 파일과 같지 않습니다";
        if (onlyCur.Count > 0) msg += " · 기준에 없음: " + List(onlyCur);
        if (onlyRef.Count > 0) msg += " · 기준에만 있음: " + List(onlyRef);
        return msg;
    }

    /// <summary>현재 데이터의 시간 열 기준으로 다시 만든다 (시간 열을 바꿨을 때). 실패하면 이유.</summary>
    public string Rebuild(RawTable current, DataSet data)
    {
        var e = CheckColumns(current, Raw);
        if (e.Length > 0) return e;
        int k = Array.IndexOf(Raw.Names, data.TimeName);
        var d = DataSet.FromRaw(Raw, k);
        if (d.Count == 0) return $"기준 파일의 시간 열 \"{data.TimeName}\"에 유효한 값이 없습니다";
        if (d.IsDate != data.IsDate) return "시간 열 형식(날짜/숫자)이 현재 파일과 다릅니다";
        Time0 = d.Time;
        Signals = d.Signals;
        IsDate = d.IsDate;
        ApplyOffset();
        return "";
    }

    public void SetOffset(double seconds) { Offset = seconds; ApplyOffset(); }

    /// <summary>기준 시작 시각을 현재 데이터 시작 시각에 맞춘다.</summary>
    public void AlignStart(DataSet data)
    {
        var u = IsDate ? 1000.0 : 1.0;
        Offset = (data.Start - Time0[0]) / u;
        ApplyOffset();
    }

    void ApplyOffset()
    {
        var add = Offset * (IsDate ? 1000.0 : 1.0);
        Time = Time0.Select(x => x + add).ToArray();
    }

    public double ValNear(string signal, double tm) =>
        Signals.TryGetValue(signal, out var a) ? Series.ValNear(Time, a, tm) : double.NaN;
}
