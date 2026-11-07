using System.Text.RegularExpressions;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Analysis;

/// <summary>축 이름: Boom, Arm, Bucket, Swing</summary>
public enum Axis { Boom, Arm, Bucket, Swing }

/// <summary>한 축의 조이스틱 신호 지정.</summary>
public sealed class AxisSignals
{
    /// <summary>1열 방식: 부호로 방향 구분 (+ = Extend / Left)</summary>
    public string Joy { get; set; } = "";
    /// <summary>분리 방식: Extend 열 (Swing: Left)</summary>
    public string JoyP { get; set; } = "";
    /// <summary>분리 방식: Retract 열 (Swing: Right)</summary>
    public string JoyN { get; set; } = "";
    /// <summary>Actuator 속도 열 (현재 판정에는 쓰지 않음, HTML과 동일)</summary>
    public string Vel { get; set; } = "";
    /// <summary>부호 반전</summary>
    public bool Invert { get; set; }
}

/// <summary>Action 전이 조건 [bar].</summary>
public sealed class TransitionThresholds
{
    /// <summary>Digging → Hauling: Swing ≥ T1</summary>
    public double T1 { get; set; } = 2;
    /// <summary>Hauling → Dumping: Swing ≤ T2a 이고 Arm Dump ≥ T2b</summary>
    public double T2a { get; set; } = 2;
    public double T2b { get; set; } = 2;
    /// <summary>Dumping → Returning: Swing ≥ T3a 이고 Boom Down ≥ T3b</summary>
    public double T3a { get; set; } = 2;
    public double T3b { get; set; } = 2;
    /// <summary>Returning(또는 Idling) → Digging: Swing ≤ T4a 이고 Arm Crowd ≥ T4b</summary>
    public double T4a { get; set; } = 2;
    public double T4b { get; set; } = 2;

    public double[] All => new[] { T1, T2a, T2b, T3a, T3b, T4a, T4b };
}

/// <summary>Cycle·Action 분석 설정 (HTML의 CY.cfg).</summary>
public sealed class CycleConfig
{
    /// <summary>후처리: 전이 시점을 앞으로 당기는 시간 [s]</summary>
    public double Lead { get; set; } = .2;
    /// <summary>Idling 판정: 모든 조이스틱 ≤ IdleP [bar]가 IdleT [s] 이상</summary>
    public double IdleP { get; set; } = .5;
    public double IdleT { get; set; } = .5;
    public TransitionThresholds Tr { get; set; } = new();
    /// <summary>조이스틱이 Extend/Retract 두 열로 분리되어 있는지</summary>
    public bool Split { get; set; } = true;
    public Dictionary<Axis, AxisSignals> Axes { get; set; } = Enum.GetValues<Axis>().ToDictionary(a => a, _ => new AxisSignals());
    public double Thr { get; set; } = 10;
    /// <summary>각 Action의 최소 지속 시간 [s]</summary>
    public double MinT { get; set; } = 2;
}

/// <summary>Cycle 하나의 구간 (샘플 인덱스, 없으면 −1). e = 끝 (다음 Cycle 시작 또는 n).</summary>
public sealed class CycleSegment
{
    public int S, H = -1, D = -1, R = -1, I = -1, E = -1;
    public CycleSegment Clone() => (CycleSegment)MemberwiseClone();
}

/// <summary>분석 결과 (라벨 전). 당김 후처리는 이것을 기준으로 다시 계산한다.</summary>
public sealed class CycleRun
{
    public required List<CycleSegment> Segments { get; init; }
    public required int N { get; init; }
    public required double[] TimeSec { get; init; }
    public required double Dt { get; init; }
    /// <summary>마지막 Cycle이 미완성이라 제외했는지</summary>
    public bool Incomplete { get; init; }
}

/// <summary>Cycle 표의 한 행: 시작 샘플 인덱스와 Action별 시간 [s] (없는 구간은 NaN)</summary>
public sealed record CycleRow(int Cycle, int StartIndex, double Digging, double Hauling, double Dumping, double Returning, double Idling, double Total);

public sealed class CycleLabels
{
    /// <summary>Cycle 번호 (1…N, Idling·구간 밖 = 0)</summary>
    public required double[] Cycle { get; init; }
    /// <summary>Action (1 Digging, 2 Hauling, 3 Dumping, 4 Returning, 0 Idling·구간 밖)</summary>
    public required double[] Action { get; init; }
    public required List<CycleRow> Rows { get; init; }
    /// <summary>실제 적용한 당김 [s]</summary>
    public double Shift { get; init; }
    /// <summary>Arm Dump 시작을 못 찾아 Dumping이 빈 Cycle 수</summary>
    public int NoDump { get; init; }
}

/// <summary>
/// 굴착기 Cycle·Action 자동 라벨링 (HTML 뷰어 ⑤ 'Cycle 및 Action 분석'과 같은 규칙).
/// 조이스틱 압력 [bar]으로 Digging → Hauling → Dumping → Returning (→ Idling) 상태 기계를 돌린다.
/// </summary>
public static class CycleAnalysis
{
    public static readonly string[] ActionNames = { "Digging", "Hauling", "Dumping", "Returning" };

    /// <summary>Action 코드 이름 (0 = Idling). CSV에 Action 문자열이 있었으면 그 이름.</summary>
    public static string ActionLabel(int v, IReadOnlyDictionary<int, string>? csvLabels = null)
    {
        if (v == 0) return "Idling";
        if (csvLabels != null && csvLabels.TryGetValue(v, out var s)) return s;
        return v >= 1 && v <= 4 ? ActionNames[v - 1] : "Action " + v;
    }

    /// <summary>
    /// 축의 방향별 크기 [bar] 시계열 (pos = Extend/Left, neg = Retract/Right). 여러 열이면 최대값.
    /// 단위가 압력(MPa 등)이면 bar로 환산. 지정된 열이 없으면 null.
    /// </summary>
    public static (float[] Pos, float[] Neg)? Magnitudes(AxisSignals a, DataSet d, bool split)
    {
        int n = d.Count;
        var pos = new float[n]; var neg = new float[n];
        int sg = a.Invert ? -1 : 1;
        var srcs = new List<(double[] Arr, bool Abs, int Dir, double F)>();
        double Bf(string nm) => Units.PressureToPa(d.Units.GetValueOrDefault(nm)) is double f ? f / 1e5 : 1;
        if (split && (a.JoyP.Length > 0 || a.JoyN.Length > 0))
        {
            foreach (var (nm, dir) in new[] { (a.JoyP, 1), (a.JoyN, -1) })
                if (nm.Length > 0 && d.Signals.TryGetValue(nm, out var arr)) srcs.Add((arr, true, dir, Bf(nm)));
        }
        else if (a.Joy.Length > 0 && d.Signals.TryGetValue(a.Joy, out var arr)) srcs.Add((arr, false, 0, Bf(a.Joy)));
        foreach (var s in srcs)
        {
            for (int i = 0; i < n; i++)
            {
                double x = s.Arr[i];
                if (double.IsNaN(x)) continue;
                float m = (float)(Math.Abs(x) * s.F);
                if (s.Abs)
                {
                    if (s.Dir * sg > 0) pos[i] = Math.Max(pos[i], m); else neg[i] = Math.Max(neg[i], m);
                }
                else
                {
                    double y = x * sg;
                    if (y > 0) pos[i] = Math.Max(pos[i], m);
                    else if (y < 0) neg[i] = Math.Max(neg[i], m);
                }
            }
        }
        return srcs.Count > 0 ? (pos, neg) : null;
    }

    /// <summary>
    /// 0/1 신호 정리: 앞뒤가 1인 gap 샘플 미만의 0 구간은 1로 메우고, 그 뒤 run 샘플 미만의 1 구간은 0으로 지운다.
    /// </summary>
    public static byte[] Clean(byte[] b, int gap, int run)
    {
        int n = b.Length, i = 0;
        while (i < n)
        {
            if (b[i] != 0) { i++; continue; }
            int j = i;
            while (j < n && b[j] == 0) j++;
            if (i > 0 && j < n && j - i < gap) for (int k = i; k < j; k++) b[k] = 1;
            i = j;
        }
        i = 0;
        while (i < n)
        {
            if (b[i] == 0) { i++; continue; }
            int j = i;
            while (j < n && b[j] != 0) j++;
            if (j - i < run) for (int k = i; k < j; k++) b[k] = 0;
            i = j;
        }
        return b;
    }

    /// <summary>샘플 간격 중앙값 [s] (최대 약 300개 표본, HTML과 동일)</summary>
    public static double MedianDt(double[] ts)
    {
        int n = ts.Length, step = Math.Max(1, n / 300);
        var dd = new List<double>();
        for (int i = 1; i < n; i += step) dd.Add(ts[i] - ts[i - 1]);
        dd.Sort();
        var dt = dd.Count > 0 ? dd[dd.Count >> 1] : 0;
        return dt != 0 ? dt : .01;
    }

    /// <summary>상태 기계로 Cycle 구간을 찾는다. 실패하면 error에 이유.</summary>
    public static CycleRun? Run(DataSet d, double[] timeSec, CycleConfig cfg, out string error)
    {
        error = "";
        int n = d.Count;
        var ts = timeSec;
        double dt = MedianDt(ts);
        int gap = Math.Max(1, (int)Math.Ceiling(.2 / dt)), run = Math.Max(1, (int)Math.Ceiling(.1 / dt));
        var T = cfg.Tr;
        var pc = Enum.GetValues<Axis>().ToDictionary(k => k, k => Magnitudes(cfg.Axes[k], d, cfg.Split));
        if (pc[Axis.Swing] == null) { error = "Swing의 조이스틱 열을 지정하세요 (조건은 Joystick 신호 [bar]로 판정합니다)."; return null; }
        if (pc[Axis.Arm] == null) { error = "Arm의 조이스틱 열을 지정하세요 (조건은 Joystick 신호 [bar]로 판정합니다)."; return null; }
        if (!T.All.All(x => double.IsFinite(x) && x >= 0)) { error = "Action 전이 조건의 [bar] 값은 0 이상의 숫자여야 합니다."; return null; }

        var z = new float[n];
        var bm = pc[Axis.Boom] ?? (z, z);
        var am = pc[Axis.Arm]!.Value;
        var sw = pc[Axis.Swing]!.Value;
        var sm = new float[n];
        for (int i = 0; i < n; i++) sm[i] = Math.Max(sw.Pos[i], sw.Neg[i]);
        bool noB = pc[Axis.Boom] == null;
        byte[] Mk(Func<int, bool> f) { var b = new byte[n]; for (int i = 0; i < n; i++) b[i] = f(i) ? (byte)1 : (byte)0; return Clean(b, gap, run); }
        var c1 = Mk(i => sm[i] >= T.T1);
        var c2 = Mk(i => sm[i] <= T.T2a && am.Neg[i] >= T.T2b);
        var c3 = Mk(i => sm[i] >= T.T3a && (noB || bm.Neg[i] >= T.T3b));
        var c4 = Mk(i => sm[i] <= T.T4a && am.Pos[i] >= T.T4b);

        var jq = pc.Values.Where(v => v != null).Select(v => v!.Value).ToList();
        byte[]? idl = null;
        if (jq.Count > 0 && cfg.IdleP >= 0 && cfg.IdleT > 0)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                byte q = 1;
                foreach (var x in jq) if (x.Pos[i] > cfg.IdleP || x.Neg[i] > cfg.IdleP) { q = 0; break; }
                b[i] = q;
            }
            idl = Clean(b, 0, Math.Max(1, (int)Math.Ceiling(cfg.IdleT / dt)));
        }

        var segs = new List<CycleSegment>();
        string st = "seek";
        CycleSegment? c = null;
        int ent = 0;
        void Open(int i) { c = new CycleSegment { S = i }; st = "dig"; ent = i; }
        bool OkT(int i) => ts[i] - ts[ent] >= cfg.MinT - 1e-9;
        for (int i = 0; i < n; i++)
        {
            switch (st)
            {
                case "seek":
                    if (c4[i] != 0) Open(i);
                    break;
                case "dig":
                    if (c1[i] != 0 && OkT(i)) { c!.H = i; st = "haul"; ent = i; }
                    break;
                case "haul":
                    if (c2[i] != 0 && OkT(i)) { c!.D = i; st = "dump"; ent = i; }
                    break;
                case "dump":
                    if (c3[i] != 0 && OkT(i)) { c!.R = i; st = "ret"; ent = i; }
                    break;
                case "idle":
                    if (c4[i] != 0 && OkT(i)) { c!.E = i; segs.Add(c); Open(i); }
                    break;
                case "ret":
                    if (idl != null && idl[i] != 0 && OkT(i)) { c!.I = i; st = "idle"; ent = i; }
                    else if (c4[i] != 0 && OkT(i)) { c!.E = i; segs.Add(c); Open(i); }
                    break;
            }
        }
        bool inc = false;
        if (c != null && st != "seek")
        {
            if (st is "ret" or "idle") { c.E = n; segs.Add(c); }
            else inc = true;
        }
        if (segs.Count == 0)
        {
            error = "완성된 Cycle을 찾지 못했습니다. 신호 열·부호 반전·임계값을 확인하세요." + (inc ? $" (Digging→Hauling 단계까지만 진행: {st})" : "");
            return null;
        }
        return new CycleRun { Segments = segs.Select(q => q.Clone()).ToList(), N = n, TimeSec = ts, Dt = dt, Incomplete = inc };
    }

    /// <summary>
    /// Cycle·Action 라벨과 Cycle 표를 만든다. shiftSec만큼 모든 전이 시점을 앞으로(음수면 뒤로) 옮긴다
    /// (분석 직후 결과 기준의 절대량).
    /// </summary>
    public static CycleLabels Label(CycleRun run, double shiftSec = 0)
    {
        int n = run.N;
        var ts = run.TimeSec;
        var segs = run.Segments.Select(q => q.Clone()).ToList();
        var cyc = new double[n]; var act = new double[n];
        int k0 = (int)Math.Floor(shiftSec / run.Dt + .5); // JavaScript Math.round
        if (k0 != 0)
        {
            int prev = 0;
            foreach (var q in segs)
            {
                int Shift(int v) { var r = Math.Min(n, Math.Max(prev, v - k0)); prev = r; return r; }
                if (q.S >= 0) q.S = Shift(q.S);
                if (q.H >= 0) q.H = Shift(q.H);
                if (q.D >= 0) q.D = Shift(q.D);
                if (q.R >= 0) q.R = Shift(q.R);
                if (q.I >= 0) q.I = Shift(q.I);
                if (q.E >= 0 && q.E < n) q.E = Shift(q.E);
            }
        }
        for (int k = 0; k < segs.Count; k++)
        {
            var q = segs[k];
            int h = q.H >= 0 ? q.H : q.E, d = q.D, rr = q.R >= 0 ? q.R : q.E;
            for (int i = q.S; i < q.E; i++)
            {
                bool idle = q.I >= 0 && i >= q.I;
                cyc[i] = idle ? 0 : k + 1;
                act[i] = idle ? 0 : i < h ? 1 : (d >= 0 && i >= d && i < rr) ? 3 : i < rr ? 2 : 4;
            }
        }
        double Tm(int a, int b) => Math.Max(0, ts[Math.Min(b, n - 1)] - ts[Math.Min(a, n - 1)]);
        var rows = segs.Select((q, k) =>
        {
            int h = q.H >= 0 ? q.H : q.E, rr = q.R >= 0 ? q.R : q.E, d = q.D >= 0 ? q.D : rr, end = Math.Min(q.E, n);
            int idleStart = q.I >= 0 ? q.I : end;
            return new CycleRow(k + 1, Math.Min(q.S, n - 1), Tm(q.S, h), Tm(h, d), q.D >= 0 ? Tm(d, rr) : double.NaN,
                Tm(rr, idleStart), q.I >= 0 ? Tm(q.I, end) : double.NaN, Tm(q.S, idleStart));
        }).ToList();
        return new CycleLabels
        {
            Cycle = cyc, Action = act, Rows = rows, Shift = k0 * run.Dt,
            NoDump = segs.Count(q => q.D < 0),
        };
    }

    /// <summary>평균 ± 표준편차(모집단) 문자열. 값이 없으면 '—'.</summary>
    public static string MeanSd(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).ToList();
        if (v.Count == 0) return "—";
        double m = v.Average(), s = Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / v.Count);
        return $"{m:0.00} ± {s:0.00}";
    }

    static readonly (Axis Axis, string[] Keywords)[] AxisKeywords =
    {
        (Axis.Boom, new[] { "BM", "BOOM", "붐" }),
        (Axis.Arm, new[] { "AM", "ARM", "암" }),
        (Axis.Bucket, new[] { "BK", "BKT", "BUCKET", "버켓", "버킷" }),
        (Axis.Swing, new[] { "SW", "SWING", "SWG", "선회" }),
    };
    static readonly Dictionary<Axis, (string[] Ext, string[] Ret)> DirWords = new()
    {
        [Axis.Boom] = (new[] { "up", "ext", "extend" }, new[] { "dn", "down", "ret", "retract" }),
        [Axis.Arm] = (new[] { "cd", "cr", "crowd", "ext", "extend" }, new[] { "dp", "dump", "ret", "retract" }),
        [Axis.Bucket] = (new[] { "cd", "cr", "crowd", "ext", "extend" }, new[] { "dp", "dump", "ret", "retract" }),
        [Axis.Swing] = (new[] { "left", "lt", "l" }, new[] { "right", "rt", "r" }),
    };

    /// <summary>신호 이름으로 축별 조이스틱·속도 열을 추측한다 (HTML의 cyGuess). Split은 true로 둔다.</summary>
    public static void GuessAxes(CycleConfig c, IReadOnlyList<string> names)
    {
        c.Split = true;
        static string Cp(string n) => Regex.Replace(n.ToLowerInvariant(), "[^a-z0-9가-힣]", "");
        string? Pick(List<string> l, string[] w) =>
            l.FirstOrDefault(n => w.Any(x => Units.Tokens(n).Contains(x))) ?? l.FirstOrDefault(n => w.Any(x => Cp(n).EndsWith(x)));
        foreach (var (k, kw) in AxisKeywords)
        {
            var a = c.Axes[k] = new AxisSignals();
            bool Hit(string n)
            {
                var tk = Units.Tokens(n);
                return kw.Any(q0 =>
                {
                    var q = q0.ToLowerInvariant();
                    return tk.Contains(q) || (q.Length >= 3 && tk.Any(t => t.Contains(q))) || (Regex.IsMatch(q, "[가-힣]") && n.Contains(q));
                });
            }
            var cand = names.Where(n => Hit(n) && !Regex.IsMatch(n, "^(Cycle|Action)$", RegexOptions.IgnoreCase)).ToList();
            var jl = cand.Where(n => Regex.IsMatch(n, "joy", RegexOptions.IgnoreCase)).ToList();
            if (jl.Count == 0) jl = cand.Where(n => Regex.IsMatch(n, "stick|lever|pilot|조이스틱|레버", RegexOptions.IgnoreCase)).ToList();
            var pi = jl.Where(n => Units.Tokens(n).Contains("pi")).ToList();
            var pj = pi.Count > 0 ? pi : jl.Where(n => Regex.IsMatch(n, "pi", RegexOptions.IgnoreCase)).ToList();
            var jp = pj.Count > 0 ? pj : jl;
            a.JoyP = Pick(jp, DirWords[k].Ext) ?? "";
            a.JoyN = Pick(jp, DirWords[k].Ret) ?? "";
            if (a.JoyN == a.JoyP) a.JoyN = "";
            a.Joy = jp.FirstOrDefault(n => n != a.JoyP && n != a.JoyN) ?? jp.FirstOrDefault() ?? "";
            static bool NotJ(string n) => !Regex.IsMatch(n, "joy", RegexOptions.IgnoreCase);
            var V = new Regex("speed|vel|stkv|strkv|strokev|속도");
            a.Vel = (k != Axis.Swing ? cand.FirstOrDefault(n => NotJ(n) && Regex.IsMatch(n, "cyl", RegexOptions.IgnoreCase) && V.IsMatch(Cp(n))) : null)
                ?? cand.FirstOrDefault(n => NotJ(n) && V.IsMatch(Cp(n)))
                ?? cand.FirstOrDefault(n => NotJ(n) && Regex.IsMatch(Cp(n), "vel|spd|rpm"))
                ?? "";
        }
    }
}
