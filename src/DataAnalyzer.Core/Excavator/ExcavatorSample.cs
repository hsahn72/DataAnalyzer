namespace DataAnalyzer.Core.Excavator;

/// <summary>샘플 데이터용 굴착기 신호 (HTML exSampleCols): 예시 파라미터로 만든 실린더 길이·압력·선회 속도.</summary>
public static class ExcavatorSample
{
    public static readonly string[] Names =
        { "Boom_CylLen_mm", "Arm_CylLen_mm", "Bkt_CylLen_mm", "pSwA", "pSwB", "pBmCylH", "pBmCylR", "pAmCylH", "pAmCylR", "pBkCylH", "pBkCylR", "SwingSpd_dps" };

    /// <param name="noise">0~1 균일 난수 (null이면 노이즈 없음)</param>
    public static double[][] Columns(double[] t, Func<double>? noise = null)
    {
        var g = Kinematics.Build(KinParams.Demo(), out _)!; var dp = Dynamics.Params(g);
        int n = t.Length; double[] Mk() => new double[n];
        var L = new[] { Mk(), Mk(), Mk() }; var P = Enumerable.Range(0, 8).Select(_ => Mk()).ToArray(); var SW = Mk();
        double[][] A = { new[] { 25.0, 20, 0 }, new[] { -90.0, 45, 1.2 }, new[] { -60.0, 55, 2.4 } };
        double W = 2 * Math.PI / 20, AMP = .35, WS = .5, R = Kinematics.Rad;
        double Rn(double a) => noise == null ? 0 : (noise() - .5) * a;
        (double[] T, double[] w, double[] a) Ex(double s)
        {
            double p = W * (s % 20);
            return (A.Select(q => (q[0] + q[1] * Math.Sin(p + q[2])) * R).ToArray(),
                    A.Select(q => q[1] * R * W * Math.Cos(p + q[2])).ToArray(),
                    A.Select(q => -q[1] * R * W * W * Math.Sin(p + q[2])).ToArray());
        }
        (double H, double R) Pr(double F, CylSpec c)
        {
            const double pb = 20e5; double pH, pR;
            if (F >= 0) { pR = pb; pH = (F + pR * c.AR) / c.AH; } else { pH = pb; pR = (pH * c.AH - F) / c.AR; }
            return (pH / 1e5, pR / 1e5);
        }
        double kS = dp.Sw.Disp * 1e-6 / (2 * Math.PI) * dp.Sw.Ratio * dp.Sw.Eff;
        for (int i = 0; i < n; i++)
        {
            double s = t[i]; var e = Ex(s); var res = Dynamics.Inertial(g, dp, e.T, e.w, e.a, true);
            double Fx = -8000 * Math.Sin(.7 * s), Fy = 12000 * (.5 + .5 * Math.Sin(.4 * s)), Ft = 1500 * Math.Sin(.3 * s);
            for (int j = 0; j < 3; j++)
            {
                double rx = res.Tip.X - res.Pj[j].X, ry = res.Tip.Y - res.Pj[j].Y, act = res.Tau[j] - (rx * Fy - ry * Fx);
                L[j][i] = g.Fwd(j, e.T[j]) * 1000 + Rn(.02);
                var h = Pr(act / g.DLdTh(j, e.T) / dp.Cyl[j].N, dp.Cyl[j]);
                P[2 + 2 * j][i] = h.H + Rn(.05); P[3 + 2 * j][i] = h.R + Rn(.05);
            }
            double w = AMP * Math.Sin(WS * s), al = AMP * WS * Math.Cos(WS * s), d = 1e-3;
            var e1 = Ex(s + d); var e2 = Ex(s - d);
            double jd = (Dynamics.SwingJ(dp, Dynamics.Inertial(g, dp, e1.T, e1.w, e1.a, false)) - Dynamics.SwingJ(dp, Dynamics.Inertial(g, dp, e2.T, e2.w, e2.a, false))) / (2 * d);
            double tact = Dynamics.SwingJ(dp, res) * al + jd * w - res.Tip.X * Ft, dP = tact / kS / 1e5;
            P[0][i] = 30 + Math.Max(dP, 0) + Rn(.05); P[1][i] = 30 + Math.Max(-dP, 0) + Rn(.05); SW[i] = w / R + Rn(.004);
        }
        return L.Concat(P).Append(SW).ToArray();
    }
}
