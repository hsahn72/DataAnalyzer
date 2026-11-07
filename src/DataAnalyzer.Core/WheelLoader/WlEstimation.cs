using System.Globalization;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.WheelLoader;

/// <summary>가중 최소제곱 결과 (HTML wlLS / wlIRLS)</summary>
public sealed class WlFit
{
    public required double[] X { get; init; }
    public required double[] Se { get; init; }
    /// <summary>활성(부호 제약에 걸리지 않은) 열 번호</summary>
    public required List<int> Act { get; init; }
    public double Rms0 { get; init; }
    public double Rms1 { get; init; }
    public int M { get; init; }
    /// <summary>최종 가중치 (IRLS)</summary>
    public double[] W { get; set; } = Array.Empty<double>();
    public int It { get; set; }
}

/// <summary>압력 안정도 가중치와 평가 신호</summary>
public sealed class WlStab
{
    public required double[] W { get; init; }
    public required double[][] Hf { get; init; }
    public required double[][] Zl { get; init; }
    public required double[] Z { get; init; }
}

/// <summary>Bucket 추정 결과 (값, 표준오차). 순서: X, Y, J, c, p1, p2, v, d, jc, jf</summary>
public sealed class WlBktEst
{
    public double Eps { get; init; }
    public required double[] X { get; init; }
    public required double[] Se { get; init; }
    /// <summary>Static 추정값 (X, Y, -, c, p1, p2, v, d, jc, jf)</summary>
    public required double[] St { get; init; }
    public int N { get; init; }
    public double Rms0 { get; init; }
    public double Rms1 { get; init; }
    public required bool[] On { get; init; }
}

/// <summary>Arm 추정 결과: P, Q, J(=I3zz+M3|C3|²), c, p, v 와 표준오차, 총 관성 Jt</summary>
public sealed class WlArmEst
{
    public double Jt { get; init; }
    public required double[] X { get; init; }
    public required double[] Se { get; init; }
    public int N { get; init; }
    public double Rms0 { get; init; }
    public double Rms1 { get; init; }
}

/// <summary>추정 옵션·상태 (HTML WL.rg, WL.dirInv, WL.est)</summary>
public sealed class WlEstState
{
    /// <summary>파라미터 키 → Regression 대상 여부. 없으면 uF4d·uFjc·uFjf는 꺼짐, 나머지는 켜짐.</summary>
    public Dictionary<string, bool> RegOn { get; } = new();
    /// <summary>각도 증가 = Down(Arm) / Dump(Bucket)</summary>
    public bool DirInv { get; set; }
    public WlBktEst? Bkt { get; set; }
    public WlArmEst? Arm { get; set; }
    public bool IsOn(string k) => RegOn.TryGetValue(k, out var v) ? v : k is not ("uF4d" or "uFjc" or "uFjf");
}

/// <summary>
/// 휠로더 Dynamics Parameter Estimation · Weighing · 구간 Labeling (HTML wlObs / wlStabW / wlLS / wlIRLS / wlDirArr / wlSelMask /
/// wlEstBkt / wlEstArm / wlEstSig / wlWeighOn / wlLabelRun).
/// </summary>
public static class WlEstimation
{
    const double Rad = Kinematics.Rad;
    public static readonly string[] ObsKeys = { "H1", "H2", "H3", "H4", "H5", "M1", "M2", "M3", "M4", "M5", "M6", "N1", "N2", "N3", "Haaf1", "Haaf2", "Haaf3", "Haaf4", "Hkaf1", "Hkaf2", "Hkaf3", "Hkaf4", "Hkkf1", "Hkkf2", "Hkkf3", "Hkkf4" };
    static readonly string[] ObsExtra = { "N1s", "N2s", "J31", "J32", "J4", "Hjc", "Hjf", "Fj" };
    /// <summary>Bucket 회귀 파라미터 (사용값 키, 관측 열)</summary>
    public static readonly (string Key, string Col)[] BktParams =
        { ("uM4C4x", "N1"), ("uM4C4y", "N2"), ("uJ4", "N3"), ("uF4c", "Hkkf1"), ("uF4p1", "Hkkf2"), ("uF4p2", "Hkkf3"), ("uF4v", "Hkkf4"), ("uF4d", "J4"), ("uFjc", "Hjc"), ("uFjf", "Hjf") };

    static double[] Fill(int n) { var a = new double[n]; Array.Fill(a, double.NaN); return a; }
    static bool Fin(double x) => !double.IsNaN(x);
    static double Num(KinParams k, string key) => k[key];
    static double Opt(KinParams k, string key, double d) { var v = k[key]; return double.IsFinite(v) ? v : d; }
    static int JsRound(double x) => (int)Math.Floor(x + .5);
    static double P5(double v) => double.IsFinite(v) ? double.Parse(v.ToString("E4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : v;

    /// <summary>각도 Range [rad] (비어 있으면 ±∞)</summary>
    public static (double Lo, double Hi) AngRange(KinParams k, string a, string b)
    {
        double lo = k[a], hi = k[b];
        return (double.IsFinite(lo) ? lo * Rad : double.NegativeInfinity, double.IsFinite(hi) ? hi * Rad : double.PositiveInfinity);
    }

    /// <summary>Coulomb 마찰 tanh 전이 속도 ε [m/s]</summary>
    public static double EpsOf(KinParams k, double[] v, Func<int, bool>? pred)
    {
        double u = Opt(k, "VEps", double.NaN) * 1e-3;
        if (u > 0) return u;
        var a = new List<double>();
        for (int i = 0; i < v.Length; i++) if (Fin(v[i]) && (pred == null || pred(i))) a.Add(Math.Abs(v[i]));
        if (a.Count == 0) return .005;
        a.Sort();
        return Math.Max(1e-5, .05 * a[(int)Math.Floor(a.Count * .95)]);
    }

    /// <summary>마찰력 [N]. Bucket(cy.Tot): (c + p1·pH + p2·pR)·tanh(v/ε) + cv·v / Arm: 개수×(...)</summary>
    public static double Fric(double[] c, double v, WlCyl cy, int i, double eps = .005)
    {
        double C(int k) => k < c.Length ? c[k] : 0;
        double Cz(int k) { double v = C(k); return double.IsNaN(v) ? 0 : v; }
        if (cy.Tot) return (C(0) + Cz(2) * cy.HP[i] / 1e5 + Cz(3) * cy.RP[i] / 1e5) * Math.Tanh(v / eps) + C(1) * v;
        return cy.N * ((C(0) * 1e3 + C(2) * 1e-3 * (cy.HP[i] * cy.Dt + cy.RP[i] * (cy.Dt + cy.Dr))) * Math.Tanh(v / eps) + C(1) * 1e3 * v);
    }
    static double[] CfB(KinParams k) => new[] { "uF4c", "uF4v", "uF4p1", "uF4p2" }.Select(x => double.IsFinite(k[x]) ? k[x] : 0).ToArray();

    /// <summary>관측 Vector (H·M·N·Haaf·Hkaf·Hkkf) + Jacobian + 핀 반력 마찰 열</summary>
    public static Dictionary<string, double[]> Obs(WlBase b, WlKin kn, double e3, double e4)
    {
        var g = b.G; int n = b.N; var KnB = WlGeom.NoBody(b.KK); double L3 = g.L3, G = 9.81;
        var O = new Dictionary<string, double[]>(); foreach (var k in ObsKeys.Concat(ObsExtra)) O[k] = Fill(n);
        double ew;
        {
            var a = new List<double>(); for (int i = 0; i < n; i++) if (Fin(kn.W4[i])) a.Add(Math.Abs(kn.W4[i]));
            a.Sort(); ew = Math.Max(1e-6, .05 * (a.Count > 0 ? a[(int)Math.Floor(a.Count * .95)] : .01));
        }
        var cA = b.CA; var cB = b.CB;
        for (int i = 0; i < n; i++)
        {
            double a = b.T2[i], bb = b.T3[i];
            if (!(Fin(a) && Fin(bb) && Fin(kn.W3[i]) && Fin(kn.W4[i]) && Fin(kn.Al3[i]) && Fin(kn.Al4[i]))) continue;
            var dl = g.DL(a, bb); double T34 = a + bb, W3 = kn.W3[i], W34 = W3 + kn.W4[i], A3 = kn.Al3[i], A34 = A3 + kn.Al4[i];
            double c4 = Math.Cos(bb), s4 = Math.Sin(bb), c3 = Math.Cos(a), s3 = Math.Sin(a), c34 = Math.Cos(T34), s34 = Math.Sin(T34), sq = W34 * W34;
            double sg3 = Math.Tanh(b.VA[i] / e3), sg4 = Math.Tanh(b.VB[i] / e4);
            double pAH = cA != null ? cA.HP[i] / 1e5 : double.NaN, pAR = cA != null ? cA.RP[i] / 1e5 : double.NaN, pBH = cB != null ? cB.HP[i] / 1e5 : double.NaN, pBR = cB != null ? cB.RP[i] / 1e5 : double.NaN;
            double J31 = dl.DLa, J32 = dl.DLb2, J4 = dl.DLb3, CV3 = b.VA[i], CV4 = b.VB[i];
            O["J31"][i] = J31; O["J32"][i] = J32; O["J4"][i] = J4;
            var ps = g.Pose(a, bb);
            if (!ps.Bad && b.FB != null && Fin(b.FB[i]) && g.PinForces(KnB, ps, b.FB[i]) is { } fp)
            {
                double Fj = 0; foreach (var k in WlPinForces.Keys) { var f = fp[k]; Fj += Math.Sqrt(f.X * f.X + f.Y * f.Y); }
                double sw = Math.Tanh(kn.W4[i] / ew); O["Fj"][i] = Fj; O["Hjc"][i] = sw; O["Hjf"][i] = Fj * sw;
            }
            O["H1"][i] = L3 * (A34 * c4 - sq * s4); O["H2"][i] = L3 * (-A34 * s4 - sq * c4); O["H3"][i] = G * c3; O["H4"][i] = -G * s3; O["H5"][i] = A3;
            O["M1"][i] = G * c34 + L3 * A3 * c4 + L3 * W3 * W3 * s4 + L3 * A34 * c4 - L3 * sq * s4; O["M2"][i] = -G * s34 - L3 * A3 * s4 + L3 * W3 * W3 * c4 - L3 * A34 * s4 - L3 * sq * c4;
            O["M3"][i] = G * c3; O["M4"][i] = -G * s3; O["M5"][i] = A3; O["M6"][i] = A34;
            O["N1"][i] = G * c34 + L3 * A3 * c4 + L3 * W3 * W3 * s4; O["N2"][i] = -G * s34 - L3 * A3 * s4 + L3 * W3 * W3 * c4; O["N3"][i] = A34; O["N1s"][i] = G * c34; O["N2s"][i] = -G * s34;
            O["Haaf1"][i] = J31 * sg3; O["Haaf2"][i] = J31 * sg3 * pAH; O["Haaf3"][i] = J31 * sg3 * pAR; O["Haaf4"][i] = J31 * CV3;
            O["Hkaf1"][i] = J32 * sg4; O["Hkaf2"][i] = J32 * sg4 * pBH; O["Hkaf3"][i] = J32 * sg4 * pBR; O["Hkaf4"][i] = J32 * CV4;
            O["Hkkf1"][i] = J4 * sg4; O["Hkkf2"][i] = J4 * sg4 * pBH; O["Hkkf3"][i] = J4 * sg4 * pBR; O["Hkkf4"][i] = J4 * CV4;
        }
        return O;
    }

    /// <summary>관측 Vector를 결과 신호로 (이름이 원본과 겹치면 WL_ 접두)</summary>
    public static void PutObs(CalcResult res, Dictionary<string, double[]> O, ICollection<string> existing)
    {
        foreach (var k in ObsKeys.Concat(new[] { "J31", "J32", "J4", "Hjc", "Hjf", "Fj" }))
            if (O[k].Any(double.IsFinite)) res.Outputs.Add(new OutSignal(existing.Contains(k) ? "WL_" + k : k, O[k], ""));
    }

    /// <summary>압력 안정도 가중치: 빠른 압력 진동(고주파 성분 국소 RMS)이 큰 구간일수록 가중치↓</summary>
    public static WlStab StabW(double[][] cys, double[] ts, double win, List<int> ix)
    {
        int n = ts.Length; var d = new List<double>();
        for (int i = 1; i < n; i++) { double x = ts[i] - ts[i - 1]; if (x > 0) d.Add(x); }
        d.Sort(); double dt = d.Count > 0 ? d[d.Count >> 1] : .01; int n1 = Math.Max(3, JsRound(.1 / dt)), nw = Math.Max(5, JsRound(win / dt));
        double Med(double[] a) { var v = ix.Select(i => a[i]).Where(double.IsFinite).OrderBy(x => x).ToList(); return v.Count > 0 ? v[v.Count >> 1] : double.NaN; }
        var hfs = new List<double[]>(); var loc = new List<double[]>();
        foreach (var p in cys)
        {
            var m = Dynamics.SmoothMA(p, n1); var hf = new double[n]; for (int i = 0; i < n; i++) hf[i] = p[i] - m[i]; hfs.Add(hf);
            var l = Dynamics.SmoothMA(hf.Select(v => v * v).ToArray(), nw).Select(Math.Sqrt).ToArray();
            double md = Med(l); if (double.IsNaN(md) || md == 0) md = 0;
            double mp = Math.Abs(Med(p)); if (double.IsNaN(mp) || mp == 0) mp = 1;
            double fl = 1e-6 * mp, den = Math.Max(Math.Max(md, fl), 1e-12);
            loc.Add(l.Select(v => v / den).ToArray());
        }
        var w = Fill(n); var zz = Fill(n);
        for (int i = 0; i < n; i++)
        {
            double z = 0; foreach (var l in loc) if (l[i] > z) z = l[i];
            w[i] = 1 / (1 + (z / 2) * (z / 2)); zz[i] = z;
        }
        return new WlStab { W = w, Hf = hfs.ToArray(), Zl = loc.ToArray(), Z = zz };
    }

    /// <summary>대칭 선형계 풀이 (부분 피벗 Gauss-Jordan, 특이하면 null)</summary>
    public static double[]? SolveSym(double[][] M, double[] r)
    {
        int n = r.Length; var A = M.Select((row, i) => row.Append(r[i]).ToArray()).ToArray();
        for (int c = 0; c < n; c++)
        {
            int p = c; for (int i = c + 1; i < n; i++) if (Math.Abs(A[i][c]) > Math.Abs(A[p][c])) p = i;
            if (Math.Abs(A[p][c]) < 1e-18) return null;
            (A[c], A[p]) = (A[p], A[c]);
            for (int i = 0; i < n; i++) if (i != c) { double f = A[i][c] / A[c][c]; for (int j = c; j <= n; j++) A[i][j] -= f * A[c][j]; }
        }
        return A.Select((row, i) => row[n] / row[i]).ToArray();
    }

    /// <summary>열 스케일링 + 부호 제약(nn: 음수 금지) 최소제곱 (Active set)</summary>
    public static WlFit? LS(List<double[]> R, List<double> y, bool[] nn)
    {
        int m = R.Count, p = R[0].Length; var sc = new double[p];
        foreach (var r in R) for (int j = 0; j < p; j++) sc[j] += r[j] * r[j];
        for (int j = 0; j < p; j++) { sc[j] = Math.Sqrt(sc[j]); if (sc[j] == 0 || double.IsNaN(sc[j])) sc[j] = 1; }
        var M = new double[p][]; for (int a = 0; a < p; a++) M[a] = new double[p];
        var rv = new double[p];
        for (int i = 0; i < m; i++)
        {
            var row = R[i];
            for (int a = 0; a < p; a++) { double ra = row[a] / sc[a]; rv[a] += ra * y[i]; for (int c = 0; c < p; c++) M[a][c] += ra * row[c] / sc[c]; }
        }
        double tr = 0; for (int i = 0; i < p; i++) tr += M[i][i]; if (tr == 0 || double.IsNaN(tr)) tr = 1;
        for (int i = 0; i < p; i++) M[i][i] += 1e-10 * tr;
        var act = Enumerable.Range(0, p).ToList(); var z = new double[p];
        for (int it = 0; it < p + 2; it++)
        {
            var sub = act.Select(i => act.Select(j => M[i][j]).ToArray()).ToArray();
            var sol = SolveSym(sub, act.Select(i => rv[i]).ToArray());
            if (sol == null) return null;
            z = new double[p]; for (int k = 0; k < act.Count; k++) z[act[k]] = sol[k];
            var neg = act.Where(i => nn[i] && z[i] < 0).ToList();
            if (neg.Count == 0) break;
            act = act.Where(i => !neg.Contains(i)).ToList();
            if (act.Count == 0) break;
        }
        var x = z.Select((v, j) => v / sc[j]).ToArray(); double s2 = 0, s0 = 0;
        for (int i = 0; i < m; i++) { double f = 0; for (int j = 0; j < p; j++) f += R[i][j] * x[j]; double e = y[i] - f; s2 += e * e; s0 += y[i] * y[i]; }
        double sig2 = s2 / Math.Max(1, m - act.Count); var se = new double[p]; Array.Fill(se, double.NaN);
        if (act.Count > 0)
        {
            var sub = act.Select(i => act.Select(j => M[i][j]).ToArray()).ToArray();
            for (int k = 0; k < act.Count; k++)
            {
                var col = SolveSym(sub, act.Select((_, q) => q == k ? 1.0 : 0).ToArray());
                if (col != null && col[k] > 0) se[act[k]] = Math.Sqrt(sig2 * col[k]) / sc[act[k]];
            }
        }
        return new WlFit { X = x, Se = se, Act = act, Rms0 = Math.Sqrt(s0 / m), Rms1 = Math.Sqrt(s2 / m), M = m };
    }

    /// <summary>가중 + IRLS(Tukey biweight, MAD 스케일) 최소제곱</summary>
    public static WlFit? IRLS(List<double[]> R, List<double> y, bool[] nn, double[]? w0, int it)
    {
        int m = R.Count; var bas = w0 != null ? (double[])w0.Clone() : Enumerable.Repeat(1.0, m).ToArray(); var rw = Enumerable.Repeat(1.0, m).ToArray();
        WlFit? fit = null; int used = 0;
        for (int k = 0; k <= it; k++)
        {
            var sw = bas.Select((v, i) => Math.Sqrt(Math.Max(v * rw[i], 1e-12))).ToArray();
            var f = LS(R.Select((r, i) => r.Select(v => v * sw[i]).ToArray()).ToList(), y.Select((v, i) => v * sw[i]).ToList(), nn);
            if (f == null) return fit;
            double dx = 1;
            if (fit != null)
            {
                dx = double.NegativeInfinity;
                for (int j = 0; j < f.X.Length; j++) { double q = Math.Abs(f.X[j] - fit.X[j]) / (Math.Abs(f.X[j]) + 1e-9); if (double.IsNaN(q)) { dx = double.NaN; break; } if (q > dx) dx = q; }
            }
            fit = f; used = k; fit.W = bas.Select((v, i) => v * rw[i]).ToArray();
            if (k == it || (k > 0 && dx < 1e-5)) break;
            var r = y.Select((v, i) => { double s = 0; for (int j = 0; j < f.X.Length; j++) s += R[i][j] * f.X[j]; return (v - s) * Math.Sqrt(bas[i]); }).ToArray();
            var a = r.Select(Math.Abs).OrderBy(x => x).ToArray(); double sm = 1.4826 * a[a.Length >> 1];
            if (!(sm > 1e-12)) break;
            double c = 4.685 * sm;
            rw = r.Select(v => { double u = Math.Abs(v) / c; return u < 1 ? (1 - u * u) * (1 - u * u) : 1e-4; }).ToArray();
        }
        fit!.It = used;
        return fit;
    }

    /// <summary>움직임 방향 (+1/−1/0). 짧은(0.2 s 미만) 방향 흔들림은 이전 방향으로 흡수</summary>
    public static sbyte[] DirArr(double[] w, bool[] mask, double[] ts)
    {
        int n = w.Length; var a = new List<double>(); for (int i = 0; i < n; i++) if (mask[i]) a.Add(Math.Abs(w[i]));
        a.Sort(); double dead = a.Count > 0 ? Math.Max(1e-6, .05 * a[(int)Math.Floor(a.Count * .95)]) : double.NaN;
        if (a.Count > 0 && double.IsNaN(a[(int)Math.Floor(a.Count * .95)])) dead = double.NaN;
        var dir = new sbyte[n]; for (int i = 0; i < n; i++) dir[i] = (sbyte)(!mask[i] ? 0 : w[i] > dead ? 1 : w[i] < -dead ? -1 : 0);
        var dts = new List<double>(); for (int i = 1; i < n; i++) { double d = ts[i] - ts[i - 1]; if (d > 0) dts.Add(d); }
        dts.Sort(); double dt = dts.Count > 0 ? dts[dts.Count >> 1] : .01; int minN = Math.Max(2, JsRound(.2 / dt));
        for (int pass = 0; pass < 2; pass++)
        {
            int i = 0; sbyte prev = 0;
            while (i < n)
            {
                int j = i; while (j < n && dir[j] == dir[i] && mask[j] == mask[i]) j++;
                if (mask[i] && dir[i] != 0 && j - i < minN) { for (int k = i; k < j; k++) dir[k] = prev; }
                else if (mask[i] && dir[i] != 0) prev = dir[i];
                i = j;
            }
        }
        return dir;
    }

    /// <summary>방향(Crowd·Up / Dump·Down) + 각도 구간으로 회귀 샘플 선별 (null = 선별 안 함)</summary>
    public static bool[]? SelMask(bool bkt, WlBase b, WlKin kn, KinParams K, bool dirInv)
    {
        string p = bkt ? "Bk" : "Ar";
        if (K.Str(p + "Sel", "off") != "on") return null;
        var ang = bkt ? b.T3 : b.T2; var w = bkt ? kn.W4 : kn.W3; int n = ang.Length;
        var (lo, hi) = bkt ? AngRange(K, "T4min", "T4max") : AngRange(K, "T3min", "T3max");
        var mask = new bool[n]; for (int i = 0; i < n; i++) mask[i] = Fin(ang[i]) && Fin(w[i]) && ang[i] >= lo && ang[i] <= hi;
        var dir = DirArr(w, mask, kn.Ts); int sg = dirInv ? -1 : 1;
        (double, double) Rg(string a, string bb) { double x = K[p + a], y = K[p + bb]; return (double.IsFinite(x) ? x * Rad : double.NegativeInfinity, double.IsFinite(y) ? y * Rad : double.PositiveInfinity); }
        var U = Rg(bkt ? "C0" : "U0", bkt ? "C1" : "U1"); var Dn = Rg("D0", "D1"); var o = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            double a = ang[i]; bool up = dir[i] * sg > 0, dn = dir[i] * sg < 0;
            if ((up && a >= U.Item1 && a <= U.Item2) || (dn && a >= Dn.Item1 && a <= Dn.Item2)) o[i] = true;
        }
        return o;
    }

    static double[] UseSignal(bool bkt, bool[]? sm, WlBase b, KinParams K)
    {
        var ang = bkt ? b.T3 : b.T2; var (lo, hi) = bkt ? AngRange(K, "T4min", "T4max") : AngRange(K, "T3min", "T3max"); var o = Fill(ang.Length);
        for (int i = 0; i < ang.Length; i++) if (Fin(ang[i]) && ang[i] >= lo && ang[i] <= hi) o[i] = sm != null ? (sm[i] ? 1 : 0) : 1;
        return o;
    }

    static void PutStab(CalcResult res, WlStab W)
    {
        res.Outputs.Add(new("WL_Bkt_pHead_HF_bar", W.Hf[0].Select(v => v / 1e5).ToArray(), "bar"));
        res.Outputs.Add(new("WL_Bkt_pRod_HF_bar", W.Hf[1].Select(v => v / 1e5).ToArray(), "bar"));
        res.Outputs.Add(new("WL_Bkt_pStab_Head", W.Zl[0], "")); res.Outputs.Add(new("WL_Bkt_pStab_Rod", W.Zl[1], ""));
        res.Outputs.Add(new("WL_Bkt_pStab_Idx", W.Z, "")); res.Outputs.Add(new("WL_Bkt_pStab_Weight", W.W, ""));
    }

    /// <summary>
    /// e-1 Bucket Parameter Estimation. 성공하면 K의 사용값(uM4C4x …)을 갱신하고 state.Bkt에 결과를 둔다.
    /// existing = 데이터에 이미 있는 신호 이름 (관측 Vector 이름 충돌 확인용).
    /// </summary>
    public static CalcResult EstimateBucket(Data.DataSet d, KinParams K, WlSignals s, WlEstState st)
    {
        var res = new CalcResult();
        var (b, err) = WlAnalysis.Base(d, K, s);
        if (b == null) { res.Message = err!; res.Warn = true; return res; }
        var KK = b.KK; int n = b.N; var FB = b.FB; var CB = b.CB;
        if (FB == null || CB == null) { res.Message = "Bucket 실린더 압력(Head/Rod 열)과 Tube/Rod 직경이 필요합니다."; res.Warn = true; return res; }
        var T = WlAnalysis.TqAll(b); var kn = WlAnalysis.Kin(d, s, b); var (lo, hi) = AngRange(KK, "T4min", "T4max");
        bool stM = KK.Str("BkMode") == "static"; var SM = SelMask(true, b, kn, KK, st.DirInv);
        bool InR(int i) { double x = b.T3[i]; return Fin(x) && x >= lo && x <= hi && (SM == null || SM[i]) && Fin(b.VB[i]); }
        double epsU = EpsOf(KK, b.VB, InR); var O = Obs(b, kn, EpsOf(KK, b.VA, _ => true), epsU);
        var PK = BktParams; bool[] NN = KK.Str("BkNN") == "free" ? new bool[10] : new[] { false, false, true, true, true, true, true, false, true, true };
        var on = PK.Select((p, j) => st.IsOn(p.Key) && !(stM && j == 2)).ToArray();
        var fixv = PK.Select(p => double.IsFinite(KK[p.Key]) ? KK[p.Key] : 0).ToArray();
        var act = Enumerable.Range(0, 10).Where(j => on[j]).ToList(); var actS = new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }.Where(j => on[j]).ToList();
        if (act.Count == 0) { res.Message = "Regression 대상 Parameter를 하나 이상 체크하세요 (표의 체크박스)."; res.Warn = true; return res; }
        var rmask = new bool[n]; for (int i = 0; i < n; i++) rmask[i] = Fin(b.T3[i]) && b.T3[i] >= lo && b.T3[i] <= hi;
        var DIR = DirArr(kn.W4, rmask, kn.Ts); int sgn = st.DirInv ? -1 : 1;
        var yr = Fill(n); var cd = PK.Select(_ => Fill(n)).ToArray(); var cs = new[] { Fill(n), Fill(n) };
        for (int i = 0; i < n; i++)
        {
            if (!(InR(i) && Fin(T["Tq4_Eq"][i]))) continue;
            if (!PK.All(p => Fin(O[p.Col][i])) || !(Fin(O["N1s"][i]) && Fin(O["N2s"][i]))) continue;
            yr[i] = T["Tq4_Eq"][i] * 1e3; for (int j = 0; j < 10; j++) cd[j][i] = O[PK[j].Col][i];
            cs[0][i] = O["N1s"][i]; cs[1][i] = O["N2s"][i];
        }
        var seg = new int[n]; Array.Fill(seg, -1);
        { int sid = 0; bool pOK = false; int pD = 0; for (int i = 0; i < n; i++) { bool ok = Fin(yr[i]); if (ok) { if (!pOK || DIR[i] != pD) sid++; seg[i] = sid; } pOK = ok; pD = DIR[i]; } }
        double[] F(double[] a)
        {
            var o = Fill(n); int i = 0;
            while (i < n)
            {
                if (seg[i] < 0) { i++; continue; }
                int j = i; while (j < n && seg[j] == seg[i]) j++;
                var sm = Dynamics.SmoothMA(a[i..j], kn.N); Array.Copy(sm, 0, o, i, sm.Length); i = j;
            }
            return o;
        }
        var Fy = F(yr); var Fs = new[] { F(cs[0]), F(cs[1]) };
        var Fd = cd.Select((c, j) =>
        {
            if (j >= 3) return F(c);
            var o = Fill(n); for (int i = 0; i < n; i++) if (Fin(c[i])) o[i] = j < 2 ? Fs[j][i] + (c[i] - cs[j][i]) : c[i];
            return o;
        }).ToArray();
        double[] Fsj(int j) => j < 2 ? Fs[j] : Fd[j];
        var R = new List<double[]>(); var y = new List<double>(); var RS = new List<double[]>(); var yS = new List<double>(); var ix = new List<int>(); var okv = new List<double>();
        for (int i = 0; i < n; i++)
        {
            if (!(Fin(Fy[i]) && Fd.All(c => Fin(c[i])) && Fin(Fs[0][i]) && Fin(Fs[1][i]))) continue;
            double fx = 0, fS = 0;
            for (int j = 0; j < 10; j++)
            {
                if (!on[j] && !(stM && j == 2)) fx += fixv[j] * (stM ? Fsj(j) : Fd[j])[i];
                if (!on[j] && j != 2) fS += fixv[j] * Fsj(j)[i];
            }
            R.Add(act.Select(j => (stM ? Fsj(j) : Fd[j])[i]).ToArray()); y.Add(Fy[i] - fx);
            RS.Add(actS.Select(j => Fsj(j)[i]).ToArray()); yS.Add(Fy[i] - fS); ix.Add(i); okv.Add(Fy[i]);
        }
        if (R.Count < 30)
        {
            res.Message = "추정에 쓸 유효 샘플이 부족합니다 (" + R.Count + "개 / 구간 입력한 Range, T4 Range " + (double.IsFinite(lo) || double.IsFinite(hi) ? "적용" : "미지정") + "). 각도·압력·시간 열, 직경, T4 Range를 확인하세요.";
            res.Warn = true; return res;
        }
        int vn = ix.Count, nC = 0, nD = 0; double vmn = double.PositiveInfinity, vmx = double.NegativeInfinity;
        foreach (var i in ix) { double x = b.T3[i]; vmn = Math.Min(vmn, x); vmx = Math.Max(vmx, x); if (DIR[i] * sgn > 0) nC++; else if (DIR[i] * sgn < 0) nD++; }
        { var rg = Fill(n); for (int i = 0; i < n; i++) if (rmask[i]) rg[i] = 0; foreach (var i in ix) rg[i] = 1; res.Outputs.Add(new("WL_Bkt_Reg", rg, "")); }
        double winS = Opt(KK, "WTwin", .5); int itN = Math.Max(0, JsRound(Opt(KK, "IRLSn", 20)));
        var Wst = winS > 0 ? StabW(new[] { CB.HP, CB.RP }, kn.Ts, winS, ix) : null; var wv = Wst != null ? ix.Select(i => Wst.W[i]).ToArray() : null;
        var fit = IRLS(R, y, act.Select(j => NN[j]).ToArray(), wv, itN);
        if (fit == null) { res.Outputs.Clear(); res.Message = "추정 실패: 움직임이 부족하거나 열이 서로 종속입니다."; res.Warn = true; return res; }
        var xs = new double[10]; var se = new double[10]; Array.Fill(xs, double.NaN); Array.Fill(se, double.NaN);
        for (int k = 0; k < act.Count; k++) { xs[act[k]] = fit.X[k]; se[act[k]] = fit.Se[k]; }
        var clamp = act.Where((j, k) => NN[j] && !fit.Act.Contains(k)).ToList();
        WlFit? fitF = clamp.Count > 0 ? IRLS(R, y, new bool[act.Count], wv, itN) : null;
        WlFit? fitS = stM ? fit : actS.Count > 0 ? IRLS(RS, yS, actS.Select(j => NN[j]).ToArray(), wv, itN) : null;
        double StV(int j) { int k = actS.IndexOf(j); return fitS != null && k >= 0 ? fitS.X[k] : fixv[j]; }
        double u0 = 0, u1 = 0, wsum = 0; int wlow = 0;
        for (int i = 0; i < R.Count; i++) { double f = 0; for (int j = 0; j < R[i].Length; j++) f += R[i][j] * fit.X[j]; double e = y[i] - f; u0 += okv[i] * okv[i]; u1 += e * e; wsum += fit.W[i]; if (fit.W[i] < .1) wlow++; }
        double rmsU0 = Math.Sqrt(u0 / R.Count), rmsU1 = Math.Sqrt(u1 / R.Count);
        res.Outputs.Add(new("WL_Bkt_Use", UseSignal(true, SM, b, KK), ""));
        PutObs(res, O, d.Signals.Keys);
        if (Wst != null) PutStab(res, Wst);
        if (Wst != null || itN > 0) { var wt = Fill(n); for (int k = 0; k < ix.Count; k++) wt[ix[k]] = fit.W[k]; res.Outputs.Add(new("WL_Bkt_Weight", wt, "")); }
        st.Bkt = new WlBktEst
        {
            Eps = epsU, X = xs, Se = se, St = Enumerable.Range(0, 10).Select(j => j == 2 ? double.NaN : StV(j)).ToArray(),
            N = R.Count, Rms0 = fit.Rms0, Rms1 = fit.Rms1, On = on,
        };
        foreach (var j in act) K[PK[j].Key] = P5(xs[j]);
        string pr(double v) => Data.Format.Precision(v, 5);
        double M4 = Num(KK, "BKM");
        var L = new List<string>
        {
            "✓ Bucket Parameter Estimation 완료 [" + (stM ? "Static" : "Dynamic") + "] (입력한 Range, 유효 샘플 " + R.Count + "개, 최소제곱)",
            "  Regression 대상: " + string.Join(", ", act.Select(j => PK[j].Key[1..])) + (PK.Where((_, j) => !on[j] && !(stM && j == 2)).Any() ? " · 체크 해제된 Parameter는 [사용값](비면 0)으로 고정하고 Tq4에서 뺀 뒤 회귀했습니다" : ""),
        };
        if (stM) { if (M4 > 0 && on[0] && on[1]) L.Add("  Static 모드: 입력한 M4=" + M4 + " kg 기준: C4x = " + pr(xs[0] / M4) + " m, C4y = " + pr(xs[1] / M4) + " m"); }
        else if (M4 > 0 && on[0] && on[1] && on[2])
        {
            double C4x = xs[0] / M4, C4y = xs[1] / M4, Iz = xs[2] - M4 * (C4x * C4x + C4y * C4y);
            L.Add("  → 입력한 M4=" + M4 + " kg 기준: C4x = " + pr(C4x) + " m, C4y = " + pr(C4y) + " m, I4zz = " + pr(Iz) + " kg·m²" + (Iz < 0 ? "  ⚠ 음수 — 질량·부품 파라미터를 확인하세요" : ""));
        }
        L.Add("  Tq4 RMS " + (rmsU0 / 1e3).ToString("F3", CultureInfo.InvariantCulture) + " kN·m → 잔차 RMS " + (rmsU1 / 1e3).ToString("F3", CultureInfo.InvariantCulture) + " kN·m (비가중) · 가중 잔차 RMS " + (fit.Rms1 / 1e3).ToString("F3", CultureInfo.InvariantCulture) + " kN·m");
        L.Add("  사용 샘플 " + vn + "개 · θ4 " + (vmn / Rad).ToString("F2", CultureInfo.InvariantCulture) + " ~ " + (vmx / Rad).ToString("F2", CultureInfo.InvariantCulture) + "° · Crowd(증가) " + nC + "개 / Dump(감소) " + nD + "개 · ε = " + Data.Format.Precision(epsU * 1e3, 3) + " mm/s");
        L.Add("  가중: " + (Wst != null ? "압력 안정도 창 " + winS + " s" : "안정도 가중 없음") + " + " + (itN > 0 ? "IRLS(Tukey) " + fit.It + "회 반복" : "IRLS 없음") + " → 평균 가중치 " + (wsum / R.Count).ToString("F2", CultureInfo.InvariantCulture) + ", 0.1 미만 " + (wlow * 100.0 / R.Count).ToString("F1", CultureInfo.InvariantCulture) + " %");
        if (clamp.Count > 0)
            L.Add("⚠ 하한 0에 걸린 Parameter: " + string.Join(", ", clamp.Select(j => PK[j].Key[1..])) + " — 제약 없는 추정값: " + string.Join(" / ", clamp.Select(j => PK[j].Key[1..] + " = " + (fitF != null ? pr(fitF.X[act.IndexOf(j)]) : "?"))));
        if (rmsU1 > .5 * rmsU0) L.Add("⚠ 잔차가 Tq4의 절반 이상입니다 — 단독 동작 구간·부품 질량·압력 센서·각도 열을 확인하세요.");
        var es = EstSignals(d, K, s, st, true);
        res.Outputs.AddRange(es.Outputs); res.Charts.AddRange(es.Charts);
        res.Message = string.Join("\n", L); res.Warn = rmsU1 > .5 * rmsU0;
        return res;
    }

    /// <summary>e-2 Arm Parameter Estimation (Bucket 사용값 고정). 성공하면 K의 uP, uQ, uJ3, uF3c, uF3p, uF3v 갱신.</summary>
    public static CalcResult EstimateArm(Data.DataSet d, KinParams K, WlSignals s, WlEstState st)
    {
        var res = new CalcResult();
        var (b, err) = WlAnalysis.Base(d, K, s);
        if (b == null) { res.Message = err!; res.Warn = true; return res; }
        var KK = b.KK; int n = b.N; var g = b.G; var CA = b.CA; var CB = b.CB;
        if (CA == null || CB == null) { res.Message = "Arm 추정에는 Arm·Bucket 두 실린더의 압력(Head/Rod 열)과 Tube/Rod 직경이 필요합니다."; res.Warn = true; return res; }
        double X0 = KK["uM4C4x"], Y0 = KK["uM4C4y"], J0 = KK["uJ4"];
        if (!(double.IsFinite(X0) && double.IsFinite(Y0) && double.IsFinite(J0))) { res.Message = "Arm 추정에는 M4·C4x, M4·C4y, I4zz+M4(C4x²+C4y²)의 [사용값]이 필요합니다. 먼저 Bucket Parameter Estimation을 하거나 값을 직접 입력하세요."; res.Warn = true; return res; }
        var T = WlAnalysis.TqAll(b); var kn = WlAnalysis.Kin(d, s, b); double L3 = g.L3, G = WlGeom.Grav; var (lo, hi) = AngRange(KK, "T3min", "T3max");
        var SM = SelMask(false, b, kn, KK, st.DirInv); var cBu = CfB(KK);
        double[] yr = Fill(n), pc = Fill(n), pq = Fill(n), f1 = Fill(n), f2 = Fill(n), f3 = Fill(n), ik = Fill(n), a3c = Fill(n);
        for (int i = 0; i < n; i++)
        {
            double a = b.T2[i], bb = b.T3[i];
            if (!(Fin(a) && Fin(bb) && Fin(T["Tq3_Eq"][i]) && Fin(b.VA[i]) && Fin(b.VB[i]) && Fin(kn.Al3[i]) && Fin(kn.Al4[i]) && Fin(kn.W3[i]) && Fin(kn.W4[i]) && a >= lo && a <= hi && (SM == null || SM[i]))) continue;
            var dl = g.DL(a, bb); double sA = Math.Tanh(b.VA[i] / .005), c4 = Math.Cos(kn.Th4[i]), s4 = Math.Sin(kn.Th4[i]), wf = kn.W3[i] + kn.W4[i];
            yr[i] = T["Tq3_Eq"][i] * 1e3 - dl.DLb2 * Fric(cBu, b.VB[i], CB, i) - G * X0 * Math.Cos(a + bb) + G * Y0 * Math.Sin(a + bb);
            pc[i] = G * Math.Cos(a); pq[i] = -G * Math.Sin(a); f1[i] = dl.DLa * CA.N * 1e3 * sA; f3[i] = dl.DLa * CA.N * 1e3 * b.VA[i];
            f2[i] = dl.DLa * CA.N * 1e-3 * (CA.HP[i] * CA.Dt + CA.RP[i] * (CA.Dt + CA.Dr)) * sA;
            ik[i] = J0 * (kn.Al3[i] + kn.Al4[i]) + L3 * (2 * kn.Al3[i] + kn.Al4[i]) * (X0 * c4 - Y0 * s4) + L3 * (kn.W3[i] * kn.W3[i] - wf * wf) * (X0 * s4 + Y0 * c4);
            a3c[i] = kn.Al3[i];
        }
        double[] F(double[] a) => Dynamics.SmoothMA(a, kn.N);
        var Fy = F(yr); var Fp = F(pc); var Fq = F(pq); var Ff1 = F(f1); var Ff2 = F(f2); var Ff3 = F(f3);
        var R = new List<double[]>(); var y = new List<double>();
        for (int i = 0; i < n; i++)
            if (Fin(Fy[i]) && Fin(Fp[i]) && Fin(Fq[i]) && Fin(Ff1[i]) && Fin(Ff2[i]) && Fin(Ff3[i]) && Fin(ik[i]) && Fin(a3c[i]))
            { R.Add(new[] { Fp[i], Fq[i], a3c[i], Ff1[i], Ff2[i], Ff3[i] }); y.Add(Fy[i] - ik[i]); }
        if (R.Count < 30)
        {
            res.Message = "추정에 쓸 유효 샘플이 부족합니다 (" + R.Count + "개 / 구간 입력한 Range, T3 Range " + (double.IsFinite(lo) || double.IsFinite(hi) ? "적용" : "미지정") + "). 각도·압력·시간 열, 직경, T3 Range를 확인하세요.";
            res.Warn = true; return res;
        }
        res.Outputs.Add(new("WL_Arm_Use", UseSignal(false, SM, b, KK), ""));
        var fit = LS(R, y, new[] { false, false, true, true, true, true });
        if (fit == null) { res.Outputs.Clear(); res.Message = "추정 실패: 움직임이 부족하거나 열이 서로 종속입니다."; res.Warn = true; return res; }
        double P = fit.X[0], Q = fit.X[1], Jt = fit.X[2], M4 = KK["BKM"], M3 = KK["LAM"], m4l = M4 > 0 ? M4 * L3 * L3 : 0, Jo = Jt - m4l;
        st.Arm = new WlArmEst { Jt = Jt, X = new[] { P, Q, Jo, fit.X[3], fit.X[4], fit.X[5] }, Se = fit.Se, N = R.Count, Rms0 = fit.Rms0, Rms1 = fit.Rms1 };
        K["uP"] = P5(P); K["uQ"] = P5(Q); K["uJ3"] = P5(Jo); K["uF3c"] = P5(fit.X[3]); K["uF3p"] = P5(fit.X[4]); K["uF3v"] = P5(fit.X[5]);
        string pr(double v) => Data.Format.Precision(v, 5);
        var L = new List<string> { "✓ Arm Parameter Estimation 완료 (입력한 Range, 유효 샘플 " + R.Count + "개, 최소제곱; 사용한 M4·C4x=" + X0 + ", M4·C4y=" + Y0 + ", J4=" + J0 + ")" };
        if (M4 > 0 && M3 > 0)
        {
            double C3x = (P - M4 * L3) / M3, C3y = Q / M3, Iz = Jo - M3 * (C3x * C3x + C3y * C3y);
            L.Add("  → 입력한 M3=" + M3 + " kg, M4=" + M4 + " kg 기준: C3x = " + pr(C3x) + " m, C3y = " + pr(C3y) + " m, I3zz = " + pr(Iz) + " kg·m²" + (Iz < 0 ? "  ⚠ 음수 — 질량 파라미터를 확인하세요" : ""));
        }
        L.Add("  Tq3 RMS " + (fit.Rms0 / 1e3).ToString("F3", CultureInfo.InvariantCulture) + " kN·m → 잔차 RMS " + (fit.Rms1 / 1e3).ToString("F3", CultureInfo.InvariantCulture) + " kN·m");
        if (fit.Rms1 > .5 * fit.Rms0) L.Add("⚠ 잔차가 Tq3의 절반 이상입니다 — 단독 동작 구간·Bucket 추정값·압력 센서를 확인하세요.");
        var es = EstSignals(d, K, s, st, false);
        res.Outputs.AddRange(es.Outputs); res.Charts.AddRange(es.Charts);
        res.Message = string.Join("\n", L); res.Warn = fit.Rms1 > .5 * fit.Rms0;
        return res;
    }

    /// <summary>추정(사용값) 파라미터로 토크 역계산: Tq4_Est(+Static) 또는 Tq3_Est</summary>
    public static CalcResult EstSignals(Data.DataSet d, KinParams K, WlSignals s, WlEstState st, bool bkt)
    {
        var res = new CalcResult();
        var (b, _) = WlAnalysis.Base(d, K, s);
        if (b == null) return res;
        var KK = b.KK; var g = b.G; int n = b.N; var CA = b.CA; var CB = b.CB; var kn = WlAnalysis.Kin(d, s, b); double L3 = g.L3, G = WlGeom.Grav;
        double Nz(string k, double dflt) => double.IsFinite(KK[k]) ? KK[k] : dflt;
        var cfB = CfB(KK); var cfA = new[] { Nz("uF3c", 0), Nz("uF3v", 0), Nz("uF3p", 0) };
        double X = Nz("uM4C4x", double.NaN), Y = Nz("uM4C4y", double.NaN), J = Nz("uJ4", double.NaN);
        var (lo4, hi4) = AngRange(KK, "T4min", "T4max");
        double epsB = bkt && st.Bkt != null ? st.Bkt.Eps : EpsOf(KK, b.VB, i => Fin(b.T3[i]) && b.T3[i] >= lo4 && b.T3[i] <= hi4);
        var O = bkt ? Obs(b, kn, .005, epsB) : null; var o = Fill(n); bool stM = KK.Str("BkMode") == "static";
        static double Z(double x) => double.IsNaN(x) || x == 0 ? 0 : x;
        for (int i = 0; i < n; i++)
        {
            double a = b.T2[i], bb = b.T3[i];
            if (!(Fin(a) && Fin(bb) && Fin(kn.Al3[i]) && Fin(kn.Al4[i]) && Fin(kn.W3[i]) && Fin(kn.W4[i]))) continue;
            var dl = g.DL(a, bb); double ph = kn.Th3[i] + kn.Th4[i], c4 = Math.Cos(kn.Th4[i]), s4 = Math.Sin(kn.Th4[i]), W3 = kn.W3[i] * kn.W3[i], wf = kn.W3[i] + kn.W4[i], al = kn.Al3[i] + kn.Al4[i];
            if (bkt)
            {
                if (CB == null || !Fin(b.VB[i])) continue;
                double n1 = stM ? O!["N1s"][i] : O!["N1"][i], n2 = stM ? O["N2s"][i] : O["N2"][i];
                o[i] = (Z(X) * n1 + Z(Y) * n2 + (stM ? 0 : Z(J) * O["N3"][i]) + dl.DLb3 * (Fric(cfB, b.VB[i], CB, i, epsB) + Nz("uF4d", 0)) + Nz("uFjc", 0) * Z(O["Hjc"][i]) + Nz("uFjf", 0) * Z(O["Hjf"][i])) / 1e3;
            }
            else
            {
                if (CA == null || CB == null || !Fin(b.VA[i]) || !Fin(b.VB[i])) continue;
                double P = Nz("uP", double.NaN), Q = Nz("uQ", double.NaN), Jo = Nz("uJ3", 0), M4 = Nz("BKM", 0), Jt = Jo + (M4 > 0 ? M4 * L3 * L3 : 0);
                double Qb = -G * (P * Math.Cos(kn.Th3[i]) + X * Math.Cos(ph) - Y * Math.Sin(ph) - Q * Math.Sin(kn.Th3[i]));
                double In = Jt * kn.Al3[i] + J * al + L3 * (2 * kn.Al3[i] + kn.Al4[i]) * (X * c4 - Y * s4) + L3 * (W3 - wf * wf) * (X * s4 + Y * c4);
                o[i] = (In - Qb + dl.DLa * Fric(cfA, b.VA[i], CA, i) + dl.DLb2 * Fric(cfB, b.VB[i], CB, i)) / 1e3;
            }
        }
        string nm = bkt ? "WL_Tq4_Est_kNm" : "WL_Tq3_Est_kNm", eq = bkt ? "WL_Tq4_Eq_kNm" : "WL_Tq3_Eq_kNm";
        if (!o.Any(double.IsFinite)) return res;
        string? stN = null;
        if (bkt && st.Bkt != null && CB != null)
        {
            var S = st.Bkt.St; var os = Fill(n); var jc = Fill(n); var ft = Fill(n);
            double Sv(int j) => double.IsNaN(S[j]) ? 0 : S[j];
            for (int i = 0; i < n; i++)
            {
                double a = b.T2[i], bb = b.T3[i];
                if (!(Fin(a) && Fin(bb) && Fin(b.VB[i]))) continue;
                var dl = g.DL(a, bb);
                os[i] = (S[0] * 9.81 * Math.Cos(a + bb) - S[1] * 9.81 * Math.Sin(a + bb) + dl.DLb3 * (Fric(new[] { S[3], S[6], S[4], S[5] }, b.VB[i], CB, i, epsB) + Sv(7)) + Sv(8) * Z(O!["Hjc"][i]) + Sv(9) * Z(O["Hjf"][i])) / 1e3;
            }
            for (int i = 0; i < n; i++) { if (!(Fin(b.T2[i]) && Fin(b.T3[i]))) continue; var dl = g.DL(b.T2[i], b.T3[i]); jc[i] = dl.DLb3; ft[i] = S[3] * dl.DLb3 / 1e3; }
            stN = "WL_Tq4_Static_Est_kNm";
            res.Outputs.Add(new(stN, os, "kNm")); res.Outputs.Add(new("WL_Bkt_Jac_m_per_rad", jc, "m/rad")); res.Outputs.Add(new("WL_Bkt_CoulombTq_kNm", ft, "kNm"));
        }
        res.Outputs.Add(new(nm, o, "kNm"));
        var ch = new List<string>(); if (d.Signals.ContainsKey(eq)) ch.Add(eq); ch.Add(nm); if (stN != null) ch.Add(stN);
        res.Charts.Add(ch.ToArray());
        if (bkt && d.Signals.ContainsKey(eq) && d.Signals.ContainsKey("WL_Bkt_AngAbs_deg")) res.XyCharts.Add(("WL_Bkt_AngAbs_deg", eq));
        res.Message = "✓ " + nm + " 계산";
        return res;
    }

    /// <summary>f. Weighing: 적재물 중량 [kg] (Arm·Bucket 평형식 최소제곱)</summary>
    public static CalcResult Weigh(Data.DataSet d, KinParams K, WlSignals s, double winA, double winB, string winLabel)
    {
        var res = new CalcResult();
        var (b, err) = WlAnalysis.Base(d, K, s);
        if (b == null) { res.Message = err!; res.Warn = true; return res; }
        var KK = b.KK; var g = b.G; int n = b.N; var CA = b.CA; var CB = b.CB;
        if (CA == null || CB == null) { res.Message = "Weighing에는 두 실린더의 압력(Head/Rod 열)과 Tube/Rod 직경이 필요합니다."; res.Warn = true; return res; }
        double X = KK["uM4C4x"], Y = KK["uM4C4y"], J = KK["uJ4"], P = KK["uP"], Q = KK["uQ"], Jo = KK["uJ3"];
        if (!(double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(J) && double.IsFinite(P) && double.IsFinite(Q)))
        { res.Message = "[e. Dynamics Parameter Estimation]의 [사용값]이 필요합니다: M4·C4x, M4·C4y, I4zz+M4(C4x²+C4y²), M4L3+M3C3x, M3C3y (Bucket·Arm Parameter Estimation으로 구하거나 직접 입력)."; res.Warn = true; return res; }
        var warn = new List<string>(); if (!double.IsFinite(Jo)) warn.Add("I3zz+M3(C3x²+C3y²) [사용값] 미입력 → 0으로 처리");
        var T = WlAnalysis.TqAll(b); var kn = WlAnalysis.Kin(d, s, b); double L3 = g.L3, G = WlGeom.Grav, M4 = KK["BKM"], Jt = (double.IsFinite(Jo) ? Jo : 0) + (M4 > 0 ? M4 * L3 * L3 : 0);
        double Pick(string u) => double.IsFinite(KK[u]) ? KK[u] : 0;
        var cfA = new[] { Pick("uF3c"), Pick("uF3v"), Pick("uF3p") }; var cfB = CfB(KK);
        bool hx = double.IsFinite(KK["PYx"]), hy = double.IsFinite(KK["PYy"]);
        double px = hx ? KK["PYx"] * g.U : .5 * g.L4, py = hy ? KK["PYy"] * g.U : .3 * g.L4, pp = px * px + py * py;
        if (!hx || !hy) warn.Add("적재물 무게중심 미입력 → 기본값 (0.5·L4, 0.3·L4, Bucket 좌표) 사용");
        double[] m = Fill(n), m3a = Fill(n), m4a = Fill(n), rs = Fill(n);
        for (int i = 0; i < n; i++)
        {
            double a = b.T2[i], bb = b.T3[i];
            if (!(Fin(a) && Fin(bb) && Fin(T["Tq3_Eq"][i]) && Fin(T["Tq4_Eq"][i]) && Fin(b.VA[i]) && Fin(b.VB[i]) && Fin(kn.Al3[i]) && Fin(kn.Al4[i]) && Fin(kn.W3[i]) && Fin(kn.W4[i]))) continue;
            var dl = g.DL(a, bb); double Fr3 = Fric(cfA, b.VA[i], CA, i), Fr4 = Fric(cfB, b.VB[i], CB, i), t3f = kn.Th3[i], t4f = kn.Th4[i], ph = t3f + t4f, c4 = Math.Cos(t4f), s4 = Math.Sin(t4f);
            double W3 = kn.W3[i] * kn.W3[i], wf = kn.W3[i] + kn.W4[i], al = kn.Al3[i] + kn.Al4[i];
            double c1 = G * Math.Cos(ph) + L3 * kn.Al3[i] * c4 + L3 * W3 * s4, c2 = -G * Math.Sin(ph) - L3 * kn.Al3[i] * s4 + L3 * W3 * c4;
            double R4 = T["Tq4_Eq"][i] * 1e3 - dl.DLb3 * Fr4 - (X * c1 + Y * c2 + J * al), a4 = px * c1 + py * c2 + pp * al;
            double Qb = -G * (P * Math.Cos(t3f) + X * Math.Cos(ph) - Y * Math.Sin(ph) - Q * Math.Sin(t3f));
            double In = Jt * kn.Al3[i] + J * al + L3 * (2 * kn.Al3[i] + kn.Al4[i]) * (X * c4 - Y * s4) + L3 * (W3 - wf * wf) * (X * s4 + Y * c4);
            double R3 = T["Tq3_Eq"][i] * 1e3 - dl.DLa * Fr3 - dl.DLb2 * Fr4 + Qb - In;
            double a3 = L3 * L3 * kn.Al3[i] + pp * al + L3 * (2 * kn.Al3[i] + kn.Al4[i]) * (px * c4 - py * s4) + L3 * (W3 - wf * wf) * (px * s4 + py * c4) + G * (L3 * Math.Cos(t3f) + px * Math.Cos(ph) - py * Math.Sin(ph));
            double den = a3 * a3 + a4 * a4;
            if (!(den > 1e-6)) continue;
            double mm = (a3 * R3 + a4 * R4) / den; m[i] = mm;
            if (Math.Abs(a3) > 1) m3a[i] = R3 / a3;
            if (Math.Abs(a4) > 1) m4a[i] = R4 / a4;
            rs[i] = Math.Sqrt((R3 - mm * a3) * (R3 - mm * a3) + (R4 - mm * a4) * (R4 - mm * a4)) / 1e3;
        }
        res.Put("WL_Payload_kg", m, "kg"); res.Put("WL_Payload_ArmEq_kg", m3a, "kg"); res.Put("WL_Payload_BktEq_kg", m4a, "kg"); res.Put("WL_Payload_Resid_kNm", rs, "kNm");
        if (!res.Ok) { res.Message = "계산 가능한 샘플이 없습니다 (각도·압력·시간 열과 [사용값]을 확인하세요)."; res.Warn = true; return res; }
        var c0 = new[] { "WL_Payload_kg", "WL_Payload_ArmEq_kg", "WL_Payload_BktEq_kg" }.Where(res.Has).ToArray();
        res.Charts.Add(c0); if (res.Has("WL_Payload_Resid_kNm")) res.Charts.Add(new[] { "WL_Payload_Resid_kNm" });
        string f(double x) => x.ToString("F1", CultureInfo.InvariantCulture); var L = new List<string>();
        foreach (var (k, t) in new[] { ("WL_Payload_kg", "적재물 중량(LS)"), ("WL_Payload_ArmEq_kg", "  · Arm 평형식만"), ("WL_Payload_BktEq_kg", "  · Bucket 평형식만") })
        {
            var o = res.Outputs.FirstOrDefault(x => x.Name == k);
            if (o != null && Stat(o.Values, d.Time, winA, winB) is { } sv)
                L.Add(t + ":  중앙값 " + f(sv.Med) + " kg · 평균 " + f(sv.Mean) + " ± " + f(sv.Sd) + " (10~90%: " + f(sv.P10) + " ~ " + f(sv.P90) + "), N=" + sv.N);
        }
        res.Message = "✓ Weighing 완료 (Dynamics Parameter Estimation의 [사용값] + 마찰 보정 + 관성 항 포함)\n[" + winLabel + " 통계]\n" + string.Join("\n", L) + (warn.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", warn) : "");
        res.Warn = warn.Count > 0;
        return res;
    }

    public sealed record StatResult(int N, double Med, double Mean, double Sd, double P10, double P90);

    /// <summary>구간 [a, b] 통계 (중앙값, 평균, 표준편차, 10/90 %)</summary>
    public static StatResult? Stat(double[] arr, double[] t, double a, double b)
    {
        var v = new List<double>(); for (int i = 0; i < arr.Length; i++) if (Fin(arr[i]) && t[i] >= a && t[i] <= b) v.Add(arr[i]);
        if (v.Count == 0) return null;
        v.Sort(); int n = v.Count; double mean = v.Sum() / n, sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / n);
        return new StatResult(n, v[n >> 1], mean, sd, v[(int)Math.Floor(n * .1)], v[Math.Min(n - 1, (int)Math.Floor(n * .9))]);
    }

    /// <summary>구간 Labeling: 반복한 링크(bkt = Bucket θ4, 아니면 Lift Arm θ3)의 각속도로 Cycle과 동작 방향을 나눈다</summary>
    public static CalcResult Label(Data.DataSet d, KinParams K, WlSignals s, bool bkt, bool inv)
    {
        var res = new CalcResult();
        var (b, err) = WlAnalysis.Base(d, K, s);
        if (b == null) { res.Message = err!; res.Warn = true; return res; }
        var kn = WlAnalysis.Kin(d, s, b); var th = bkt ? kn.Th4 : kn.Th3; var w = bkt ? kn.W4 : kn.W3; int n = th.Length;
        var (lo, hi) = bkt ? AngRange(K, "T4min", "T4max") : AngRange(K, "T3min", "T3max"); bool noR = !double.IsFinite(lo) && !double.IsFinite(hi);
        var mask = new bool[n]; int mc = 0;
        for (int i = 0; i < n; i++) if (Fin(th[i]) && Fin(w[i]) && th[i] >= lo && th[i] <= hi) { mask[i] = true; mc++; }
        if (mc < 10) { res.Message = "Range 안의 유효 샘플이 부족합니다 (" + mc + "개). " + (bkt ? "T4" : "T3") + " Range와 각도 열을 확인하세요."; res.Warn = true; return res; }
        var dir = DirArr(w, mask, kn.Ts);
        var cyc = new double[n]; var mot = new double[n]; int sgn = inv ? -1 : 1, c = 0, first = 0, last = 0, nUp = 0, nDn = 0;
        for (int i = 0; i < n; i++)
        {
            if (!mask[i]) continue;
            int dd = dir[i];
            if (dd != 0)
            {
                if (first == 0) { first = dd; c = 1; } else if (dd == first && last != first) c++;
                if (dd != last) { if (dd * sgn > 0) nUp++; else nDn++; }
                last = dd;
            }
            if (c > 0) { cyc[i] = c; mot[i] = dd * sgn; }
        }
        var names = bkt ? new[] { "Crowd", "Dump" } : new[] { "Up", "Down" };
        res.Outputs.Add(new("WL_Cycle", cyc, "")); res.Outputs.Add(new("WL_Motion", mot, ""));
        res.Charts.Add(new[] { "WL_Cycle", "WL_Motion" });
        res.Message = "✓ Labeling 완료: " + (bkt ? "Bucket(θ4)" : "Lift Arm(θ3)") + " 반복 · " + (noR ? "Range 미지정 → 전체 구간 · " : "Range 안 ") + mc + "개 샘플 · Cycle " + c + "개 · " + names[0] + " " + nUp + "회, " + names[1] + " " + nDn + "회\n"
            + "  WL_Cycle (1,2,… / Range 밖=0), WL_Motion (" + names[0] + "=+1, " + names[1] + "=-1, 정지=0) 신호를 추가했습니다.";
        return res;
    }
}
