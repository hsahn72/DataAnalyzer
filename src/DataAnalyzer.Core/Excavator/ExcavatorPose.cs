using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Excavator;

/// <summary>한 시각의 자세 (HTML exAngles 결과)</summary>
public sealed class PoseAtTime
{
    public double T { get; init; }
    /// <summary>링크 상대각 [rad] (붐, 암, 버켓)</summary>
    public required double[] Th { get; init; }
    /// <summary>실린더 길이 [m]</summary>
    public required double[] Cyl { get; init; }
    /// <summary>기구학 해 없음 표시</summary>
    public required bool[] Bad { get; init; }
    /// <summary>"각도" / "실린더" / ""</summary>
    public required string[] Src { get; init; }
    public Pose? Fk { get; init; }
    public bool Ok => Fk != null;
}

public static class ExcavatorPose
{
    /// <summary>시각 t의 링크 각도·실린더 길이·순기구학 (HTML exAngles). 각도 열이 있으면 각도를, 없으면 실린더 길이로 역기구학.</summary>
    public static PoseAtTime At(Kinematics g, ExcavatorSignals s, DataSet d, double t)
    {
        const double Rad = Kinematics.Rad;
        double[] th = { double.NaN, double.NaN, double.NaN }, cyl = { double.NaN, double.NaN, double.NaN };
        var bad = new bool[3]; var src = new[] { "", "", "" };
        double V(string col) => col != "" && d.Signals.TryGetValue(col, out var a) ? Series.ValAt(d.Time, a, t) : double.NaN;
        var ang = s.Links.Select(r => V(r.Ang) * (r.AngUnit == "rad" ? 1 : Rad) + r.Offset * Rad).ToArray();
        var cl = s.Links.Select(r => V(r.Cyl) * ExcavatorSignals.LenToM(r.CylUnit)).ToArray();
        var rel = s.Absolute ? new[] { ang[0], ang[1] - ang[0], ang[2] - ang[1] } : ang;
        for (int i = 0; i < 3; i++)
        {
            if (!double.IsNaN(rel[i])) { th[i] = rel[i]; src[i] = "각도"; }
            else if (!double.IsNaN(cl[i])) { var r = g.Inv(i, cl[i]); th[i] = r.Ang; bad[i] = r.Bad; src[i] = "실린더"; }
            cyl[i] = !double.IsNaN(cl[i]) ? cl[i] : !double.IsNaN(th[i]) ? g.Fwd(i, th[i]) : double.NaN;
            if (!double.IsNaN(th[i]) && double.IsNaN(cyl[i])) bad[i] = true;
        }
        bool ok = th.All(double.IsFinite);
        return new PoseAtTime { T = t, Th = th, Cyl = cyl, Bad = bad, Src = src, Fk = ok ? g.Fk(th) : null };
    }

    /// <summary>선회각 [deg]: 지정한 선회 각도 열, 없으면 계산한 Swing_Ang_calc_deg/rad (HTML exSwingAng)</summary>
    public static double SwingDeg(ExcavatorSignals s, DataSet d, double t)
    {
        if (s.SwingAngle != "" && s.SwingAngleUnit != "" && d.Signals.TryGetValue(s.SwingAngle, out var a))
            return Series.ValAt(d.Time, a, t) * (s.SwingAngleUnit == "rad" ? 1 / Kinematics.Rad : 1);
        if (d.Signals.TryGetValue("Swing_Ang_calc_deg", out var b)) return Series.ValAt(d.Time, b, t);
        if (d.Signals.TryGetValue("Swing_Ang_calc_rad", out var c)) return Series.ValAt(d.Time, c, t) / Kinematics.Rad;
        return double.NaN;
    }
}
