using System.Text.RegularExpressions;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Excavator;

/// <summary>링크 하나(붐·암·버켓)의 CSV 신호 연결 (HTML EX.sig.links[j])</summary>
public sealed class LinkSignals
{
    /// <summary>각도 열 (있으면 그대로 사용)</summary>
    public string Ang { get; set; } = "";
    /// <summary>"deg" 또는 "rad"</summary>
    public string AngUnit { get; set; } = "deg";
    /// <summary>각도 오프셋 [deg]</summary>
    public double Offset { get; set; }
    /// <summary>실린더 길이 열</summary>
    public string Cyl { get; set; } = "";
    /// <summary>"mm", "cm", "m"</summary>
    public string CylUnit { get; set; } = "m";
    /// <summary>실린더 속도 열 (각도·길이 열이 없을 때 적분)</summary>
    public string Vel { get; set; } = "";
    /// <summary>"mps", "mmps", "cmps"</summary>
    public string VelUnit { get; set; } = "mmps";
    /// <summary>적분 길이 오프셋 [mm]</summary>
    public double VelOffset { get; set; }
    /// <summary>속도 바이어스(Drift) [mm/s]</summary>
    public double VelDrift { get; set; }
    /// <summary>느린 Drift 제거 창 [s] (0 = 사용 안 함)</summary>
    public double VelWindow { get; set; }
    /// <summary>적분 길이를 사용자가 승인했는가</summary>
    public bool VelApproved { get; set; }

    public LinkSignals Clone() => (LinkSignals)MemberwiseClone();
}

/// <summary>굴착기 분석용 CSV 신호 연결 (HTML EX.sig + EX.dyn)</summary>
public sealed class ExcavatorSignals
{
    public static readonly string[] LinkKor = { "붐", "암", "버켓" };
    public static readonly string[] LinkEng = { "Boom", "Arm", "Bkt" };
    public static readonly string[] CylPrefix = { "Bm", "Am", "Bk" };

    /// <summary>동역학용 압력 신호 키와 설명</summary>
    public static readonly (string Key, string Label)[] PressureKeys =
    {
        ("pSwA", "선회 모터 A"), ("pSwB", "선회 모터 B"),
        ("pBmCylH", "붐 실린더 헤드"), ("pBmCylR", "붐 실린더 로드"),
        ("pAmCylH", "암 실린더 헤드"), ("pAmCylR", "암 실린더 로드"),
        ("pBkCylH", "버켓 실린더 헤드"), ("pBkCylR", "버켓 실린더 로드"),
    };
    public static readonly string[][] CylPressureKeys = { new[] { "pBmCylH", "pBmCylR" }, new[] { "pAmCylH", "pAmCylR" }, new[] { "pBkCylH", "pBkCylR" } };

    public LinkSignals[] Links { get; set; } = { new(), new(), new() };
    /// <summary>각도 열이 수평 기준 절대각인가 (false = 링크 상대각)</summary>
    public bool Absolute { get; set; }
    /// <summary>선회 각도 열과 단위("deg"/"rad", 비어 있으면 미선택)</summary>
    public string SwingAngle { get; set; } = "";
    public string SwingAngleUnit { get; set; } = "";
    /// <summary>사용자가 신호 연결을 확인했는가</summary>
    public bool Confirmed { get; set; }

    /// <summary>자동 검색 후보 (링크별 각도·실린더 열, 선회 각도 열)</summary>
    public List<string>[] AngCandidates { get; private set; } = { new(), new(), new() };
    public List<string>[] CylCandidates { get; private set; } = { new(), new(), new() };
    public List<string> SwingCandidates { get; private set; } = new();

    // ---- 동역학 설정 (EX.dyn) ----
    /// <summary>압력 키 → 열 이름</summary>
    public Dictionary<string, string> Pressure { get; set; } = new();
    /// <summary>선회 속도 열과 단위 (dps, rps, rpm)</summary>
    public string SwingSpeed { get; set; } = "";
    public string SwingSpeedUnit { get; set; } = "dps";
    /// <summary>평활 창 [s]</summary>
    public double SmoothWindow { get; set; } = .2;
    /// <summary>숫자 시간축 단위 "s" 또는 "ms"</summary>
    public string TimeUnit { get; set; } = "s";
    public bool Gravity { get; set; } = true;
    /// <summary>실린더별 Head↔Rod 교환</summary>
    public bool[] Swap { get; set; } = new bool[3];
    /// <summary>추력 부호 전체 반전</summary>
    public bool Negate { get; set; }
    /// <summary>Tip 외력을 버켓 → 환경(굴착력) 방향으로 표시</summary>
    public bool ForceOnEnvironment { get; set; }
    /// <summary>각도 출력 단위 "deg", "rad", "both"</summary>
    public string AngleOut { get; set; } = "deg";

    public string P(string key) => Pressure.TryGetValue(key, out var s) ? s : "";

    // ---- 헤더 검색 ----
    static readonly string[][] KwLink = { new[] { "BM", "BOOM", "붐" }, new[] { "AM", "ARM", "암" }, new[] { "BK", "BKT", "BUCKET", "버켓", "버킷" } };
    static readonly string[] KwCyl = { "CYLLEN", "CL", "LEN", "CYL", "CYLINDER", "STROKE" };
    static readonly string[] KwAng = { "ANG", "ANGLE", "THETA" };
    static readonly string[] AllKw = KwCyl.Concat(KwAng).Concat(KwLink.SelectMany(x => x)).Select(x => x.ToLowerInvariant()).ToArray();
    static readonly Regex Kwr = new("len|disp|ang|cyl|theta");

    /// <summary>이름이 키워드 중 하나를 낱말로 포함하는가 (HTML exHas)</summary>
    public static bool Has(string name, IEnumerable<string> kws)
    {
        string lo = name.ToLowerInvariant(); var tk = Units.Tokens(name);
        return kws.Any(k =>
        {
            string q = k.ToLowerInvariant();
            if (q.Any(ch => ch > 0x7f)) return lo.Contains(q);
            return tk.Any(t => t == q || (q.Length >= 3 && t.Contains(q))
                || (t.StartsWith(q) && (AllKw.Contains(t[q.Length..]) || Kwr.IsMatch(t[q.Length..])))
                || (t.EndsWith(q) && (AllKw.Contains(t[..^q.Length]) || Kwr.IsMatch(t[..^q.Length]))));
        });
    }
    public static bool HasLink(string name, int j) => Has(name, KwLink[j]);

    /// <summary>열의 단위: 단위 행/[ ] 우선, 없으면 이름 끝의 deg/rad/mm/cm 토큰</summary>
    public static string UnitOf(DataSet d, string n)
    {
        var u = Units.Normalize(d.Units.GetValueOrDefault(n));
        if (u != "") return u;
        var t = Units.Tokens(n);
        var l = t.Length > 0 ? t[^1] : "";
        return l is "deg" or "rad" or "mm" or "cm" ? l : "";
    }

    /// <summary>절댓값의 중앙값 (최대 약 600점 표본)</summary>
    public static double MedAbs(double[] a)
    {
        var v = new List<double>(); int st = Math.Max(1, a.Length / 600);
        for (int i = 0; i < a.Length; i += st) if (!double.IsNaN(a[i])) v.Add(Math.Abs(a[i]));
        v.Sort();
        return v.Count > 0 ? v[v.Count >> 1] : 0;
    }

    /// <summary>속도 단위 문자열 → mps/mmps/cmps (모르면 "")</summary>
    public static string VelUnitOf(string? u)
    {
        var s = Regex.Replace((u ?? "").ToLowerInvariant(), @"[\s\[\]()]", "");
        return Regex.IsMatch(s, @"^mm/s(ec)?$") ? "mmps" : Regex.IsMatch(s, @"^m/s(ec)?$") ? "mps" : Regex.IsMatch(s, @"^cm/s(ec)?$") ? "cmps" : "";
    }
    public static double VelToMps(string vu) => vu switch { "mps" => 1, "mmps" => .001, "cmps" => .01, _ => double.NaN };
    public static double LenToM(string cu) => cu == "mm" ? .001 : cu == "cm" ? .01 : 1;

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9가-힣]", "");

    /// <summary>CSV 헤더에서 링크 신호를 자동 검색 (HTML exGuess + exVelFill)</summary>
    public void Guess(DataSet d)
    {
        var nm = d.Names;
        bool CylLike(string n) => Units.PressureToPa(d.Units.GetValueOrDefault(n)) == null && !Regex.IsMatch(n, "pres|압력", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(Regex.Replace(n, "[^a-z0-9가-힣]", "", RegexOptions.IgnoreCase), "speed|vel|stkv|strkv|strokev|속도", RegexOptions.IgnoreCase)
            && (Has(n, KwCyl) || Regex.IsMatch(n, "len|disp", RegexOptions.IgnoreCase));
        int Score(string n) => (Regex.IsMatch(n, "cyl", RegexOptions.IgnoreCase) ? 2 : 0) + (Regex.IsMatch(n, "len|disp", RegexOptions.IgnoreCase) ? 1 : 0);
        for (int j = 0; j < 3; j++)
        {
            var m = nm.Where(n => Has(n, KwLink[j])).ToList();
            var an = m.Where(n => Has(n, KwAng)).ToList();
            var cy = m.Where(n => CylLike(n) && !an.Contains(n)).OrderByDescending(Score).ToList();
            AngCandidates[j] = an; CylCandidates[j] = cy;
            string a = an.FirstOrDefault() ?? "", c = cy.FirstOrDefault() ?? "", ua = UnitOf(d, a), uc = UnitOf(d, c);
            Links[j] = new LinkSignals
            {
                Ang = a,
                AngUnit = ua is "rad" or "deg" ? ua : Regex.IsMatch(a, "rad", RegexOptions.IgnoreCase) && !Regex.IsMatch(a, "deg", RegexOptions.IgnoreCase) ? "rad" : "deg",
                Cyl = c,
                CylUnit = uc is "mm" or "cm" or "m" ? uc : c != "" && d.Signals.TryGetValue(c, out var ca) ? (MedAbs(ca) > 20 ? "mm" : "m") : "m",
            };
        }
        FillVelocity(d);
        SwingCandidates = nm.Where(n => Has(n, new[] { "SW", "SWING", "SWG", "선회" }) && Has(n, KwAng.Concat(new[] { "POS", "POSITION", "DEG", "RAD" }))
            && !Regex.IsMatch(n, "spd|speed|vel|rate|rpm", RegexOptions.IgnoreCase)).ToList();
        SwingAngle = SwingCandidates.FirstOrDefault() ?? "";
        var su = UnitOf(d, SwingAngle);
        SwingAngleUnit = su is "deg" or "rad" ? su : "";
        Confirmed = false;
    }

    /// <summary>각도·길이 열이 없는 링크에 실린더 속도 열을 찾아 둔다 (HTML exVelFill)</summary>
    public void FillVelocity(DataSet d)
    {
        for (int j = 0; j < 3; j++)
        {
            var r = Links[j];
            r.Vel = ""; r.VelUnit = "mmps"; r.VelOffset = 0; r.VelDrift = 0; r.VelWindow = 0; r.VelApproved = false;
            if (r.Ang != "" || r.Cyl != "") continue;
            var lkn = d.Names.Where(n => Has(n, KwLink[j]) && !Regex.IsMatch(n, "ang|deg|rad|joy", RegexOptions.IgnoreCase)).ToList();
            var v = lkn.FirstOrDefault(n => Regex.IsMatch(n, "cyl", RegexOptions.IgnoreCase) && Regex.IsMatch(Regex.Replace(n, "[^a-z0-9]", "", RegexOptions.IgnoreCase), "speed|vel|stkv|strkv|strokev", RegexOptions.IgnoreCase))
                ?? lkn.FirstOrDefault(n => Regex.IsMatch(n, "vel|spd|speed|rate|속도", RegexOptions.IgnoreCase));
            if (v == null) continue;
            r.Vel = v;
            var u = VelUnitOf(d.Units.GetValueOrDefault(v));
            r.VelUnit = u != "" ? u : MedAbs(d.Signals[v]) > .8 ? "mmps" : "mps";
        }
    }

    /// <summary>압력·선회 속도 열 자동 선택 (HTML exDynGuess)</summary>
    public void GuessDynamics(DataSet d)
    {
        Pressure = new();
        foreach (var (k, _) in PressureKeys)
        {
            var q = Norm(k);
            Pressure[k] = d.Names.FirstOrDefault(n => Norm(n) == q) ?? d.Names.FirstOrDefault(n => Norm(n).StartsWith(q)) ?? "";
        }
        SwingSpeed = d.Names.FirstOrDefault(n => Regex.IsMatch(n, "(swing|sw).*(spd|speed|vel|rpm|omega|dps|rps)|선회.*(속도|rpm)", RegexOptions.IgnoreCase)) ?? "";
        var su = Units.Normalize(d.Units.GetValueOrDefault(SwingSpeed));
        SwingSpeedUnit = su is "dps" or "rps" or "rpm" ? su
            : Regex.IsMatch(SwingSpeed, "rpm", RegexOptions.IgnoreCase) ? "rpm" : Regex.IsMatch(SwingSpeed, "rad|rps", RegexOptions.IgnoreCase) ? "rps" : "dps";
        var tu = Units.Normalize(d.TimeUnit);
        if (tu is "s" or "ms") TimeUnit = tu;
    }

    /// <summary>링크 계산 방식 설명</summary>
    public static string LinkState(DataSet d, LinkSignals r) =>
        r.Ang != "" && d.Signals.ContainsKey(r.Ang) ? "각도 열 사용"
        : r.Cyl != "" && d.Signals.ContainsKey(r.Cyl) ? "Kinematics (실린더→각도)"
        : r.Vel != "" && d.Signals.ContainsKey(r.Vel) ? "⚠ 속도 적분 → 길이 보정·승인 필요" : "미지정";
}
