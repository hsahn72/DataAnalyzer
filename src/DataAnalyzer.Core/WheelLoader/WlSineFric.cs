using System.Globalization;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.WheelLoader;

/// <summary>Sine 피팅 하나 (k: 각도 배율)</summary>
public sealed record WlSine(double K, double A_, double B, double C, double D, double[] Se, double Rms, double Amp, double Phi);

/// <summary>e-4 결과 (Sine Fitting + 마찰력 Fitting). 값/표준오차 쌍.</summary>
public sealed class WlBrEst
{
    public (double V, double Se) X { get; set; }
    public (double V, double Se) Y { get; set; }
    public (double V, double Se) C { get; set; }
    public double KBest { get; set; } = double.NaN;
    public (double V, double Se) D { get; set; } = (double.NaN, double.NaN);
    /// <summary>Sine 계수 a, b, C, dF와 dF 포함 여부</summary>
    public double Sa { get; set; }
    public double Sb { get; set; }
    public double Sc { get; set; }
    public double Sd { get; set; }
    public bool UseD { get; set; }
    public double R2 { get; set; }
    public double Rms { get; set; }
    public int Bins { get; set; }
    public int Samples { get; set; }
    /// <summary>마찰 계수 (c, p1, p2, v, d, jc, jf) — 마찰력 Fitting 후</summary>
    public (double V, double Se)[]? Fric { get; set; }
    public bool[]? FricOn { get; set; }
    public double FricRms0 { get; set; }
    public double FricRms1 { get; set; }
}

/// <summary>휠로더 Crowd·Dump 평균/반차 + Sine Fitting, 마찰력 Fitting (HTML wlSineFit / wlBrCtx / wlBrSine / wlBrFric / wlBrCopy)</summary>
public static class WlSineFric
{
    const double Rad = Kinematics.Rad;
    static double[] Fill(int n) { var a = new double[n]; Array.Fill(a, double.NaN); return a; }
    static bool Fin(double x) => !double.IsNaN(x);
    static string P(double v, int p) => Format.Precision(v, p);
    static string Fmt(double v, double s) => double.IsFinite(v) ? P(v, 4) + (double.IsFinite(s) ? " ± " + P(s, 2) : "") : "—";

    /// <summary>가중 Sine 피팅: y = a·cos(kθ) + b·sin(kθ) (+ C) (+ d·ex). k를 k0~k1에서 탐색한 최적과 k=1 결과.</summary>
    public static (WlSine? Best, WlSine? One) SineFit(double[] th, double[] y, double[] w, double k0, double k1, double dk, bool noC, double[]? ex)
    {
        int m = th.Length, p = (noC ? 2 : 3) + (ex != null ? 1 : 0);
        double[] H(int i, double k) { var h = new List<double> { Math.Cos(k * th[i]), Math.Sin(k * th[i]) }; if (!noC) h.Add(1); if (ex != null) h.Add(ex[i]); return h.ToArray(); }
        WlSine? Fit(double k)
        {
            var M = new double[p][]; for (int a = 0; a < p; a++) M[a] = new double[p];
            var r = new double[p];
            for (int i = 0; i < m; i++) { var h = H(i, k); for (int a = 0; a < p; a++) { r[a] += w[i] * h[a] * y[i]; for (int b = 0; b < p; b++) M[a][b] += w[i] * h[a] * h[b]; } }
            var x = WlEstimation.SolveSym(M, r); if (x == null) return null;
            double e = 0, sw = 0;
            for (int i = 0; i < m; i++) { var h = H(i, k); double f = 0; for (int j = 0; j < p; j++) f += h[j] * x[j]; double d = y[i] - f; e += w[i] * d * d; sw += w[i]; }
            double sg2 = e / Math.Max(1, m - p);
            var se = Enumerable.Range(0, p).Select(q => { var c = WlEstimation.SolveSym(M, Enumerable.Range(0, p).Select(j => j == q ? 1.0 : 0).ToArray()); return c != null && c[q] > 0 ? Math.Sqrt(sg2 * c[q]) : double.NaN; }).ToArray();
            return new WlSine(k, x[0], x[1], noC ? 0 : x[2], ex != null ? x[p - 1] : 0, se, Math.Sqrt(e / sw), Math.Sqrt(x[0] * x[0] + x[1] * x[1]), Math.Atan2(-x[1], x[0]));
        }
        WlSine? best = null;
        for (double k = k0; k <= k1 + 1e-9; k += dk) { var f = Fit(k); if (f != null && (best == null || f.Rms < best.Rms)) best = f; }
        return (best, Fit(1));
    }

    sealed class Ctx
    {
        public required WlBase B; public required KinParams KK; public required WlKin Kn; public required Dictionary<string, double[]> T;
        public required double Eps; public required sbyte[] Dir; public int Sg; public required double[] H; public required List<int> Sel; public double Amin;
        public required Dictionary<string, double[]> O;
    }

    static Ctx? Context(DataSet d, KinParams K, WlSignals s, WlEstState st, out string? err)
    {
        var (b, e) = WlAnalysis.Base(d, K, s);
        if (b == null) { err = e; return null; }
        if (b.FB == null || b.CB == null) { err = "Bucket 실린더 압력(Head/Rod 열)과 Tube/Rod 직경이 필요합니다."; return null; }
        var KK = b.KK; int n = b.N; var T = WlAnalysis.TqAll(b); var kn = WlAnalysis.Kin(d, s, b); var (lo, hi) = WlEstimation.AngRange(KK, "T4min", "T4max");
        var SM = WlEstimation.SelMask(true, b, kn, KK, st.DirInv);
        bool InR(int i) { double x = b.T3[i]; return Fin(x) && x >= lo && x <= hi && (SM == null || SM[i]) && Fin(b.VB[i]) && Fin(T["Tq4_Eq"][i]); }
        double eps = WlEstimation.EpsOf(KK, b.VB, InR);
        var rmask = new bool[n]; for (int i = 0; i < n; i++) rmask[i] = Fin(b.T3[i]) && b.T3[i] >= lo && b.T3[i] <= hi;
        var DIR = WlEstimation.DirArr(kn.W4, rmask, kn.Ts);
        var H = Fill(n); var sel = new List<int>(); double amin = double.PositiveInfinity;
        for (int i = 0; i < n; i++) { if (!InR(i)) continue; H[i] = T["Tq4_Eq"][i]; sel.Add(i); amin = Math.Min(amin, b.T3[i]); }
        if (sel.Count < 20) { err = "사용할 샘플이 부족합니다 (T4 Range·구간 선별을 확인하세요)."; return null; }
        err = null;
        return new Ctx
        {
            B = b, KK = KK, Kn = kn, T = T, Eps = eps, Dir = DIR, Sg = st.DirInv ? -1 : 1, H = H, Sel = sel, Amin = amin,
            O = WlEstimation.Obs(b, kn, WlEstimation.EpsOf(KK, b.VA, _ => true), eps),
        };
    }

    static string AngAxis(DataSet d, CalcResult res, double[] t3)
    {
        var ax = new[] { "WL_Bkt_AngRel_deg", "WL_Bkt_AngRel_rad" }.FirstOrDefault(d.Signals.ContainsKey);
        if (ax == null) { ax = "WL_Bkt_AngRel_deg"; res.Outputs.Add(new(ax, t3.Select(x => x / Rad).ToArray(), "deg")); }
        return ax;
    }

    /// <summary>1단계: Crowd·Dump 평균(0.5° 구간)을 중력 Sine으로 피팅 → M4·C4x, M4·C4y. 결과는 st.Br.</summary>
    public static CalcResult Sine(DataSet d, KinParams K, WlSignals s, WlEstState st, out WlBrEst? est)
    {
        var res = new CalcResult(); est = null;
        var C = Context(d, K, s, st, out var err);
        if (C == null) { res.Message = err!; res.Warn = true; return res; }
        var KK = C.KK; var b = C.B; int n = b.N; const double G9 = 9.81; bool noC = KK.Str("BrC") == "zero", useD = KK.Str("BrD") == "on";
        double bw = .5 * Rad; var order = new List<int>(); var bn = new Dictionary<int, (double[] c, double[] d, double th, int n, double j)>();
        foreach (var i in C.Sel)
        {
            if (C.Dir[i] == 0) continue;
            int bk = (int)Math.Floor((b.T3[i] - C.Amin) / bw);
            if (!bn.TryGetValue(bk, out var o)) { o = (new double[2], new double[2], 0, 0, 0); order.Add(bk); }
            var q = C.Dir[i] * C.Sg > 0 ? o.c : o.d; q[0] += C.H[i]; q[1]++;
            o.th += b.T2[i] + b.T3[i]; o.j += Fin(C.O["J4"][i]) ? C.O["J4"][i] : 0; o.n++;
            bn[bk] = o;
        }
        var bins = order.Select(k => (k, o: bn[k])).Where(x => x.o.c[1] >= 3 && x.o.d[1] >= 3)
            .Select(x => (B: x.k, Th: x.o.th / x.o.n, M: (x.o.c[0] / x.o.c[1] + x.o.d[0] / x.o.d[1]) / 2, H: (x.o.c[0] / x.o.c[1] - x.o.d[0] / x.o.d[1]) / 2, W: Math.Min(x.o.c[1], x.o.d[1]), J: x.o.j / x.o.n / 1e3)).ToList();
        if (bins.Count < 8) { res.Message = "Crowd와 Dump가 모두 있는 각도 구간(0.5° 간격)이 부족합니다 (" + bins.Count + "개). 왕복 데이터와 방향 기준(e-1)을 확인하세요."; res.Warn = true; return res; }
        var bm = bins.ToDictionary(o => o.B); var mH = Fill(n); var hH = Fill(n);
        foreach (var i in C.Sel) if (bm.TryGetValue((int)Math.Floor((b.T3[i] - C.Amin) / bw), out var o)) { mH[i] = o.M; hH[i] = o.H; }
        var th = bins.Select(o => o.Th).ToArray(); var w = bins.Select(o => o.W).ToArray(); var ym = bins.Select(o => o.M).ToArray(); var ex = useD ? bins.Select(o => o.J).ToArray() : null;
        var fk = SineFit(th, ym, w, .5, 1.5, .005, noC, ex); var f1 = SineFit(th, ym, w, 1, 1, 1, noC, ex).One;
        if (f1 == null) { res.Message = "Sine 피팅 실패 (각도 범위가 너무 좁습니다)."; res.Warn = true; return res; }
        double X = f1.A_ * 1e3 / G9, Y = -f1.B * 1e3 / G9, dF2 = useD ? f1.D : 0, seD = useD ? f1.Se[^1] : double.NaN;
        var RES = Fill(n); var SFg = Fill(n);
        foreach (var i in C.Sel)
        {
            if (Fin(mH[i])) RES[i] = C.T["Tq4_Eq"][i] - mH[i];
            SFg[i] = f1.A_ * Math.Cos(b.T2[i] + b.T3[i]) + f1.B * Math.Sin(b.T2[i] + b.T3[i]) + f1.C;
        }
        double sw = 0, sy = 0; for (int i = 0; i < bins.Count; i++) { sw += w[i]; sy += w[i] * ym[i]; }
        double my = sy / sw, sst = 0, sse = 0, mx = 0;
        for (int i = 0; i < bins.Count; i++)
        {
            double fit = f1.A_ * Math.Cos(th[i]) + f1.B * Math.Sin(th[i]) + f1.C + (ex != null ? f1.D * ex[i] : 0), e = ym[i] - fit;
            sst += w[i] * (ym[i] - my) * (ym[i] - my); sse += w[i] * e * e; mx = Math.Max(mx, Math.Abs(e));
        }
        double R2 = sst > 0 ? 1 - sse / sst : double.NaN; int p = (noC ? 2 : 3) + (ex != null ? 1 : 0);
        res.Outputs.Add(new("WL_HTq4_Mean_kNm", mH, "kNm")); res.Outputs.Add(new("WL_HTq4_Half_kNm", hH, "kNm"));
        res.Outputs.Add(new("WL_HTq4_SineFit_kNm", SFg, "kNm")); res.Outputs.Add(new("WL_Tq4_Residual_kNm", RES, "kNm"));
        var ax = AngAxis(d, res, b.T3);
        res.XyCharts.Add((ax, "WL_Tq4_Eq_kNm")); res.XyCharts.Add((ax, "WL_HTq4_Mean_kNm")); res.XyCharts.Add((ax, "WL_HTq4_SineFit_kNm"));
        est = new WlBrEst
        {
            X = (X, f1.Se[0] * 1e3 / G9), Y = (Y, f1.Se[1] * 1e3 / G9), C = (f1.C, noC ? double.NaN : f1.Se[2]), KBest = fk.Best?.K ?? double.NaN,
            D = useD ? (dF2, seD) : (double.NaN, double.NaN), Sa = f1.A_, Sb = f1.B, Sc = f1.C, Sd = dF2, UseD = useD, R2 = R2, Rms = f1.Rms, Bins = bins.Count, Samples = C.Sel.Count,
        };
        double M4 = KK["BKM"], span = (th.Max() - th.Min()) / Rad;
        var L = new List<string>
        {
            "✓ Sine Fitting 완료 (Crowd·Dump 평균 → 중력 Sine, 각도 0.5° 구간 " + bins.Count + "개, 사용 샘플 " + C.Sel.Count + "개, T34 범위 " + Math.Round(span).ToString(CultureInfo.InvariantCulture) + "°)",
            "  모델: 평균 = a·cos(T34) + b·sin(T34)" + (noC ? "" : " + C") + (useD ? " + dF4·J4" : "") + "  (k = 1 고정)",
            "  M4·C4x = a/9.81 = " + Fmt(X, f1.Se[0] * 1e3 / G9) + " kg·m,  M4·C4y = −b/9.81 = " + Fmt(Y, f1.Se[1] * 1e3 / G9) + " kg·m" + (noC ? "" : ",  C = " + Fmt(f1.C, f1.Se[2]) + " kN·m") + (useD ? ",  dF4 = " + Fmt(dF2, seD) + " N" : "") + "  → Sine 진폭 A = " + P(f1.Amp, 4) + " kN·m",
        };
        if (M4 > 0) L.Add("  → 입력한 M4=" + M4 + " kg 기준: C4x = " + P(X / M4, 5) + " m, C4y = " + P(Y / M4, 5) + " m");
        L.Add("  [Fitting 통계] 자유도 " + Math.Max(0, bins.Count - p) + " · R² = " + (double.IsFinite(R2) ? R2.ToString("F5", CultureInfo.InvariantCulture) : "—") + " · 가중 잔차 RMS " + P(f1.Rms, 4) + " kN·m · 최대 |잔차| " + P(mx, 4) + " kN·m"
            + (fk.Best is { } bb ? "\n     참고: k 자유 피팅 최적 k = " + bb.K.ToString("F3", CultureInfo.InvariantCulture) + (bb.K <= .505 || bb.K >= 1.495 ? " ⚠ 탐색 경계(0.5/1.5)에 걸림" : Math.Abs(bb.K - 1) > .05 ? " ⚠ k가 1에서 벗어남 → 힘→토크 환산이나 추력 오프셋 의심" : "") : ""));
        {
            double lo = th.Min(), hi = th.Max(), thP = Math.Atan2(f1.B, f1.A_);
            bool Has(double t0) { for (int q = -3; q <= 3; q++) { double pp = t0 + 2 * Math.PI * q; if (pp >= lo && pp <= hi) return true; } return false; }
            double rel = Math.Max(f1.Se[0], f1.Se[1]) * 1e3 / G9 / Math.Max(1e-9, Math.Sqrt(X * X + Y * Y)), C4 = M4 > 0 ? Math.Sqrt(X * X + Y * Y) / M4 : double.NaN;
            if (!Has(thP) && !Has(thP + Math.PI)) L.Add("⚠ 관측한 T34 범위에 Sine의 정점·저점이 없습니다 → 진폭 A와 오프셋 C가 서로 대체되어 M4·C4x/y가 부정확합니다. [Sine 오프셋 C = 0 고정]으로 다시 해 보거나 더 넓은 각도 범위 데이터를 쓰세요.");
            if (rel > .3) L.Add("⚠ M4·C4 표준오차가 값의 " + Math.Round(rel * 100).ToString(CultureInfo.InvariantCulture) + " % 입니다 — 신뢰하기 어렵습니다.");
            if (C4 > 1.2 * b.G.L4) L.Add("⚠ 입력한 M4 기준 무게중심 거리 " + C4.ToString("F2", CultureInfo.InvariantCulture) + " m 가 Bucket 길이 L4보다 큽니다 → 물리적으로 불가능.");
        }
        res.Message = string.Join("\n", L);
        return res;
    }

    static readonly (string Key, string Col)[] FricParams = { ("uF4c", "Hkkf1"), ("uF4p1", "Hkkf2"), ("uF4p2", "Hkkf3"), ("uF4v", "Hkkf4"), ("uF4d", "J4"), ("uFjc", "Hjc"), ("uFjf", "Hjf") };
    static readonly string[] FricNames = { "Coulomb", "Pressure Coulomb1", "Pressure Coulomb2", "Viscous", "dF4", "Joint Coulomb", "Joint 힘 비례" };
    static readonly string[] FricUnits = { "N", "N/bar", "N/bar", "N·s/m", "N", "N·m", "m" };

    /// <summary>2단계: (Tq4_Eq − SineFit)을 마찰 열로 회귀. regOn = 키별 대상 여부 (없으면 uF4d·uFjc·uFjf 꺼짐)</summary>
    public static CalcResult Friction(DataSet d, KinParams K, WlSignals s, WlEstState st, WlBrEst e, IReadOnlyDictionary<string, bool>? regOn = null)
    {
        var res = new CalcResult();
        var C = Context(d, K, s, st, out var err);
        if (C == null) { res.Message = err!; res.Warn = true; return res; }
        var KK = C.KK; var b = C.B; int n = b.N; bool useD = e.UseD;
        bool On(string k) => regOn != null && regOn.TryGetValue(k, out var v) ? v : k is not ("uF4d" or "uFjc" or "uFjf");
        var SF = Fill(n); foreach (var i in C.Sel) SF[i] = e.Sa * Math.Cos(b.T2[i] + b.T3[i]) + e.Sb * Math.Sin(b.T2[i] + b.T3[i]) + e.Sc;
        var PK = FricParams; var NN = KK.Str("BkNN") == "free" ? new bool[7] : new[] { true, true, true, true, false, true, true };
        var on = PK.Select((p, j) => On(p.Key) && !(useD && j == 4)).ToArray(); var fix = PK.Select((_, j) => useD && j == 4 ? e.Sd : 0).ToArray();
        var act = Enumerable.Range(0, 7).Where(j => on[j]).ToList();
        if (act.Count == 0) { res.Message = "Regression 대상 마찰 계수를 하나 이상 체크하세요."; res.Warn = true; return res; }
        var O = C.O; var R = new List<double[]>(); var y = new List<double>(); var ix = new List<int>();
        foreach (var i in C.Sel)
        {
            if (C.Dir[i] == 0 || !PK.All(p => Fin(O[p.Col][i]))) continue;
            double fx = 0; for (int j = 0; j < 7; j++) if (!on[j]) fx += fix[j] * O[PK[j].Col][i];
            R.Add(act.Select(j => O[PK[j].Col][i]).ToArray()); y.Add((C.H[i] - SF[i]) * 1e3 - fx); ix.Add(i);
        }
        double winS = KK["WTwin"]; if (!double.IsFinite(winS)) winS = .5;
        double itv = KK["IRLSn"]; int itN = Math.Max(0, (int)Math.Floor((double.IsFinite(itv) ? itv : 20) + .5));
        var Wst = winS > 0 && ix.Count > 10 ? WlEstimation.StabW(new[] { b.CB!.HP, b.CB.RP }, C.Kn.Ts, winS, ix) : null; var wv = Wst != null ? ix.Select(i => Wst.W[i]).ToArray() : null;
        if (R.Count < 30) { res.Message = "움직이는 샘플이 부족합니다 (" + R.Count + "개)."; res.Warn = true; return res; }
        var fr = WlEstimation.IRLS(R, y, act.Select(j => NN[j]).ToArray(), wv, itN);
        if (fr == null) { res.Message = "마찰력 Fitting 실패: 열이 서로 종속입니다 (체크 항목을 줄여 보세요)."; res.Warn = true; return res; }
        var cf = new double[7]; var sef = new double[7]; Array.Fill(cf, double.NaN); Array.Fill(sef, double.NaN);
        for (int k = 0; k < act.Count; k++) { cf[act[k]] = fr.X[k]; sef[act[k]] = fr.Se[k]; }
        var FT = Fill(n); var Ti = Fill(n); var E2 = Fill(n);
        foreach (var i in C.Sel)
        {
            double f = 0; bool ok = true;
            for (int j = 0; j < 7; j++)
            {
                double c = on[j] ? cf[j] : fix[j], o = O[PK[j].Col][i];
                if (double.IsNaN(c) || double.IsNaN(o)) { if (c != 0) ok = false; continue; }
                f += c * o;
            }
            if (ok) { FT[i] = f / 1e3; Ti[i] = C.H[i] - f / 1e3; E2[i] = SF[i] + f / 1e3; } else Ti[i] = C.H[i];
        }
        double u0 = 0, u1 = 0;
        for (int k = 0; k < ix.Count; k++) { int i = ix[k]; u0 += ((C.H[i] - SF[i]) * 1e3) * ((C.H[i] - SF[i]) * 1e3); double ff = 0; for (int j = 0; j < R[k].Length; j++) ff += R[k][j] * fr.X[j]; u1 += (y[k] - ff) * (y[k] - ff); }
        double rms0 = Math.Sqrt(u0 / ix.Count), rms1 = Math.Sqrt(u1 / ix.Count);
        res.Outputs.Add(new("WL_Tq4i_kNm", Ti, "kNm")); res.Outputs.Add(new("WL_HTq4_Fric_kNm", FT, "kNm")); res.Outputs.Add(new("WL_HTq4_Est2_kNm", E2, "kNm"));
        var ax = AngAxis(d, res, b.T3);
        res.XyCharts.Add((ax, "WL_Tq4_Residual_kNm")); res.XyCharts.Add((ax, "WL_HTq4_Fric_kNm"));
        e.Fric = Enumerable.Range(0, 7).Select(j => (cf[j], sef[j])).ToArray(); e.FricOn = on; e.FricRms0 = rms0; e.FricRms1 = rms1;
        if (!useD) e.D = (cf[4], sef[4]);
        var L = new List<string>
        {
            "✓ 마찰력 Fitting 완료 (잔차 = Tq4_Eq − SineFit [N·m], 샘플 " + ix.Count + "개)",
            "  Regression 대상: " + string.Join(", ", act.Select(j => FricNames[j])) + (useD ? " · dF4는 Sine Fitting 단계 값으로 고정" : "") + " · ε = " + P(C.Eps * 1e3, 3) + " mm/s · 부호 제약: " + (KK.Str("BkNN") == "free" ? "없음" : "마찰 계수 ≥ 0"),
            "  [Fitting 통계] 잔차 RMS " + P(rms0 / 1e3, 4) + " → " + P(rms1 / 1e3, 4) + " kN·m",
        };
        foreach (var j in act) L.Add("     " + FricNames[j] + " = " + Fmt(cf[j], sef[j]) + " " + FricUnits[j]);
        var cl = act.Where((j, k) => NN[j] && !fr.Act.Contains(k)).ToList();
        if (cl.Count > 0) L.Add("⚠ 하한 0에 걸린 계수: " + string.Join(", ", cl.Select(j => FricNames[j])) + " — 마찰 열끼리 비슷하면(공선성) 음수가 나옵니다.");
        res.Message = string.Join("\n", L);
        return res;
    }

    /// <summary>Sine·마찰 결과를 [사용값]에 복사 (M4C4x, M4C4y + 대상 마찰 계수)</summary>
    public static void CopyToK(KinParams K, WlBrEst e, IReadOnlyDictionary<string, bool>? regOn = null)
    {
        bool On(string k) => regOn != null && regOn.TryGetValue(k, out var v) ? v : k is not ("uF4d" or "uFjc" or "uFjf");
        static double P6(double v) => double.Parse(v.ToString("E5", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        K["uM4C4x"] = P6(e.X.V); K["uM4C4y"] = P6(e.Y.V);
        var keys = new[] { "uF4c", "uF4p1", "uF4p2", "uF4v", "uF4d", "uFjc", "uFjf" };
        for (int j = 0; j < 7; j++)
        {
            double v = j == 4 ? e.D.V : e.Fric != null ? e.Fric[j].V : double.NaN;
            if (On(keys[j]) && double.IsFinite(v)) K[keys[j]] = P6(v);
        }
    }
}
