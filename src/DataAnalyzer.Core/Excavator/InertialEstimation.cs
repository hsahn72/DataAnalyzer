using System.Globalization;

namespace DataAnalyzer.Core.Excavator;

/// <summary>링크 하나의 추정 설정 (HTML ipCfg)</summary>
public sealed class InertialConfig
{
    /// <summary>질량 [kg] (입력)</summary>
    public double M { get; set; } = double.NaN;
    /// <summary>사용할 각도 Range [deg]</summary>
    public double Lo { get; set; } = double.NaN;
    public double Hi { get; set; } = double.NaN;
    /// <summary>Coulomb 마찰 항</summary>
    public bool Coulomb { get; set; } = true;
    /// <summary>점성 마찰 항</summary>
    public bool Viscous { get; set; }
    /// <summary>상수항</summary>
    public bool Offset { get; set; }
    /// <summary>다른 링크(선회 포함)가 움직이는 샘플 제외</summary>
    public bool ExcludeOthers { get; set; } = true;
    /// <summary>움직임 판정 임계값 [deg/s]</summary>
    public double Threshold { get; set; } = 2;
}

/// <summary>확정된 링크 관성 값 (HTML IP.cur[k])</summary>
public sealed record InertialSet(double M, double Cx, double Cy, double Ipiv, double Icom, double Fc, double Fv, string File, string Date);

/// <summary>추정 결과 (HTML IP.res[k])</summary>
public sealed class InertialResult
{
    public int K { get; init; }
    public double M { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
    /// <summary>링크 시작 핀 기준 Izz = Icom + M·|C|²</summary>
    public double Ipiv { get; init; }
    public double Icom { get; init; }
    public double Fc { get; init; }
    public double Fv { get; init; }
    public double B0 { get; init; }
    public double SdX { get; init; }
    public double SdY { get; init; }
    public double SeJ { get; init; }
    public double Rms { get; init; }
    public double R2 { get; init; }
    public double Cond { get; init; }
    public int N { get; init; }
    public (double Min, double Max) Span { get; init; }
    public int Outliers { get; init; }
    public List<string> Warnings { get; init; } = new();
    public string File { get; init; } = "";
    public InertialSet ToSet(string date) => new(M, Cx, Cy, Ipiv, Icom, Fc, Fv, File, date);
}

/// <summary>단독 동작 확인 결과</summary>
public sealed record MotionStat(string Name, double Min, double Max, double Rms, double Active, int Count);
public sealed record MotionCheck(List<MotionStat> Stats, int J, bool Ok, List<MotionStat> Bad, MotionStat Target, bool HasSwing);

/// <summary>최소제곱 결과</summary>
public sealed record LsqResult(double[] X, double[] Se, double Rms, double R2, double Cond, int N);

/// <summary>
/// 단독 동작 시험 데이터로 링크 질량중심·관성 추정 (Bucket → Arm → Boom). HTML의 ipDp / ipRow / ipLsq / ipCheck / ipEstimate.
/// k = 4 버켓, 3 암, 2 붐.
/// </summary>
public static class InertialEstimation
{
    public static int LinkIndex(int k) => k - 2;
    public static string Kor(int k) => ExcavatorSignals.LinkKor[k - 2];
    public static string Eng(int k) => ExcavatorSignals.LinkEng[k - 2];
    const double Rad = Kinematics.Rad;

    public static string Fmt(double x, int d = 4)
    {
        if (!double.IsFinite(x)) return "—";
        if (Math.Abs(x) >= 1e4 || (x != 0 && Math.Abs(x) < 1e-3)) return JsExp(x, 3);
        return Math.Round(x, d, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
    }
    static string JsExp(double x, int d)
    {
        var s = x.ToString("e" + d, CultureInfo.InvariantCulture); // 1.234e+004
        int e = s.IndexOf('e'); var mant = s[..e]; var ex = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
        return mant + "e" + (ex >= 0 ? "+" : "-") + Math.Abs(ex);
    }

    /// <summary>선회 속도 [deg/s] (없으면 null)</summary>
    public static double[]? SwingDps(ExcavatorAnalysis ex)
    {
        var s = ex.S;
        if (s.SwingSpeed == "" || !ex.D.Signals.TryGetValue(s.SwingSpeed, out var sc)) return null;
        double u = s.SwingSpeedUnit == "rpm" ? 6 : s.SwingSpeedUnit == "rps" ? 1 / Rad : 1;
        return sc.Select(x => x * u).ToArray();
    }

    /// <summary>링크 k 추정용 Dp: 하류 링크는 확정값(없으면 ① 값), 추정 대상은 0</summary>
    public static (DynParams Dp, List<string> Bad) DpFor(ExcavatorAnalysis ex, int k, IReadOnlyDictionary<int, InertialSet> cur)
    {
        var dp = Dynamics.Params(ex.G); var bad = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            int kk = i + 2;
            if (kk == k) { dp.M[i] = 0; dp.I[i] = new double[3]; continue; }
            if (kk > k && cur.TryGetValue(kk, out var s)) { dp.M[i] = s.M; dp.C[i] = new V2(s.Cx, s.Cy); dp.I[i] = new[] { dp.I[i][0], dp.I[i][1], s.Icom }; }
        }
        for (int i = 0; i < 3; i++)
        {
            int kk = i + 2;
            if (kk > k && !(double.IsFinite(dp.M[i]) && double.IsFinite(dp.I[i][2]) && double.IsFinite(dp.C[i].X) && double.IsFinite(dp.C[i].Y))) bad.Add(Kor(kk) + "(M, C, Izz)");
        }
        return (dp, bad);
    }

    /// <summary>한 샘플의 회귀 변수. τ_j = X·gx + Y·gy + Jp·pdd (X=M·Cx, Y=M·Cy, Jp = Izz_COM + M·|C|²)</summary>
    public static (double Gx, double Gy, double Pdd, double W) Row(Kinematics g, double[] th, double[] w, double[] al, int j)
    {
        double[] ph = { th[0], th[0] + th[1], th[0] + th[1] + th[2] };
        double[] pd = { w[0], w[0] + w[1], w[0] + w[1] + w[2] };
        double[] pdd = { al[0], al[0] + al[1], al[0] + al[1] + al[2] };
        V2[] v = { Kinematics.Rot(ph[0], g.Lb, 0), Kinematics.Rot(ph[1], g.La, 0) };
        double ax = 0, ay = 0;
        for (int k = 0; k < j; k++) { double jx = -v[k].Y, jy = v[k].X; ax += pdd[k] * jx - pd[k] * pd[k] * v[k].X; ay += pdd[k] * jy - pd[k] * pd[k] * v[k].Y; }
        double Ay = ay + Dynamics.G, c = Math.Cos(ph[j]), s = Math.Sin(ph[j]);
        return (c * Ay - s * ax, -s * Ay - c * ax, pdd[j], w[j]);
    }

    /// <summary>열 스케일링한 정규방정식 최소제곱 (Gauss-Jordan 역행렬)</summary>
    public static LsqResult? Lsq(List<double[]> A, List<double> y)
    {
        int n = y.Count, p = A[0].Length; var sc = new double[p];
        for (int c = 0; c < p; c++) { double s = 0; for (int i = 0; i < n; i++) s += A[i][c] * A[i][c]; sc[c] = Math.Sqrt(s / n); if (sc[c] == 0 || double.IsNaN(sc[c])) sc[c] = 1; }
        var M = new double[p][]; for (int c = 0; c < p; c++) M[c] = new double[p];
        var b = new double[p];
        for (int i = 0; i < n; i++) for (int c = 0; c < p; c++) { double a = A[i][c] / sc[c]; b[c] += a * y[i]; for (int d = 0; d <= c; d++) M[c][d] += a * A[i][d] / sc[d]; }
        for (int c = 0; c < p; c++) for (int d = c + 1; d < p; d++) M[c][d] = M[d][c];
        var W = new double[p][];
        for (int i = 0; i < p; i++) { W[i] = new double[2 * p]; Array.Copy(M[i], W[i], p); W[i][p + i] = 1; }
        for (int c = 0; c < p; c++)
        {
            int m = c; for (int r = c + 1; r < p; r++) if (Math.Abs(W[r][c]) > Math.Abs(W[m][c])) m = r;
            if (!(Math.Abs(W[m][c]) > 1e-12)) return null;
            (W[c], W[m]) = (W[m], W[c]);
            double dd = W[c][c]; for (int k = 0; k < 2 * p; k++) W[c][k] /= dd;
            for (int r = 0; r < p; r++) if (r != c) { double f = W[r][c]; if (f != 0) for (int k = 0; k < 2 * p; k++) W[r][k] -= f * W[c][k]; }
        }
        var Mi = W.Select(r => r[p..]).ToArray(); var x = new double[p];
        for (int c = 0; c < p; c++) for (int d = 0; d < p; d++) x[c] += Mi[c][d] * b[d];
        double rss = 0, sy = 0, syy = 0;
        for (int i = 0; i < n; i++) { double f = 0; for (int c = 0; c < p; c++) f += A[i][c] / sc[c] * x[c]; rss += (y[i] - f) * (y[i] - f); sy += y[i]; syy += y[i] * y[i]; }
        double s2 = rss / Math.Max(1, n - p);
        var se = new double[p]; for (int c = 0; c < p; c++) se[c] = Math.Sqrt(Math.Max(0, s2 * Mi[c][c])) / sc[c];
        static double N1(double[][] m) => Enumerable.Range(0, m[0].Length).Max(c => m.Sum(r => Math.Abs(r[c])));
        double tot = syy - sy * sy / n;
        return new LsqResult(x.Select((v, c) => v / sc[c]).ToArray(), se, Math.Sqrt(rss / n), tot > 0 ? 1 - rss / tot : double.NaN, N1(M) * N1(Mi), n);
    }

    /// <summary>단독 동작 확인 (대상 외 링크 RMS 각속도가 대상의 10% 미만인가)</summary>
    public static MotionCheck Check(ExcavatorAnalysis ex, int k, double thr)
    {
        if (!(thr > 0)) thr = 2;
        var sm = ex.Smoothed(); int n = ex.D.Count, j = LinkIndex(k); var sw = SwingDps(ex); var st = new List<MotionStat>();
        for (int q = 0; q < 3; q++)
        {
            double mn = double.PositiveInfinity, mx = double.NegativeInfinity, s2 = 0; int c = 0, act = 0;
            for (int i = 0; i < n; i++)
            {
                double a = sm.Th[q][i], w = sm.W[q][i];
                if (!double.IsFinite(a) || !double.IsFinite(w)) continue;
                mn = Math.Min(mn, a); mx = Math.Max(mx, a); double wd = w / Rad; s2 += wd * wd; c++; if (Math.Abs(wd) > thr) act++;
            }
            st.Add(new MotionStat(ExcavatorSignals.LinkKor[q], mn / Rad, mx / Rad, c > 0 ? Math.Sqrt(s2 / c) : double.NaN, c > 0 ? act / (double)c : 0, c));
        }
        if (sw != null)
        {
            double s2 = 0; int c = 0, act = 0;
            foreach (var w in sw) { if (!double.IsFinite(w)) continue; s2 += w * w; c++; if (Math.Abs(w) > thr) act++; }
            st.Add(new MotionStat("선회", double.NaN, double.NaN, c > 0 ? Math.Sqrt(s2 / c) : double.NaN, c > 0 ? act / (double)c : 0, c));
        }
        var tg = st[j];
        var oth = st.Where((s, q) => q != j && s.Count > 0).ToList();
        var bad = oth.Where(o => o.Rms > .1 * tg.Rms || o.Active > .1 * Math.Max(tg.Active, .01)).ToList();
        return new MotionCheck(st, j, tg.Count > 0 && tg.Active > .03 && bad.Count == 0, bad, tg, sw != null);
    }

    /// <summary>대상 링크 각도 범위 [deg] (Range 채우기)</summary>
    public static (double Lo, double Hi)? AngleRange(ExcavatorAnalysis ex, int k)
    {
        var a = ex.Smoothed().Th[LinkIndex(k)].Where(double.IsFinite).ToList();
        if (a.Count == 0) return null;
        return (Math.Round(a.Min() / Rad, 1, MidpointRounding.AwayFromZero), Math.Round(a.Max() / Rad, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>추정 실행. 성공하면 Result와 측정/적합 토크 신호, 실패하면 Error 메시지.</summary>
    public static (InertialResult? Result, CalcResult Out, string? Error) Estimate(ExcavatorAnalysis ex, int k, InertialConfig c, IReadOnlyDictionary<int, InertialSet> cur, string file = "")
    {
        var outp = new CalcResult(); int j = LinkIndex(k); string kor = Kor(k);
        double lo = c.Lo, hi = c.Hi, M = c.M;
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || !(hi > lo)) return (null, outp, kor + " 각도 Range(Min < Max)를 입력하세요");
        if (!(M > 0)) return (null, outp, "M" + k + "(질량 [kg])을 입력하세요");
        var F = ex.CylThrustN(j);
        if (F == null) return (null, outp, "③ 에서 " + kor + " 실린더 압력(Head, Rod) 열을 선택하고, ① 에서 직경을 입력하세요");
        var (dp, bad) = DpFor(ex, k, cur);
        if (bad.Count > 0) return (null, outp, "이 단계에는 하류 링크의 확정 값이 필요합니다: " + string.Join(", ", bad) + ". 이전 단계 결과를 [저장된 값 불러오기]로 불러오거나 ① 파라미터에 입력하세요.");
        var S = ex.Smoothed(); int n = ex.D.Count; var sw = SwingDps(ex); var g = ex.G;
        var chk = Check(ex, k, c.Threshold);
        var tau = new double[n]; Array.Fill(tau, double.NaN);
        var rr = new (double Gx, double Gy, double Pdd, double W)?[n]; var has = new bool[n];
        for (int i = 0; i < n; i++)
        {
            var th = new[] { S.Th[0][i], S.Th[1][i], S.Th[2][i] }; var w = new[] { S.W[0][i], S.W[1][i], S.W[2][i] }; var a = new[] { S.Al[0][i], S.Al[1][i], S.Al[2][i] };
            if (!th.Concat(w).Concat(a).All(double.IsFinite) || !double.IsFinite(F[i])) continue;
            var res = Dynamics.Inertial(g, dp, th, w, a, true); double ta = F[i] * g.DLdTh(j, th);
            if (!double.IsFinite(ta)) continue;
            tau[i] = ta - res.Tau[j]; rr[i] = Row(g, th, w, a, j); has[i] = true;
        }
        var ok = new bool[n]; double thd = c.Threshold * Rad; int mg = S.N + 2;
        for (int i = mg; i < n - mg; i++)
        {
            if (!has[i]) continue;
            double ang = S.Th[j][i] / Rad;
            if (ang < lo || ang > hi) continue;
            if (c.ExcludeOthers)
            {
                bool mv = false;
                for (int q = 0; q < 3; q++) if (q != j && Math.Abs(S.W[q][i]) > thd) mv = true;
                if (sw != null && Math.Abs(sw[i]) > c.Threshold) mv = true;
                if (mv) continue;
            }
            ok[i] = true;
        }
        var wa = new List<double>(); int cnt = 0;
        for (int i = 0; i < n; i++) if (ok[i]) { cnt++; wa.Add(Math.Abs(rr[i]!.Value.W)); }
        wa.Sort();
        double w95 = wa.Count > 0 ? wa[(int)Math.Floor(wa.Count * .95)] : 0, eps = Math.Max(1e-3, .03 * w95);
        int p = 3 + (c.Coulomb ? 1 : 0) + (c.Viscous ? 1 : 0) + (c.Offset ? 1 : 0), minN = Math.Max(20, p * 8);
        if (cnt < minN) return (null, outp, "Range 안의 유효 샘플이 너무 적습니다 (" + cnt + "개). Range를 넓히거나 [다른 링크 움직임 샘플 제외] 임계값을 높이세요.");
        double[] Reg((double Gx, double Gy, double Pdd, double W) r)
        {
            var v = new List<double> { r.Gx, r.Gy, r.Pdd };
            if (c.Coulomb) v.Add(Math.Tanh(r.W / eps));
            if (c.Viscous) v.Add(r.W);
            if (c.Offset) v.Add(1);
            return v.ToArray();
        }
        var A = new List<double[]>(); var yy = new List<double>();
        for (int i = 0; i < n; i++) if (ok[i]) { A.Add(Reg(rr[i]!.Value)); yy.Add(tau[i]); }
        var L = Lsq(A, yy);
        if (L == null) return (null, outp, "최소제곱 행렬이 특이(singular)합니다. 각도·가속도 변화가 충분한 구간을 Range로 지정하세요.");
        int nOut = 0;
        {
            var rs = A.Select((r, i) => { double f = 0; for (int z = 0; z < r.Length; z++) f += r[z] * L.X[z]; return yy[i] - f; }).ToList();
            var ab = rs.Select(Math.Abs).OrderBy(x => x).ToList(); double sg = 1.4826 * ab[ab.Count >> 1];
            if (sg > 0)
            {
                var A2 = new List<double[]>(); var y2 = new List<double>();
                for (int i = 0; i < rs.Count; i++) if (Math.Abs(rs[i]) <= 4 * sg) { A2.Add(A[i]); y2.Add(yy[i]); }
                nOut = A.Count - A2.Count;
                if (nOut > 0 && A2.Count >= minN) { var L2 = Lsq(A2, y2); if (L2 != null) L = L2; else nOut = 0; } else nOut = 0;
            }
        }
        double X = L.X[0], Y = L.X[1], Jp = L.X[2], cx = X / M, cy = Y / M, Icom = Jp - M * (cx * cx + cy * cy);
        int qi = 3; double fc = c.Coulomb ? L.X[qi++] : 0, fv = c.Viscous ? L.X[qi++] : 0, b0 = c.Offset ? L.X[qi++] : 0;
        double sdx = L.Se[0] / M, sdy = L.Se[1] / M; var wn = new List<string>();
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        for (int i = 0; i < n; i++) if (ok[i]) { double a = S.Th[j][i] / Rad; mn = Math.Min(mn, a); mx = Math.Max(mx, a); }
        if (mx - mn < 25) wn.Add("사용한 각도 변화폭이 " + Math.Round(mx - mn).ToString(CultureInfo.InvariantCulture) + "°로 작아 Cx/Cy 분리가 불안정할 수 있습니다 (권장 ≥ 25°).");
        if (L.Cond > 1e4) wn.Add("회귀 행렬 조건수가 큽니다 (" + JsExp(L.Cond, 1) + "). 가속도 변화가 작거나 마찰/상수항 옵션이 서로 겹칠 수 있습니다.");
        if (Icom < 0) wn.Add("Izz(COM)이 음수입니다. M 값이 너무 작거나 Cx/Cy 추정 오차, 노이즈 영향일 수 있습니다.");
        if (!chk.Ok && !c.ExcludeOthers) wn.Add("단독 동작 확인에서 경고가 있었습니다.");
        if (sdx > Math.Max(.05, .15 * Math.Abs(cx)) || sdy > Math.Max(.05, .15 * Math.Abs(cy)))
            wn.Add("Cx/Cy 추정 불확실성이 큽니다 (±1σ: " + Fmt(sdx, 3) + ", " + Fmt(sdy, 3) + " m). 단일 주파수 정현파 같은 단조로운 동작은 중력항과 관성항을 분리하기 어렵습니다. 속도를 바꿔가며 반복한 데이터를 사용하세요.");
        if (L.Se[2] > .15 * Math.Abs(Jp)) wn.Add("Izz 추정 불확실성이 큽니다 (±1σ: " + Fmt(L.Se[2], 1) + " kg·m², 값 " + Fmt(Jp, 1) + ").");
        var result = new InertialResult
        {
            K = k, M = M, Cx = cx, Cy = cy, Ipiv = Jp, Icom = Icom, Fc = fc, Fv = fv, B0 = b0, SdX = sdx, SdY = sdy, SeJ = L.Se[2],
            Rms = L.Rms, R2 = L.R2, Cond = L.Cond, N = L.N, Span = (mn, mx), Outliers = nOut, Warnings = wn, File = file,
        };
        var meas = new double[n]; var fit = new double[n]; Array.Fill(meas, double.NaN); Array.Fill(fit, double.NaN);
        var cf = new List<double> { X, Y, Jp }; if (c.Coulomb) cf.Add(fc); if (c.Viscous) cf.Add(fv); if (c.Offset) cf.Add(b0);
        for (int i = 0; i < n; i++)
        {
            if (!has[i] || !double.IsFinite(tau[i])) continue;
            var v = Reg(rr[i]!.Value); double f = 0; for (int z = 0; z < v.Length; z++) f += v[z] * cf[z];
            meas[i] = tau[i] / 1e3; fit[i] = f / 1e3;
        }
        string mN = Eng(k) + "_IP_TqMeas_kNm", fN = Eng(k) + "_IP_TqFit_kNm";
        outp.Outputs.Add(new(mN, meas, "kNm")); outp.Outputs.Add(new(fN, fit, "kNm")); outp.Charts.Add(new[] { mN, fN });
        outp.Message = "✓ " + kor + " 추정 완료 — 샘플 " + L.N + "개, RMS 잔차 " + Fmt(L.Rms, 1) + " N·m, R² " + Fmt(L.R2, 4) + ". 결과를 확인하고 [결과 확정]을 누르면 세트에 반영됩니다. 차트: " + mN + ", " + fN;
        return (result, outp, null);
    }

    static double P6(double x) => double.Parse(x.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>확정 값을 ① 파라미터에 적용 (M, C, Izz — IRef가 pivot이면 핀 기준 Izz)</summary>
    public static KinParams ApplyTo(KinParams k0, IReadOnlyDictionary<int, InertialSet> cur)
    {
        var k = k0.Clone(); double u = k.Unit == "m" ? 1 : .001; bool piv = k.Str("IRef") == "pivot";
        foreach (var (kk, s) in cur)
        {
            k["M" + kk] = P6(s.M); k["C" + kk + "x"] = P6(s.Cx / u); k["C" + kk + "y"] = P6(s.Cy / u); k["I" + kk + "zz"] = P6(piv ? s.Ipiv : s.Icom);
        }
        return k;
    }
}
