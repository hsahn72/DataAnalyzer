namespace DataAnalyzer.Core.Excavator;

/// <summary>실린더 사양 (SI)</summary>
public sealed record CylSpec(int N, double Lmin, double Lmax, double Dt, double Dr, double AH, double AR);

/// <summary>선회 사양: I1 [kg·m²], c = 상부 무게중심, disp [cc/rev], ratio, eff (0~1)</summary>
public sealed record SwingSpec(double I1, double[] C, double Disp, double Ratio, double Eff);

/// <summary>동역학 파라미터 (HTML dynParams). 링크 i = 0 붐, 1 암, 2 버켓.</summary>
public sealed class DynParams
{
    public required double[] M { get; init; }
    /// <summary>링크 좌표 무게중심 [m]</summary>
    public required V2[] C { get; init; }
    /// <summary>무게중심 기준 [xx, yy, zz] 관성 [kg·m²]</summary>
    public required double[][] I { get; init; }
    public required CylSpec[] Cyl { get; init; }
    public required SwingSpec Sw { get; init; }
    /// <summary>압력 단위 → Pa (bar 1e5, MPa 1e6)</summary>
    public required double PU { get; init; }
}

/// <summary>순동역학 결과</summary>
public sealed class DynResult
{
    public required double[] Tau { get; init; }
    public required V2[] Pj { get; init; }
    public required V2[] R { get; init; }
    public required double[] Ph { get; init; }
    public V2 Tip { get; init; }
}

/// <summary>굴착기 동역학 (HTML의 dynParams / dynInertial / swingJ / tipForce / smoothMA / ddt).</summary>
public static class Dynamics
{
    public const double G = 9.81;
    static readonly string[] CylPre = { "Bm", "Am", "Bk" };
    static readonly string[] CylNKey = { "BmN", "AmN", "BkN" };

    static double Cross(V2 a, V2 b) => a.X * b.Y - a.Y * b.X;
    static V2 J(V2 v) => new(-v.Y, v.X);

    /// <summary>실린더 개수 (붐 기본 2, 나머지 1)</summary>
    public static int CylN(KinParams k, int j) { double v = k[CylNKey[j]]; return double.IsFinite(v) && v >= 1 ? (int)Math.Round(v, MidpointRounding.AwayFromZero) : j == 0 ? 2 : 1; }

    public static DynParams Params(Kinematics g)
    {
        var k = g.K; double u = k.Unit == "m" ? 1 : .001;
        double[] ls = { g.Lb, g.La, g.Lk };
        double[] m = { k["M2"], k["M3"], k["M4"] };
        bool piv = k.Str("IRef") == "pivot";
        var c = new V2[3]; var I = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            int n = i + 2;
            double x = k["C" + n + "x"], y = k["C" + n + "y"];
            c[i] = new V2(double.IsFinite(x) ? x * u : ls[i] / 2, double.IsFinite(y) ? y * u : 0);
            double xx = k["I" + n + "xx"], yy = k["I" + n + "yy"], zz = k["I" + n + "zz"];
            if (piv) { double cx = c[i].X, cy = c[i].Y; xx -= m[i] * cy * cy; yy -= m[i] * cx * cx; zz -= m[i] * (cx * cx + cy * cy); }
            I[i] = new[] { xx, yy, zz };
        }
        var cyl = new CylSpec[3];
        for (int j = 0; j < 3; j++)
        {
            string p = CylPre[j]; double dt = k[p + "Dt"] * u, dr = k[p + "Dr"] * u;
            cyl[j] = new CylSpec(CylN(k, j), k[p + "Lmin"] * u, k[p + "Lmax"] * u, dt, dr, Math.PI / 4 * dt * dt, Math.PI / 4 * (dt * dt - dr * dr));
        }
        double eff = k["SwEff"]; if (eff > 1) eff /= 100;
        double[] c1 = { k["C1x"] * u, k["C1y"] * u, k["C1z"] * u };
        string ax = k.Str("SwAxis", "zz"); if (ax == "") ax = "zz";
        double i1 = k["I1" + ax];
        if (ax == "zz" && k.Str("IRef") != "pivot" && double.IsFinite(c1[0]) && double.IsFinite(c1[1]) && double.IsFinite(k["M1"]))
            i1 += k["M1"] * (c1[0] * c1[0] + c1[1] * c1[1]);
        return new DynParams
        {
            M = m, C = c, I = I, Cyl = cyl,
            Sw = new SwingSpec(i1, c1, k["SwDisp"], k["SwRatio"], eff),
            PU = k.Str("pUnit") == "MPa" ? 1e6 : 1e5,
        };
    }

    /// <summary>상대각 th, 상대각속도 w, 상대각가속도 al → 관절 j의 (하류 링크 전체) 관성+중력 토크 [N·m]</summary>
    public static DynResult Inertial(Kinematics g, DynParams dp, double[] th, double[] w, double[] al, bool grav)
    {
        double[] ph = { th[0], th[0] + th[1], th[0] + th[1] + th[2] };
        double[] pd = { w[0], w[0] + w[1], w[0] + w[1] + w[2] };
        double[] pdd = { al[0], al[0] + al[1], al[0] + al[1] + al[2] };
        V2[] v = { Kinematics.Rot(ph[0], g.Lb, 0), Kinematics.Rot(ph[1], g.La, 0) };
        var p1 = g.P + v[0];
        V2[] pj = { g.P, p1, p1 + v[1] };
        var d = new V2[3]; var r = new V2[3]; var a = new V2[3];
        for (int i = 0; i < 3; i++) { d[i] = Kinematics.Rot(ph[i], dp.C[i]); r[i] = pj[i] + d[i]; }
        for (int i = 0; i < 3; i++)
        {
            double ax = 0, ay = 0;
            for (int k = 0; k < i; k++) { var jv = J(v[k]); ax += pdd[k] * jv.X - pd[k] * pd[k] * v[k].X; ay += pdd[k] * jv.Y - pd[k] * pd[k] * v[k].Y; }
            var jd = J(d[i]);
            a[i] = new V2(ax + pdd[i] * jd.X - pd[i] * pd[i] * d[i].X, ay + pdd[i] * jd.Y - pd[i] * pd[i] * d[i].Y);
        }
        double gy = grav ? -G : 0;
        var tau = new double[3];
        for (int j = 0; j < 3; j++)
        {
            double s = 0;
            for (int i = j; i < 3; i++)
                s += Cross(r[i] - pj[j], new V2(dp.M[i] * a[i].X, dp.M[i] * (a[i].Y - gy))) + dp.I[i][2] * pdd[i];
            tau[j] = s;
        }
        return new DynResult { Tau = tau, Pj = pj, R = r, Ph = ph, Tip = pj[2] + Kinematics.Rot(ph[2], g.Lk, 0) };
    }

    /// <summary>수직(선회)축에 대한 상부 전체 관성 [kg·m²]</summary>
    public static double SwingJ(DynParams dp, DynResult res)
    {
        double j = dp.Sw.I1;
        for (int i = 0; i < 3; i++)
        {
            double s = Math.Sin(res.Ph[i]), c = Math.Cos(res.Ph[i]);
            j += dp.I[i][0] * s * s + dp.I[i][1] * c * c + dp.M[i] * res.R[i].X * res.R[i].X;
        }
        return j;
    }

    /// <summary>관절 잔차 rho_j = 구동토크 − 관성토크 → Tip 외력 [Fx, Fy] (최소제곱), (tip−Pj)×F = −rho_j</summary>
    public static V2 TipForce(DynResult res, double[] rho)
    {
        double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0; int ok = 0;
        for (int j = 0; j < 3; j++)
        {
            if (!double.IsFinite(rho[j])) continue;
            double rx = res.Tip.X - res.Pj[j].X, ry = res.Tip.Y - res.Pj[j].Y, rh = -rho[j];
            a11 += ry * ry; a12 += -ry * rx; a22 += rx * rx; b1 += -ry * rh; b2 += rx * rh; ok++;
        }
        if (ok < 2) return V2.NaN;
        double det = a11 * a22 - a12 * a12;
        if (!(Math.Abs(det) > 1e-9 * (a11 * a22 + 1e-30))) return V2.NaN;
        return new V2((b1 * a22 - b2 * a12) / det, (a11 * b2 - a12 * b1) / det);
    }

    /// <summary>NaN을 건너뛰는 대칭 이동평균 (N 샘플, 가장자리는 창 축소)</summary>
    public static double[] SmoothMA(double[] y, int n)
    {
        int len = y.Length, k = Math.Max(0, n >> 1);
        var o = new double[len]; var ps = new double[len + 1]; var pc = new double[len + 1];
        for (int i = 0; i < len; i++) { bool f = double.IsFinite(y[i]); ps[i + 1] = ps[i] + (f ? y[i] : 0); pc[i + 1] = pc[i] + (f ? 1 : 0); }
        for (int i = 0; i < len; i++)
        {
            int lo = Math.Max(0, i - k), hi = Math.Min(len - 1, i + k); double c = pc[hi + 1] - pc[lo];
            o[i] = double.IsFinite(y[i]) && c > 0 ? (ps[hi + 1] - ps[lo]) / c : double.NaN;
        }
        return o;
    }

    /// <summary>중앙 차분 미분 (끝은 한쪽 차분)</summary>
    public static double[] Ddt(double[] y, double[] t)
    {
        int n = y.Length; var o = new double[n];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1); double dt = t[b] - t[a];
            o[i] = b > a && dt > 0 ? (y[b] - y[a]) / dt : double.NaN;
        }
        return o;
    }
}
