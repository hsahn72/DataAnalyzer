using System.Globalization;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Excavator;

/// <summary>계산 결과 신호 하나</summary>
public sealed record OutSignal(string Name, double[] Values, string Unit);

/// <summary>계산 결과: 새 신호들, 만들 차트, 메시지</summary>
public sealed class CalcResult
{
    public List<OutSignal> Outputs { get; } = new();
    /// <summary>시계열 차트로 추가할 신호 묶음</summary>
    public List<string[]> Charts { get; } = new();
    /// <summary>XY 차트 (X, Y)</summary>
    public List<(string X, string Y)> XyCharts { get; } = new();
    public string Message { get; set; } = "";
    public bool Warn { get; set; }
    public bool Ok => Outputs.Count > 0;

    /// <summary>값이 하나라도 유한하면 추가</summary>
    internal bool Put(string name, double[] a, string unit)
    {
        if (!a.Any(double.IsFinite)) return false;
        Outputs.Add(new OutSignal(name, a, unit));
        return true;
    }
    public bool Has(string name) => Outputs.Any(o => o.Name == name);

    /// <summary>결과 신호를 데이터에 넣는다</summary>
    public void ApplyTo(DataSet d) { foreach (var o in Outputs) d.Put(o.Name, o.Values, o.Unit); }
}

/// <summary>링크 각도 시계열 (HTML exAngSeries)</summary>
public sealed class AngleSeries
{
    /// <summary>상대각 [rad] (붐, 암, 버켓)</summary>
    public required double[][] Th { get; init; }
    /// <summary>기구학 해 없음 개수</summary>
    public required int[] Bad { get; init; }
    /// <summary>실린더 행정 밖 개수</summary>
    public required int[] OutOfStroke { get; init; }
    /// <summary>실린더 길이 유효 샘플 수</summary>
    public required int[] CylCount { get; init; }
    /// <summary>출처 비트: 1 = 각도 열, 2 = 실린더 길이</summary>
    public required int[] Src { get; init; }
}

/// <summary>
/// 굴착기 CSV 시계열 분석: 각도 변환, 동역학(Tip 외력), 실린더 속도·추력, 속도 적분, Head/Rod 진단.
/// HTML의 exAngSeries / exCalcAngles / exCalcDyn / exCylVel / exCylThrust / exIntLen / exDiagHR.
/// </summary>
public sealed class ExcavatorAnalysis
{
    public DataSet D { get; }
    public Kinematics G { get; }
    public ExcavatorSignals S { get; }
    public DynParams Dp { get; }

    public ExcavatorAnalysis(DataSet d, Kinematics g, ExcavatorSignals s)
    {
        D = d; G = g; S = s; Dp = Dynamics.Params(g);
    }

    const double Rad = Kinematics.Rad;
    double[]? Col(string name) => name != "" && D.Signals.TryGetValue(name, out var a) ? a : null;
    static double[] Fill(int n, double v = double.NaN) { var a = new double[n]; Array.Fill(a, v); return a; }
    static int JsRound(double x) => (int)Math.Floor(x + .5);
    static string F(double x, int d) => Math.Round(x, d, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    /// <summary>시간 [s] (날짜 시간축이거나 시간 단위가 ms면 1/1000)</summary>
    public double[] TimeSec() => D.TimeSeconds(S.TimeUnit == "ms");

    /// <summary>평활 창 샘플 수 (홀수)</summary>
    public static int WinN(double[] ts, double ws)
    {
        var d = new List<double>(); int st = Math.Max(1, ts.Length / 400);
        for (int i = st; i < ts.Length; i += st) d.Add((ts[i] - ts[i - st]) / st);
        d.Sort();
        double m = d.Count > 0 ? d[d.Count >> 1] : 0; if (m == 0 || double.IsNaN(m)) m = 1;
        return Math.Max(1, JsRound(ws / m) | 1);
    }
    public int WinN(double[] ts) => WinN(ts, S.SmoothWindow);

    /// <summary>시간 간격 중앙값 [s]</summary>
    public double MedianDt()
    {
        var ts = TimeSec(); int n = ts.Length; var dd = new List<double>();
        for (int i = 1; i < n; i += Math.Max(1, n / 300)) dd.Add(ts[i] - ts[i - 1]);
        dd.Sort();
        double m = dd.Count > 0 ? dd[dd.Count >> 1] : 0;
        return m == 0 || double.IsNaN(m) ? .01 : m;
    }

    // ================= 속도 적분 =================

    /// <summary>속도 열 → 길이 [mm]: L = ∫(v − 바이어스)dt (− 느린 Drift 제거) + 오프셋</summary>
    public double[] IntegrateLength(int j, LinkSignals? o = null)
    {
        var r = o ?? S.Links[j]; var v = D.Signals[r.Vel]; int n = D.Count; var ts = TimeSec();
        double vf = ExcavatorSignals.VelToMps(r.VelUnit); if (double.IsNaN(vf) || vf == 0) vf = .001;
        double f = vf * 1000; var L = new double[n]; double acc = 0;
        for (int i = 1; i < n; i++)
        {
            double dt = ts[i] - ts[i - 1];
            if (dt > 0)
            {
                double a = double.IsFinite(v[i - 1]) ? v[i - 1] : 0, b = double.IsFinite(v[i]) ? v[i] : a;
                acc += ((a + b) / 2 * f - r.VelDrift) * dt;
            }
            L[i] = acc;
        }
        if (r.VelWindow > 0)
        {
            int N = Math.Max(3, JsRound(r.VelWindow / MedianDt()));
            var ma = Dynamics.SmoothMA(L, N); double m = L.Sum() / n;
            for (int i = 0; i < n; i++) L[i] = L[i] - ma[i] + m;
        }
        for (int i = 0; i < n; i++) L[i] += r.VelOffset;
        return L;
    }

    /// <summary>파라미터의 실린더 길이 범위 [mm]</summary>
    public (double Min, double Max) StrokeRangeMm(int j)
    {
        double f = G.K.Unit == "m" ? 1000 : 1; string p = ExcavatorSignals.CylPrefix[j];
        return (G.K[p + "Lmin"] * f, G.K[p + "Lmax"] * f);
    }

    /// <summary>[범위 맞춤] 적분 길이의 최소/최대 중앙이 Lmin~Lmax 중앙에 오도록 오프셋</summary>
    public void FitRange(int j)
    {
        var r = S.Links[j]; r.VelOffset = 0;
        var L = IntegrateLength(j); var (a, b) = StrokeRangeMm(j);
        if (double.IsFinite(a) && double.IsFinite(b)) r.VelOffset = Math.Round((a + b) / 2 - (L.Min() + L.Max()) / 2, 2, MidpointRounding.AwayFromZero);
        r.VelApproved = false;
    }

    /// <summary>[Drift 자동 추정] 적분 길이의 선형 추세를 속도 바이어스로 보고 제거한 뒤 범위 맞춤</summary>
    public void EstimateDrift(int j)
    {
        var r = S.Links[j]; var o = r.Clone(); o.VelDrift = 0; o.VelOffset = 0;
        var L = IntegrateLength(j, o); var ts = TimeSec(); int n = L.Length;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++) { double x = ts[i] - ts[0]; sx += x; sy += L[i]; sxx += x * x; sxy += x * L[i]; }
        double den = n * sxx - sx * sx;
        r.VelDrift = den > 0 ? Math.Round((n * sxy - sx * sy) / den, 5, MidpointRounding.AwayFromZero) : 0;
        FitRange(j);
    }

    public static string IntegratedName(int j) => ExcavatorSignals.LinkEng[j] + "_CylLen_int_mm";

    /// <summary>[승인] 적분 길이를 신호로 저장하고 실린더 길이 열로 쓴다</summary>
    public string ApproveIntegrated(int j)
    {
        var r = S.Links[j]; var nm = IntegratedName(j);
        D.Put(nm, IntegrateLength(j), "mm");
        r.Cyl = nm; r.CylUnit = "mm"; r.VelApproved = true;
        return nm;
    }

    /// <summary>승인된 적분 길이가 데이터에 없으면 다시 만든다 (기준 Data 등)</summary>
    public void EnsureIntegrated()
    {
        for (int j = 0; j < 3; j++)
        {
            var r = S.Links[j];
            if (r.VelApproved && r.Cyl != "" && !D.Signals.ContainsKey(r.Cyl) && r.Vel != "" && D.Signals.ContainsKey(r.Vel))
                D.Put(r.Cyl, IntegrateLength(j), "mm");
        }
    }

    // ================= 각도 =================

    /// <summary>전체 시계열의 링크 상대각 [rad]: 각도 열이 있으면 그대로, 없으면 실린더 길이 → Kinematics</summary>
    public AngleSeries Angles()
    {
        EnsureIntegrated();
        int n = D.Count;
        var th = new double[3][]; for (int j = 0; j < 3; j++) th[j] = Fill(n);
        int[] bad = new int[3], oob = new int[3], nc = new int[3], src = new int[3];
        var A = new (double[] A, double K, double O)?[3]; var C = new (double[] A, double K)?[3];
        for (int j = 0; j < 3; j++)
        {
            var r = S.Links[j];
            if (Col(r.Ang) is { } aa) A[j] = (aa, r.AngUnit == "rad" ? 1 : Rad, r.Offset * Rad);
            if (Col(r.Cyl) is { } ca) C[j] = (ca, ExcavatorSignals.LenToM(r.CylUnit));
        }
        var ang = new double[3]; var rel = new double[3];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < 3; j++) ang[j] = A[j] is { } q ? q.A[i] * q.K + q.O : double.NaN;
            if (S.Absolute) { rel[0] = ang[0]; rel[1] = ang[1] - ang[0]; rel[2] = ang[2] - ang[1]; }
            else { rel[0] = ang[0]; rel[1] = ang[1]; rel[2] = ang[2]; }
            for (int j = 0; j < 3; j++)
            {
                if (!double.IsNaN(rel[j])) { th[j][i] = rel[j]; src[j] |= 1; continue; }
                if (C[j] is not { } c) continue;
                double L = c.A[i] * c.K; if (double.IsNaN(L)) continue;
                nc[j]++; var cy = Dp.Cyl[j];
                if (!double.IsNaN(cy.Lmin) && !double.IsNaN(cy.Lmax))
                {
                    double tol = .01 * (cy.Lmax - cy.Lmin);
                    if (L < cy.Lmin - tol || L > cy.Lmax + tol) oob[j]++;
                }
                var rr = G.Inv(j, L);
                if (rr.Bad || double.IsNaN(rr.Ang)) { bad[j]++; continue; }
                th[j][i] = rr.Ang; src[j] |= 2;
            }
        }
        return new AngleSeries { Th = th, Bad = bad, OutOfStroke = oob, CylCount = nc, Src = src };
    }

    /// <summary>각도 변환 → 링크 각도, Tip 위치, (선회 속도 적분) 선회각 신호</summary>
    public CalcResult CalcAngles()
    {
        var res = new CalcResult();
        var R = Angles(); int n = D.Count;
        string[] nm = ExcavatorSignals.LinkEng, kor = ExcavatorSignals.LinkKor;
        if (!R.Src.Any(x => x != 0)) { res.Message = "각도 열 또는 실린더 길이 열이 지정되지 않았거나 모두 비어 있습니다."; res.Warn = true; return res; }
        var tx = Fill(n); var ty = Fill(n); int nt = 0;
        for (int i = 0; i < n; i++)
        {
            double a = R.Th[0][i], b = R.Th[1][i], c = R.Th[2][i];
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsNaN(c)) continue;
            double a2 = a + b, a3 = a2 + c;
            tx[i] = G.P.X + G.Lb * Math.Cos(a) + G.La * Math.Cos(a2) + G.Lk * Math.Cos(a3);
            ty[i] = G.P.Y + G.Lb * Math.Sin(a) + G.La * Math.Sin(a2) + G.Lk * Math.Sin(a3);
            nt++;
        }
        var units = S.AngleOut == "both" ? new[] { "deg", "rad" } : new[] { S.AngleOut == "rad" ? "rad" : "deg" };
        var names = new List<string>();
        for (int j = 0; j < 3; j++)
        {
            if (R.Src[j] == 0) continue;
            foreach (var u in units)
            {
                var k = nm[j] + "_Ang_calc_" + u;
                res.Outputs.Add(new OutSignal(k, R.Th[j].Select(x => u == "deg" ? x / Rad : x).ToArray(), u));
                names.Add(k);
            }
        }
        if (nt > 0) { res.Outputs.Add(new("Tip_X_m", tx, "m")); res.Outputs.Add(new("Tip_Y_m", ty, "m")); }
        bool swInt = false;
        var sc = Col(S.SwingSpeed);
        if (!(S.SwingAngle != "" && S.SwingAngleUnit != "" && D.Signals.ContainsKey(S.SwingAngle)) && sc != null)
        {
            double u = S.SwingSpeedUnit == "rpm" ? 360.0 / 60 : S.SwingSpeedUnit == "rps" ? 1 / Rad : 1;
            var ts = TimeSec(); var a = new double[n]; double acc = 0;
            for (int i = 1; i < n; i++)
            {
                double w0 = sc[i - 1], w1 = sc[i], dt = ts[i] - ts[i - 1];
                if (!double.IsNaN(w0) && !double.IsNaN(w1) && dt > 0) acc += (w0 + w1) / 2 * u * dt;
                a[i] = acc;
            }
            foreach (var uu in units)
            {
                var k = "Swing_Ang_calc_" + uu;
                res.Outputs.Add(new OutSignal(k, uu == "deg" ? a : a.Select(x => x * Rad).ToArray(), uu));
                names.Add(k);
            }
            swInt = true;
        }
        var cn = names.Where(k => k.EndsWith("_" + units[0])).ToArray();
        if (cn.Length > 0) res.Charts.Add(cn);
        if (nt > 0) res.XyCharts.Add(("Tip_X_m", "Tip_Y_m"));
        var w = new List<string>();
        for (int j = 0; j < 3; j++)
        {
            if (R.CylCount[j] > 0 && R.OutOfStroke[j] > 0) w.Add(kor[j] + " 실린더 행정(Lmin~Lmax) 밖 " + R.OutOfStroke[j] + "개");
            if (R.Bad[j] > 0) w.Add(kor[j] + " 기구학 해 없음 " + R.Bad[j] + "개");
        }
        res.Message = "✓ 각도 변환 완료 → 신호 목록·차트에 추가: " + string.Join(", ", names) + (nt > 0 ? ", Tip_X_m, Tip_Y_m" : "")
            + (swInt ? " (선회각 = 선회 속도 적분, 시작=0°)" : "") + (w.Count > 0 ? "\n⚠ " + string.Join(" / ", w) + " — 단위·파라미터 확인" : "");
        res.Warn = w.Count > 0;
        return res;
    }

    // ================= 동역학 =================

    /// <summary>압력 열의 Pa 배율 (열 단위 우선, 없으면 파라미터 pUnit)</summary>
    public double PressureFactor(string key) => Units.PressureToPa(D.Units.GetValueOrDefault(S.P(key))) ?? Dp.PU;

    /// <summary>평활 후 각도·각속도·각가속도</summary>
    public (AngleSeries R, double[] Ts, int N, double[][] Th, double[][] W, double[][] Al) Smoothed()
    {
        var R = Angles(); var ts = TimeSec(); int N = WinN(ts);
        var ths = R.Th.Select(a => Dynamics.SmoothMA(a, N)).ToArray();
        var wv = ths.Select(a => Dynamics.SmoothMA(Dynamics.Ddt(a, ts), N)).ToArray();
        var al = wv.Select(a => Dynamics.Ddt(a, ts)).ToArray();
        return (R, ts, N, ths, wv, al);
    }

    string? DynLack()
    {
        var lack = new List<string>();
        if (!Dp.M.All(double.IsFinite)) lack.Add("M2~M4");
        if (!Dp.I.All(a => double.IsFinite(a[2]))) lack.Add("I2zz~I4zz");
        return lack.Count > 0 ? string.Join(", ", lack) : null;
    }

    /// <summary>동역학 계산: 실린더 추력·구동/관성 토크, 선회 토크, Tip 외력</summary>
    public CalcResult CalcDynamics()
    {
        var res = new CalcResult();
        var (R, ts, N, ths, wv, al) = Smoothed(); int n = D.Count;
        if (!R.Src.All(x => x != 0)) { res.Message = "붐·암·버켓 세 링크의 각도(또는 실린더 길이)가 모두 필요합니다."; res.Warn = true; return res; }
        if (DynLack() is { } lk) { res.Message = "동역학에 필요한 값이 없습니다: " + lk + " (파라미터 편집)"; res.Warn = true; return res; }
        var cy = new (double[] H, double[] R, double FH, double FR)?[3];
        for (int j = 0; j < 3; j++)
        {
            var ks = ExcavatorSignals.CylPressureKeys[j]; var c = Dp.Cyl[j];
            double[]? h = Col(S.P(ks[0])), r = Col(S.P(ks[1]));
            if (h == null || r == null || !(c.AH > 0) || !(c.AR > 0)) continue;
            double fh = PressureFactor(ks[0]), fr = PressureFactor(ks[1]);
            cy[j] = S.Swap[j] ? (r, h, fr, fh) : (h, r, fh, fr);
        }
        double sgn = S.Negate ? -1 : 1;
        double[][] thr = { Fill(n), Fill(n), Fill(n) }, tqa = { Fill(n), Fill(n), Fill(n) }, tqi = { Fill(n), Fill(n), Fill(n) };
        double[] Fx = Fill(n), Fy = Fill(n), Fp = Fill(n), Jv = Fill(n), tipx = Fill(n);
        var th = new double[3]; var w = new double[3]; var a = new double[3]; var rho = new double[3];
        for (int i = 0; i < n; i++)
        {
            bool ok = true;
            for (int j = 0; j < 3; j++) { th[j] = ths[j][i]; w[j] = wv[j][i]; a[j] = al[j][i]; ok &= double.IsFinite(th[j]) && double.IsFinite(w[j]) && double.IsFinite(a[j]); }
            if (!ok) continue;
            var dr = Dynamics.Inertial(G, Dp, th, w, a, S.Gravity);
            Jv[i] = Dynamics.SwingJ(Dp, dr); tipx[i] = dr.Tip.X;
            for (int j = 0; j < 3; j++)
            {
                rho[j] = double.NaN; tqi[j][i] = dr.Tau[j] / 1e3;
                if (cy[j] is not { } q) continue;
                double h = q.H[i], r = q.R[i]; if (double.IsNaN(h) || double.IsNaN(r)) continue;
                var c = Dp.Cyl[j];
                double F = (h * q.FH * c.AH - r * q.FR * c.AR) * c.N * sgn, t = F * G.DLdTh(j, th);
                thr[j][i] = F / 1e3; tqa[j][i] = t / 1e3; rho[j] = t - dr.Tau[j];
            }
            if (rho.All(double.IsFinite))
            {
                var f = Dynamics.TipForce(dr, rho); double fs = S.ForceOnEnvironment ? -1 : 1;
                Fx[i] = fs * f.X / 1e3; Fy[i] = fs * f.Y / 1e3; Fp[i] = Math.Sqrt(f.X * f.X + f.Y * f.Y) / 1e3;
            }
        }
        var NM = ExcavatorSignals.LinkEng;
        for (int j = 0; j < 3; j++) { res.Put(NM[j] + "_Thrust_kN", thr[j], "kN"); res.Put(NM[j] + "_TqAct_kNm", tqa[j], "kNm"); res.Put(NM[j] + "_TqInert_kNm", tqi[j], "kNm"); }
        string swNote = "";
        double[]? pA = Col(S.P("pSwA")), pB = Col(S.P("pSwB")); var s = Dp.Sw;
        if (pA != null && pB != null && double.IsFinite(s.Disp) && double.IsFinite(s.Ratio) && double.IsFinite(s.Eff))
        {
            double k = s.Disp * 1e-6 / (2 * Math.PI) * s.Ratio * s.Eff / 1e3, fA = PressureFactor("pSwA"), fB = PressureFactor("pSwB");
            var swa = new double[n]; for (int i = 0; i < n; i++) swa[i] = (pA[i] * fA - pB[i] * fB) * k;
            res.Put("Swing_TqAct_kNm", swa, "kNm");
            var sc = Col(S.SwingSpeed);
            if (sc != null && double.IsFinite(s.I1))
            {
                double u = S.SwingSpeedUnit == "rpm" ? 2 * Math.PI / 60 : S.SwingSpeedUnit == "rps" ? 1 : Rad;
                var om = Dynamics.SmoothMA(sc.Select(x => x * u).ToArray(), N);
                var alp = Dynamics.SmoothMA(Dynamics.Ddt(om, ts), N);
                var jd = Dynamics.SmoothMA(Dynamics.Ddt(Dynamics.SmoothMA(Jv, N), ts), N);
                var swi = Fill(n); var ft = Fill(n);
                for (int i = 0; i < n; i++) { swi[i] = (Jv[i] * alp[i] + jd[i] * om[i]) / 1e3; if (Math.Abs(tipx[i]) > .3) ft[i] = (swi[i] - swa[i]) / tipx[i]; }
                res.Put("Swing_TqInert_kNm", swi, "kNm"); res.Put("Tip_Flat_kN", ft, "kN");
            }
            else swNote = " (선회 속도 열/I1 없음 → 선회 관성토크·횡방향 외력 생략)";
        }
        res.Put("Tip_Fx_kN", Fx, "kN"); res.Put("Tip_Fy_kN", Fy, "kN"); res.Put("Tip_Fplane_kN", Fp, "kN");
        if (res.Ok)
        {
            var tf = new[] { "Tip_Fx_kN", "Tip_Fy_kN", "Tip_Fplane_kN", "Tip_Flat_kN" }.Where(res.Has).ToArray();
            if (tf.Length > 0) res.Charts.Add(tf);
            var tq = NM.SelectMany(b => new[] { b + "_TqAct_kNm", b + "_TqInert_kNm" }).Where(res.Has).ToArray();
            if (tq.Length > 0) res.Charts.Add(tq);
        }
        var miss = new List<string>();
        for (int j = 0; j < 3; j++) if (cy[j] == null) miss.Add(ExcavatorSignals.LinkKor[j] + " 실린더 압력(H,R)·직경");
        res.Message = (res.Ok ? "✓ 동역학 계산 완료 (" + res.Outputs.Count + "개 신호 추가, 평활 창 " + N + "샘플): Tip 외력 " + (res.Has("Tip_Fx_kN") ? "계산됨" : "계산 안 됨") + swNote : "계산 가능한 항목이 없습니다.")
            + (miss.Count > 0 ? "\n⚠ 누락: " + string.Join(", ", miss) + " → 해당 실린더의 구동토크와 Tip 외력은 생략" : "");
        res.Warn = !res.Ok || miss.Count > 0;
        return res;
    }

    // ================= 실린더 속도·추력 =================

    /// <summary>실린더 속도 [m/s] (신장 +): 속도 열 → 길이 열 미분 → 각도→길이(기구학) 미분</summary>
    public (double[] V, string Src)? CylVelocity(int j)
    {
        var r = S.Links[j]; var ts = TimeSec(); int N = WinN(ts);
        if (Col(r.Vel) is { } v)
        {
            double f = ExcavatorSignals.VelToMps(r.VelUnit); if (double.IsNaN(f)) f = .001;
            return (v.Select(x => x * f).ToArray(), "② 속도 열 \"" + r.Vel + "\"");
        }
        if (Col(r.Cyl) is { } c)
        {
            double f = ExcavatorSignals.LenToM(r.CylUnit);
            return (Dynamics.Ddt(Dynamics.SmoothMA(c.Select(x => x * f).ToArray(), N), ts), "② 실린더 길이 열 \"" + r.Cyl + "\" 미분");
        }
        var R = Angles();
        if (R.Src[j] == 0) return null;
        var L = R.Th[j].Select(a => double.IsNaN(a) ? double.NaN : G.Fwd(j, a)).ToArray();
        return (Dynamics.Ddt(Dynamics.SmoothMA(L, N), ts), "② 각도 열 → 기구학 실린더 길이 → 미분");
    }

    /// <summary>실린더 추력 [kN] = (pH·AH − pR·AR)·개수 (교환·부호 옵션은 반영하지 않음, HTML과 같음)</summary>
    public double[]? CylThrustKN(int j)
    {
        var ks = ExcavatorSignals.CylPressureKeys[j]; var c = Dp.Cyl[j];
        double[]? h = Col(S.P(ks[0])), r = Col(S.P(ks[1]));
        if (h == null || r == null || !(c.AH > 0) || !(c.AR > 0)) return null;
        double fh = PressureFactor(ks[0]), fr = PressureFactor(ks[1]); int n = D.Count; var o = new double[n];
        for (int i = 0; i < n; i++) o[i] = (h[i] * fh * c.AH - r[i] * fr * c.AR) * c.N / 1e3;
        return o;
    }

    /// <summary>실린더 추력 [N] — Head↔Rod 교환·부호 반전 반영 (관성 추정용)</summary>
    public double[]? CylThrustN(int j)
    {
        var ks = ExcavatorSignals.CylPressureKeys[j]; bool sw = S.Swap[j];
        string kh = sw ? ks[1] : ks[0], kr = sw ? ks[0] : ks[1]; var c = Dp.Cyl[j];
        double[]? h = Col(S.P(kh)), r = Col(S.P(kr));
        if (h == null || r == null || !(c.AH > 0) || !(c.AR > 0)) return null;
        double fh = PressureFactor(kh), fr = PressureFactor(kr), sg = S.Negate ? -1 : 1; int n = D.Count; var o = new double[n];
        for (int i = 0; i < n; i++) o[i] = (h[i] * fh * c.AH - r[i] * fr * c.AR) * c.N * sg;
        return o;
    }

    // ================= Head/Rod 진단 =================

    /// <summary>
    /// 커서 A–B 구간(원래 시간축 값)에서 Head↔Rod 교환 조합별 Tip 외력 RMS·평균·F·v 비교.
    /// 반환: (메시지, 오류 여부)
    /// </summary>
    public (string Text, bool Error) DiagnoseHeadRod(double a, double b, Func<double, string> fmtT)
    {
        double lo = Math.Min(a, b), hi = Math.Max(a, b); int n = D.Count;
        var (R, ts, N, ths, wv, al) = Smoothed();
        if (!R.Src.All(x => x != 0)) return ("붐·암·버켓 각도(또는 실린더 길이)가 모두 필요합니다.", true);
        if (DynLack() != null) return ("동역학에 필요한 질량·관성 값이 없습니다.", true);
        var cy = new (double[] H, double[] R, double FH, double FR)[3];
        for (int j = 0; j < 3; j++)
        {
            var ks = ExcavatorSignals.CylPressureKeys[j]; var c = Dp.Cyl[j];
            double[]? h = Col(S.P(ks[0])), r = Col(S.P(ks[1]));
            if (h == null || r == null || !(c.AH > 0) || !(c.AR > 0)) return ("세 실린더의 압력(Head·Rod) 열과 직경이 모두 필요합니다.", true);
            cy[j] = (h, r, PressureFactor(ks[0]), PressureFactor(ks[1]));
        }
        var samples = new List<(int I, DynResult Res, double Vx, double Vy, double[] Dl)>();
        double[] ln = { G.Lb, G.La, G.Lk };
        for (int i = 0; i < n; i++)
        {
            if (!(D.Time[i] >= lo && D.Time[i] <= hi)) continue;
            var th = new[] { ths[0][i], ths[1][i], ths[2][i] }; var w = new[] { wv[0][i], wv[1][i], wv[2][i] }; var aa = new[] { al[0][i], al[1][i], al[2][i] };
            if (!th.Concat(w).Concat(aa).All(double.IsFinite)) continue;
            var dr = Dynamics.Inertial(G, Dp, th, w, aa, S.Gravity);
            double[] pd = { w[0], w[0] + w[1], w[0] + w[1] + w[2] }; double vx = 0, vy = 0;
            for (int k = 0; k < 3; k++) { var p = Kinematics.Rot(dr.Ph[k], ln[k], 0); vx += -p.Y * pd[k]; vy += p.X * pd[k]; }
            samples.Add((i, dr, vx, vy, new[] { G.DLdTh(0, th), G.DLdTh(1, th), G.DLdTh(2, th) }));
        }
        if (samples.Count < 10) return ("구간 안의 유효 샘플이 부족합니다 (" + samples.Count + "개).", true);
        var variants = Enumerable.Range(0, 8).Select(m => (M: m, Neg: false)).Append((M: 0, Neg: true));
        var rows = new List<(string Lab, double Rms, double Pw, double Fx, double Fy)>();
        var kor = ExcavatorSignals.LinkKor;
        foreach (var v in variants)
        {
            double s2 = 0, sfv = 0, sfx = 0, sfy = 0; int k = 0;
            foreach (var q in samples)
            {
                var rho = new double[3];
                for (int j = 0; j < 3; j++)
                {
                    var c = cy[j]; bool sw = ((v.M >> j) & 1) != 0;
                    double[] H = sw ? c.R : c.H, Rr = sw ? c.H : c.R; double fH = sw ? c.FR : c.FH, fR = sw ? c.FH : c.FR;
                    double F = (H[q.I] * fH * Dp.Cyl[j].AH - Rr[q.I] * fR * Dp.Cyl[j].AR) * Dp.Cyl[j].N * (v.Neg ? -1 : 1);
                    rho[j] = F * q.Dl[j] - q.Res.Tau[j];
                }
                var f = Dynamics.TipForce(q.Res, rho);
                if (double.IsNaN(f.X) || double.IsNaN(f.Y)) continue;
                s2 += f.X * f.X + f.Y * f.Y; sfv += f.X * q.Vx + f.Y * q.Vy; sfx += f.X; sfy += f.Y; k++;
            }
            if (k == 0) continue;
            string lab = v.Neg ? "전체 추력 부호 반전" : v.M != 0 ? string.Join("·", Enumerable.Range(0, 3).Where(j => ((v.M >> j) & 1) != 0).Select(j => kor[j])) + " Head↔Rod 교환" : "현재 설정 (교환 없음)";
            rows.Add((lab, Math.Sqrt(s2 / k) / 1e3, sfv / k / 1e3, sfx / k / 1e3, sfy / k / 1e3));
        }
        rows = rows.OrderBy(r => r.Rms).ToList();
        string f1(double x) => F(x, 1);
        var text = "구간 " + fmtT(lo) + " ~ " + fmtT(hi) + " (샘플 " + samples.Count + "개) — 조합별 Tip 외력 (RMS 오름차순)\n"
            + string.Join("\n", rows.Select((r, k) => (k + 1) + ". " + r.Lab + "\n     RMS|F| = " + f1(r.Rms) + " kN · 평균 Fx = " + f1(r.Fx) + " · 평균 Fy = " + f1(r.Fy) + " kN · 평균 F·v = " + f1(r.Pw) + " kW"))
            + "\n※ 이 구간이 공중 이동(외력≈0)이면 RMS가 가장 작은 조합이 맞는 설정입니다. 먼저 공중 구간으로 조합을 정한 뒤, 굴착 구간에서는 그 조합으로 평균 F·v < 0 (지면이 운동을 방해)인지 확인하세요. 굴착 구간 자체의 RMS는 실제 굴착력 때문에 0이 되지 않습니다.";
        return (text, false);
    }
}
