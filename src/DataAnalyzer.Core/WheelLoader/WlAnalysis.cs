using System.Text.RegularExpressions;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.WheelLoader;

/// <summary>휠로더 분석용 CSV 신호 연결 (HTML WL.sig)</summary>
public sealed class WlSignals
{
    /// <summary>Lift Arm 각도 열과 단위</summary>
    public string AA { get; set; } = "";
    public string AAu { get; set; } = "deg";
    /// <summary>Tilt Lever 각도 열과 단위</summary>
    public string BA { get; set; } = "";
    public string BAu { get; set; } = "deg";
    /// <summary>Tilt Lever 각도 오프셋 (BAu 단위)</summary>
    public double BOff { get; set; }
    /// <summary>"calc" = Tilt Lever 각도에서 θ4 계산, "col" = θ4 열 직접 사용</summary>
    public string T4Mode { get; set; } = "calc";
    public string T4Src { get; set; } = "";
    public string T4u { get; set; } = "deg";
    /// <summary>Arm·Bucket 실린더 Head/Rod 압력 열과 단위</summary>
    public string AH { get; set; } = "";
    public string AR { get; set; } = "";
    public string APu { get; set; } = "bar";
    public string BH { get; set; } = "";
    public string BR { get; set; } = "";
    public string BPu { get; set; } = "bar";
    /// <summary>각도 출력 단위 deg / rad / both</summary>
    public string AngleOut { get; set; } = "deg";
    /// <summary>평활 창 [s]</summary>
    public double SmoothWindow { get; set; } = .2;
    /// <summary>숫자 시간축 단위 s / ms</summary>
    public string TimeUnit { get; set; } = "s";

    /// <summary>헤더로 열 자동 검색 (HTML wlGuess). 기존 Offset·θ4 모드는 유지.</summary>
    public static WlSignals Guess(DataSet d, WlSignals? old = null)
    {
        var nm = d.Names.Where(n => !n.StartsWith("WL_")).ToList();
        string U(string n) => d.Units.GetValueOrDefault(n) ?? "";
        string Tj(string n) => " " + string.Join(" ", Units.Tokens(n)) + " ";
        var A = new Regex(@"\s(am|arm|lift|boom|bm|리프트|암|붐)\s"); var B = new Regex(@"\s(bk|bkt|bucket|tilt|버켓|버킷|틸트)\s");
        string Find(Regex re, Func<string, string, bool> f) => nm.FirstOrDefault(n => re.IsMatch(Tj(n)) && f(n, Tj(n))) ?? "";
        string Ang(Regex re) => Find(re, (n, t) => Regex.IsMatch(t, @"\s(ang|angle|theta|deg|rad|각도)\s") || Regex.IsMatch(n, "ang|theta|각", RegexOptions.IgnoreCase));
        string Pr(Regex re, bool hd) => Find(re, (n, t) => (Regex.IsMatch(t, @"\s(p|pres|press|pressure|압력)\s") || Units.PressureToPa(U(n)) != null)
            && (hd ? Regex.IsMatch(t, @"\s(h|head|헤드)\s") : Regex.IsMatch(t, @"\s(r|rod|로드)\s")));
        string Au(string n) { var u = Units.Normalize(U(n)); if (u is "deg" or "rad") return u; return Units.Tokens(n).Contains("rad") ? "rad" : "deg"; }
        string Pu(string n) => Units.PressureToPa(U(n)) != null ? Units.Normalize(U(n)) : "bar";
        string aA = Ang(A), bA = Ang(B), aH = Pr(A, true);
        var s = old ?? new WlSignals();
        s.AA = aA; s.AAu = Au(aA); s.BA = bA; s.BAu = Au(bA); s.AH = aH; s.AR = Pr(A, false); s.APu = Pu(aH);
        var bH = Pr(B, true); s.BH = bH; s.BR = Pr(B, false); s.BPu = Pu(bH);
        return s;
    }
}

/// <summary>실린더 압력·추력 (Pa, N)</summary>
public sealed class WlCyl
{
    public required double[] F { get; init; }
    public required double[] HP { get; init; }
    public required double[] RP { get; init; }
    public double Dt { get; init; }
    public double Dr { get; init; }
    public int N { get; init; }
    /// <summary>Bucket(총 마찰력 식)이면 true</summary>
    public bool Tot { get; init; }
}

/// <summary>각도·실린더 기본 시계열 (HTML wlBase)</summary>
public sealed class WlBase
{
    public required KinParams KK { get; init; }
    public required WlGeom G { get; init; }
    public int N { get; init; }
    public required double[] T2 { get; init; }
    public required double[] T3 { get; init; }
    public required double[] Tl { get; init; }
    public required double[] La { get; init; }
    public required double[] Lb { get; init; }
    public required double[] Tx { get; init; }
    public required double[] Ty { get; init; }
    /// <summary>조립 불가 샘플 수</summary>
    public int Bad { get; init; }
    public WlCyl? CA { get; init; }
    public WlCyl? CB { get; init; }
    public double[]? FA => CA?.F;
    public double[]? FB => CB?.F;
    /// <summary>실린더 속도 [m/s]</summary>
    public required double[] VA { get; init; }
    public required double[] VB { get; init; }
}

/// <summary>평활 각도와 각속도·각가속도 (HTML wlKin)</summary>
public sealed record WlKin(double[] Ts, int N, double[] Th3, double[] Th4, double[] W3, double[] Al3, double[] W4, double[] Al4);

/// <summary>휠로더 각도·토크 시계열 계산 (HTML wlBase / wlKin / wlTqAll / wlAngTqOn)</summary>
public static class WlAnalysis
{
    const double Rad = Kinematics.Rad;
    static readonly Dictionary<string, double> ErrFs = new() { ["aH"] = 400, ["aR"] = 400, ["bH"] = 400, ["bR"] = 400, ["aA"] = 100, ["bA"] = 100, ["t4"] = 180 };
    public static readonly string[] TqKeys = { "Tq3_HF3", "Tq3_HF4", "Tq4_HF4", "Tq3_CL3", "Tq3_CL4", "Tq3_PL", "Tq3_TL", "Tq4_CL4", "Tq4_PL", "Tq4_TL", "Tq4_PinPP", "Tq4_PinPQ", "Tq4_PinP41", "Tq4_Links", "Tq3_Pins", "Tq3_Eq", "Tq4_Eq" };
    static readonly string[] ArmCylKeys = { "ArmCylTube", "ArmCylRod" };
    static readonly string[] BktCylKeys = { "BktCylTube", "BktCylRod" };

    static double[] Fill(int n) { var a = new double[n]; Array.Fill(a, double.NaN); return a; }

    /// <summary>센서 오차 주입 (검증용): 값' = 값·(1+기울기) + Offset. 기울기·Offset ±5 % (Offset은 FS 대비)</summary>
    public static (double G, double O) ErrOf(KinParams k, string id)
    {
        if (k.Str("ErrOn") != "on") return (0, 0);
        static double Cl(double x) => Math.Max(-5, Math.Min(5, double.IsFinite(x) ? x : 0));
        double fs = k["EFS_" + id];
        return (Cl(k["Eg_" + id]) / 100, Cl(k["Eo_" + id]) / 100 * (fs > 0 ? fs : ErrFs[id]));
    }

    public static double[] TimeSec(DataSet d, WlSignals s) => d.TimeSeconds(s.TimeUnit == "ms");

    /// <summary>θ4를 열에서 직접 쓰는 경우의 열 (없으면 null)</summary>
    public static double[]? OverrideCol(DataSet d, WlSignals s) =>
        s.T4Mode == "col" && s.T4Src != "" && d.Signals.TryGetValue(s.T4Src, out var a) ? a : null;

    /// <summary>각도·실린더 길이·Tip·추력 기본 시계열. 실패하면 Error.</summary>
    public static (WlBase? B, string? Error) Base(DataSet d, KinParams k0, WlSignals s)
    {
        var KK = k0.Clone(); var g = WlGeom.Build(KK);
        if (g.Error != null) return (null, g.Error);
        int n = d.Count;
        double[]? Col(string k) => k != "" && d.Signals.TryGetValue(k, out var a) ? a : null;
        double[]? aC = Col(s.AA), bC = Col(s.BA), oc = OverrideCol(d, s);
        var eA = ErrOf(KK, "aA"); var eT = ErrOf(KK, "bA"); var e4 = ErrOf(KK, "t4");
        if (aC == null || (bC == null && oc == null)) return (null, oc != null ? "Lift Arm 각도 열을 지정하세요." : "Lift Arm 각도 열과 Tilt Lever 각도 열을 지정하세요.");
        var ts = TimeSec(d, s); int N = ExcavatorAnalysis.WinN(ts, s.SmoothWindow);
        double ka = s.AAu == "rad" ? 1 : Rad, kb = s.BAu == "rad" ? 1 : Rad;
        double[] t2 = Fill(n), t3 = Fill(n), tl = Fill(n), La = Fill(n), Lb = Fill(n), tx = Fill(n), ty = Fill(n);
        int nb = 0, sj = 0;
        void Clear(int i) { t2[i] = t3[i] = tl[i] = La[i] = Lb[i] = tx[i] = ty[i] = double.NaN; }
        void Set(int i, double a, WlPose p) { t2[i] = a; t3[i] = p.T3; tl[i] = p.Tl; La[i] = p.La; Lb[i] = p.Lb; tx[i] = p.Tip.X; ty[i] = p.Tip.Y; }
        if (bC != null)
        {
            for (int i = 0; i < n; i++)
            {
                double a = aC[i] * ka * (1 + eA.G) + eA.O * Rad, al = bC[i] * kb * (1 + eT.G) + eT.O * Rad + s.BOff * kb;
                if (double.IsNaN(a) || double.IsNaN(al)) continue;
                var p = g.PoseTL(a, al);
                if (p.Bad) { nb++; continue; }
                if (sj == 0) sj = WlGeom.SJOf(p);
                Set(i, a, p);
            }
        }
        if (sj != 0)
        {
            g.SJ = sj;
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(t2[i])) continue;
                var p = g.Pose(t2[i], t3[i]);
                if (p.Bad || Math.Abs(p.Lb - Lb[i]) > 1e-6 * Math.Max(1, Lb[i])) { Clear(i); nb++; }
            }
        }
        if (oc != null)
        {
            double ku = s.T4u == "rad" ? 1 : Rad; nb = 0;
            for (int i = 0; i < n; i++)
            {
                double a = aC[i] * ka * (1 + eA.G) + eA.O * Rad, b = oc[i] * ku * (1 + e4.G) + e4.O * Rad;
                if (double.IsNaN(a) || double.IsNaN(b)) { Clear(i); continue; }
                var p = g.Pose(a, b);
                if (p.Bad) { nb++; Clear(i); continue; }
                t2[i] = a; t3[i] = b; tl[i] = p.Tl; La[i] = p.La; Lb[i] = p.Lb; tx[i] = p.Tip.X; ty[i] = p.Tip.Y;
            }
        }
        double an = KK["AmN"], bn = KK["BkN"];
        int Rnd(double x) => (int)Math.Floor(x + .5);
        int NA = Math.Max(1, double.IsNaN(an) || Rnd(an) == 0 ? 2 : Rnd(an)), NB = Math.Max(1, double.IsNaN(bn) || Rnd(bn) == 0 ? 1 : Rnd(bn));
        WlCyl? Cyl(string hc, string rc, string pu, string p, int nn, string eh, string er, bool tot)
        {
            double[]? h = Col(hc), r = Col(rc); double dt = KK[p + "Dt"] * g.U, dr = KK[p + "Dr"] * g.U;
            if (h == null || r == null || !(dt > 0) || !(dr >= 0)) return null;
            double pf = Units.PressureToPa(pu) ?? 1e5, AH = Math.PI / 4 * dt * dt, AR = Math.PI / 4 * (dt * dt - dr * dr);
            var E1 = ErrOf(KK, eh); var E2 = ErrOf(KK, er);
            var hP = h.Select(x => x * pf * (1 + E1.G) + E1.O * 1e5).ToArray(); var rP = r.Select(x => x * pf * (1 + E2.G) + E2.O * 1e5).ToArray();
            var F = new double[n]; for (int i = 0; i < n; i++) F[i] = (hP[i] * AH - rP[i] * AR) * nn;
            return new WlCyl { F = F, HP = hP, RP = rP, Dt = dt, Dr = dr, N = nn, Tot = tot };
        }
        var cA = Cyl(s.AH, s.AR, s.APu, "Am", NA, "aH", "aR", false); var cB = Cyl(s.BH, s.BR, s.BPu, "Bk", NB, "bH", "bR", true);
        return (new WlBase
        {
            KK = KK, G = g, N = n, T2 = t2, T3 = t3, Tl = tl, La = La, Lb = Lb, Tx = tx, Ty = ty, Bad = nb, CA = cA, CB = cB,
            VA = Dynamics.Ddt(Dynamics.SmoothMA(La, N), ts), VB = Dynamics.Ddt(Dynamics.SmoothMA(Lb, N), ts),
        }, null);
    }

    public static WlKin Kin(DataSet d, WlSignals s, WlBase b)
    {
        var ts = TimeSec(d, s); int N = ExcavatorAnalysis.WinN(ts, s.SmoothWindow);
        var th3 = Dynamics.SmoothMA(b.T2, N); var th4 = Dynamics.SmoothMA(b.T3, N);
        var w3 = Dynamics.Ddt(th3, ts); var w4 = Dynamics.Ddt(th4, ts);
        return new WlKin(ts, N, th3, th4, w3, Dynamics.Ddt(w3, ts), w4, Dynamics.Ddt(w4, ts));
    }

    static double GSum(Dictionary<string, (double T3, double T4)> q, IEnumerable<string> keys, int idx)
    {
        double s = 0; bool any = false;
        foreach (var k in keys) if (q.TryGetValue(k, out var v)) { s += idx == 0 ? v.T3 : v.T4; any = true; }
        return any ? s : double.NaN;
    }

    /// <summary>실린더 추력·부품 중력이 Lift Arm(θ3)·Bucket(θ4)에 가하는 등가 토크 [kN·m], 핀 반력 [kN]</summary>
    public static Dictionary<string, double[]> TqAll(WlBase b)
    {
        var g = b.G; int n = b.N; var Kn = WlGeom.NoBody(b.KK); var R = new Dictionary<string, double[]> { ["HF3"] = Fill(n), ["HF4"] = Fill(n) };
        foreach (var k in TqKeys) R[k] = Fill(n);
        foreach (var k in WlPinForces.Keys) R["F" + k] = Fill(n);
        static double Nz(double x) => double.IsNaN(x) ? 0 : x;
        var FA = b.FA; var FB = b.FB;
        for (int i = 0; i < n; i++)
        {
            double a = b.T2[i], t = b.T3[i];
            if (double.IsNaN(a) || double.IsNaN(t)) continue;
            var dl = g.DL(a, t); bool hA = FA != null && !double.IsNaN(FA[i]), hB = FB != null && !double.IsNaN(FB[i]);
            if (hA) { R["HF3"][i] = FA![i] / 1e3; R["Tq3_HF3"][i] = FA[i] * dl.DLa / 1e3; }
            if (hB) { R["HF4"][i] = FB![i] / 1e3; R["Tq3_HF4"][i] = FB[i] * dl.DLb2 / 1e3; R["Tq4_HF4"][i] = FB[i] * dl.DLb3 / 1e3; }
            var q = g.GravTq(Kn, a, t);
            R["Tq3_CL3"][i] = GSum(q, ArmCylKeys, 0) / 1e3; R["Tq3_CL4"][i] = GSum(q, BktCylKeys, 0) / 1e3;
            R["Tq3_PL"][i] = GSum(q, new[] { "PushLink" }, 0) / 1e3; R["Tq3_TL"][i] = GSum(q, new[] { "TiltLever" }, 0) / 1e3;
            R["Tq4_CL4"][i] = GSum(q, BktCylKeys, 1) / 1e3; R["Tq4_PL"][i] = GSum(q, new[] { "PushLink" }, 1) / 1e3; R["Tq4_TL"][i] = GSum(q, new[] { "TiltLever" }, 1) / 1e3;
            R["Tq4_PinPP"][i] = GSum(q, new[] { "PinPP" }, 1) / 1e3; R["Tq4_PinPQ"][i] = GSum(q, new[] { "PinPQ" }, 1) / 1e3; R["Tq4_PinP41"][i] = GSum(q, new[] { "PinP41" }, 1) / 1e3;
            R["Tq3_Pins"][i] = GSum(q, new[] { "PinPP", "PinPQ", "PinP41" }, 0) / 1e3;
            R["Tq4_Links"][i] = Nz(R["Tq4_CL4"][i]) + Nz(R["Tq4_PL"][i]) + Nz(R["Tq4_TL"][i]) + Nz(R["Tq4_PinPP"][i]) + Nz(R["Tq4_PinPQ"][i]) + Nz(R["Tq4_PinP41"][i]);
            if (hB)
            {
                var ps = g.Pose(a, t);
                if (!ps.Bad && g.PinForces(Kn, ps, FB![i]) is { } fp)
                    foreach (var k in WlPinForces.Keys) { var f = fp[k]; R["F" + k][i] = Math.Sqrt(f.X * f.X + f.Y * f.Y) / 1e3; }
            }
            if (hA && hB) R["Tq3_Eq"][i] = R["Tq3_HF3"][i] + R["Tq3_HF4"][i] + Nz(R["Tq3_CL3"][i]) + Nz(R["Tq3_CL4"][i]) + Nz(R["Tq3_PL"][i]) + Nz(R["Tq3_TL"][i]) + Nz(R["Tq3_Pins"][i]);
            if (hB) R["Tq4_Eq"][i] = R["Tq4_HF4"][i] + Nz(R["Tq4_CL4"][i]) + Nz(R["Tq4_PL"][i]) + Nz(R["Tq4_TL"][i]) + Nz(R["Tq4_PinPP"][i]) + Nz(R["Tq4_PinPQ"][i]) + Nz(R["Tq4_PinP41"][i]);
        }
        return R;
    }

    /// <summary>
    /// d. 각도(mode "ang") / 토크(mode "tq") 계산. 결과 신호와 경고, 차트 묶음, 메시지.
    /// </summary>
    public static CalcResult AngTorque(DataSet d, KinParams k, WlSignals s, string mode)
    {
        var res = new CalcResult();
        var (b, err) = Base(d, k, s);
        if (b == null) { res.Message = err!; res.Warn = true; return res; }
        var warn = new List<string>(); int n = b.N;
        var units = s.AngleOut == "both" ? new[] { "deg", "rad" } : new[] { s.AngleOut == "rad" ? "rad" : "deg" };
        double[] Cvt(double[] a, string u) => u == "deg" ? a.Select(x => x / Rad).ToArray() : a;
        void Put(string key, double[] a, string u) => res.Put(key, a, u);
        if (mode != "tq")
        {
            var abs = new double[n]; for (int i = 0; i < n; i++) abs[i] = b.T2[i] + b.T3[i];
            foreach (var u in units)
            {
                Put("WL_LiftArm_Ang_" + u, Cvt(b.T2, u), u); Put("WL_TiltLever_AngRel_" + u, Cvt(b.Tl, u), u);
                Put("WL_Bkt_AngRel_" + u, Cvt(b.T3, u), u); Put("WL_Bkt_AngAbs_" + u, Cvt(abs, u), u);
            }
            Put("WL_ArmCyl_Len_m", b.La, "m"); Put("WL_BktCyl_Len_m", b.Lb, "m"); Put("WL_Tip_X_m", b.Tx, "m"); Put("WL_Tip_Y_m", b.Ty, "m");
        }
        if (mode != "ang")
        {
            var R = TqAll(b);
            Put("WL_HF3_kN", R["HF3"], "kN"); Put("WL_HF4_kN", R["HF4"], "kN");
            foreach (var key in TqKeys) Put("WL_" + key + "_kNm", R[key], "kNm");
            foreach (var key in WlPinForces.Keys) Put("WL_Fpin_" + key + "_kN", R["F" + key], "kN");
            var kn = Kin(d, s, b);
            double[] W34 = new double[n], A34 = new double[n], T34 = new double[n];
            for (int i = 0; i < n; i++) { W34[i] = kn.W3[i] + kn.W4[i]; A34[i] = kn.Al3[i] + kn.Al4[i]; T34[i] = b.T2[i] + b.T3[i]; }
            Put("WL_CL3_m", b.La, "m"); Put("WL_CL4_m", b.Lb, "m"); Put("WL_CV3_mps", b.VA, "m/s"); Put("WL_CV4_mps", b.VB, "m/s");
            Put("WL_W3_radps", kn.W3, "rad/s"); Put("WL_W4_radps", kn.W4, "rad/s"); Put("WL_A3_radps2", kn.Al3, "rad/s²"); Put("WL_A4_radps2", kn.Al4, "rad/s²");
            Put("WL_W34_radps", W34, "rad/s"); Put("WL_A34_radps2", A34, "rad/s²");
            foreach (var u in units) Put("WL_T34_" + u, Cvt(T34, u), u);
            if (b.FA == null) warn.Add("Arm 실린더 추력 없음(압력 열·직경 확인) → HF3, Tq3_HF3, Tq3_Eq 생략");
            if (b.FB == null) warn.Add("Bucket 실린더 추력 없음 → HF4, Tq3_HF4, Tq4_HF4, Tq3_Eq, Tq4_Eq 생략");
            foreach (var (key, nm) in new[] { ("Tq3_CL3", "Arm 실린더"), ("Tq3_CL4", "Bucket 실린더"), ("Tq3_PL", "Push Link"), ("Tq3_TL", "Tilt Lever") })
                if (!R[key].Any(double.IsFinite)) warn.Add(nm + " 질량 미입력 → 자중 토크 생략(합계에서 0으로 처리)");
        }
        if (b.Bad > 0) warn.Add("각도로 형상을 조립할 수 없는 샘플 " + b.Bad + "개 (각도 단위·Push Link→Bucket 분기·파라미터 확인)");
        if (!res.Ok) { res.Message = "계산된 항목이 없습니다 (각도 열·단위·분기·파라미터 확인)"; res.Warn = true; return res; }
        string au = s.AngleOut == "rad" ? "rad" : "deg";
        string[][] groups = mode == "ang"
            ? new[] { new[] { "WL_LiftArm_Ang_" + au, "WL_TiltLever_AngRel_" + au, "WL_Bkt_AngRel_" + au, "WL_Bkt_AngAbs_" + au }, new[] { "WL_ArmCyl_Len_m", "WL_BktCyl_Len_m" }, new[] { "WL_Tip_X_m", "WL_Tip_Y_m" } }
            : new[]
            {
                new[] { "WL_HF3_kN", "WL_HF4_kN" }, new[] { "WL_Tq3_HF3_kNm", "WL_Tq3_HF4_kNm", "WL_Tq4_HF4_kNm" }, new[] { "WL_Tq3_CL3_kNm", "WL_Tq3_CL4_kNm", "WL_Tq3_PL_kNm", "WL_Tq3_TL_kNm" },
                new[] { "WL_Tq4_CL4_kNm", "WL_Tq4_PL_kNm", "WL_Tq4_TL_kNm" }, new[] { "WL_Tq4_PinPP_kNm", "WL_Tq4_PinPQ_kNm", "WL_Tq4_PinP41_kNm" }, new[] { "WL_Tq4_Links_kNm" },
                new[] { "WL_Fpin_P40_kN", "WL_Fpin_PGL_kN", "WL_Fpin_PP_kN", "WL_Fpin_PQ_kN", "WL_Fpin_P41_kN" }, new[] { "WL_Tq3_Eq_kNm", "WL_Tq4_Eq_kNm" }, new[] { "WL_CL3_m", "WL_CL4_m" },
                new[] { "WL_CV3_mps", "WL_CV4_mps" }, new[] { "WL_W3_radps", "WL_W4_radps", "WL_W34_radps" }, new[] { "WL_A3_radps2", "WL_A4_radps2", "WL_A34_radps2" }, new[] { "WL_T34_" + au },
            };
        foreach (var gr in groups) { var f = gr.Where(res.Has).ToArray(); if (f.Length > 0) res.Charts.Add(f); }
        bool ov = OverrideCol(d, s) != null;
        string w = warn.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", warn) : "";
        res.Message = mode == "ang"
            ? "✓ 각도 계산 완료 (" + res.Outputs.Count + "개 신호 → 신호 목록·차트)\nBucket 각도 θ4: " + (ov ? "선택한 CSV 열 「" + s.T4Src + "」 (" + (s.T4u == "rad" ? "rad" : "deg") + ") 사용 → 실린더 길이·Tip 위치는 θ3, θ4로 계산 (이후 모든 계산이 이 θ4 기준)" : "계산값 = f(Lift Arm 각도, Tilt Lever 각도 + Offset), Push Link 연결 기구학") + w
            : "✓ 토크 계산 완료 (" + res.Outputs.Count + "개 신호 → 신호 목록·차트, 토크는 각도 증가 방향 +, kN·m)\n"
              + "각도 θ3: CSV 열 「" + s.AA + "」(" + (s.AAu == "rad" ? "rad" : "deg") + ") · θ4: " + (ov ? "선택한 CSV 열 「" + s.T4Src + "」" : "계산값") + " 기준 · HF3/HF4 = 실린더 유압 추력(전체)\n"
              + "Tq3_Eq = Tq3_HF3 + Tq3_HF4 + Tq3_CL3 + Tq3_CL4 + Tq3_PL + Tq3_TL + Tq3_Pins (Arm 총합) · Tq4_Eq = Tq4_HF4 + Tq4_CL4 + Tq4_PL + Tq4_TL + Tq4_PinPP + Tq4_PinPQ + Tq4_PinP41 (Bucket 총합)" + w;
        res.Warn = warn.Count > 0;
        return res;
    }
}
