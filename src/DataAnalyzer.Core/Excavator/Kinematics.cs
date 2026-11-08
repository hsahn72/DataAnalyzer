namespace DataAnalyzer.Core.Excavator;

/// <summary>2D 벡터 (x = 앞, y = 위)</summary>
public readonly record struct V2(double X, double Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 NaN => new(double.NaN, double.NaN);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>점 역할 이름과 기본 배정 (HTML의 KROLES)</summary>
public sealed record KinRole(string Key, string Label, string DefaultPoint);

/// <summary>
/// 굴착기 Front 기구학 (HTML의 buildGeom / fk / cylLen / cylAngle / circInt).
/// 각도는 rad, 길이는 m. th = [붐, 암, 버켓] 상대각.
/// </summary>
public sealed class Kinematics
{
    public const double Rad = Math.PI / 180;

    public static readonly string[] Points = { "P20", "P21", "P30", "P31", "P40", "P41", "PGL" };
    public static readonly KinRole[] RoleList =
    {
        new("boomBase", "붐 실린더 베이스 (선회체 좌표, 붐 핀 기준)", "P20"),
        new("boomRod", "붐 실린더 로드 (붐 좌표)", "P21"),
        new("armBase", "암 실린더 베이스 (붐 좌표)", "P30"),
        new("armRod", "암 실린더 로드 (암 좌표)", "P31"),
        new("bktBase", "버켓 실린더 베이스 (암 좌표)", "P40"),
        new("guide", "가이드 링크 고정점 (암 좌표)", "PGL"),
        new("push", "푸시 링크 연결점 (버켓 좌표)", "P41"),
    };
    public static readonly IReadOnlyDictionary<string, string> DefaultRoles = RoleList.ToDictionary(r => r.Key, r => r.DefaultPoint);
    public static readonly string[] Lengths = { "BPH", "L1", "L2", "L3", "L4", "Lgl", "Lpl" };
    public static readonly string[] Required = Lengths.Concat(Points.SelectMany(p => new[] { p + "x", p + "y" })).ToArray();

    // ---- 기본 도구 ----
    public static double WrapA(double x)
    {
        while (x > Math.PI) x -= 2 * Math.PI;
        while (x <= -Math.PI) x += 2 * Math.PI;
        return x;
    }
    public static V2 Rot(double a, double x, double z) { double c = Math.Cos(a), s = Math.Sin(a); return new(x * c - z * s, x * s + z * c); }
    public static V2 Rot(double a, V2 p) => Rot(a, p.X, p.Y);
    public static double Dist(V2 p, V2 q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>피벗 기준 베이스점 B(부모 좌표) ~ 로드점 Rv(자식 좌표, 자식 상대각 a): 길이</summary>
    public static double CylLen(V2 b, V2 rv, double a) { var p = Rot(a, rv); return Hypot(b.X - p.X, b.Y - p.Y); }

    /// <summary>역: 길이 → 자식 상대각(rad). s=±1 해 선택, 삼각형이 안 되면 Bad (각도는 클램프)</summary>
    public static (double Ang, bool Bad) CylAngle(V2 b, V2 rv, double lc, double s)
    {
        double a = b.Length, d = rv.Length;
        if (!(a > 0 && d > 0 && lc > 0)) return (double.NaN, true);
        double c = (a * a + d * d - lc * lc) / (2 * a * d);
        bool bad = Math.Abs(c) > 1;
        double cc = Math.Max(-1, Math.Min(1, c));
        return (WrapA(Math.Atan2(b.Y, b.X) + (s < 0 ? -1 : 1) * Math.Acos(cc) - Math.Atan2(rv.Y, rv.X)), bad);
    }

    /// <summary>두 원의 교점 (c0,r0)∩(c1,r1), s=±1: c0→c1 방향 기준 좌/우</summary>
    public static (V2 P, bool Bad) CircInt(V2 c0, double r0, V2 c1, double r1, double s)
    {
        double dx = c1.X - c0.X, dy = c1.Y - c0.Y, d = Hypot(dx, dy);
        if (!(d > 0)) return (V2.NaN, true);
        double a = (r0 * r0 - r1 * r1 + d * d) / (2 * d), h2 = r0 * r0 - a * a, h = Math.Sqrt(Math.Max(0, h2));
        double mx = c0.X + a * dx / d, my = c0.Y + a * dy / d, k = (s < 0 ? -1 : 1) * h / d;
        return (new V2(mx - k * dy, my + k * dx), h2 < 0);
    }

    // ---- 기하 ----
    public KinParams K { get; }
    public double Ls { get; private init; }
    public double Lb { get; private init; }
    public double La { get; private init; }
    public double Lk { get; private init; }
    public double Lgl { get; private init; }
    public double Lpl { get; private init; }
    /// <summary>붐 핀 위치 [L1, BPH]</summary>
    public V2 P { get; private init; }
    public V2 B0 { get; private init; }
    public V2 B1 { get; private init; }
    public V2 A0 { get; private init; }
    public V2 A1 { get; private init; }
    public V2 C0 { get; private init; }
    public V2 Gp { get; private init; }
    public V2 P4 { get; private init; }
    /// <summary>암 피벗 기준 암 실린더 베이스 (A0 − [Lb,0])</summary>
    public V2 Ba => new(A0.X - Lb, A0.Y);
    public int SB { get; private init; }
    public int SA { get; private init; }
    public int SJ { get; private init; }
    public int SP { get; private init; }

    Kinematics(KinParams k) { K = k; }

    /// <summary>파라미터에서 기하 구성. 실패하면 error에 HTML과 같은 메시지.</summary>
    public static Kinematics? Build(KinParams k, out string? error)
    {
        double u = k.Unit == "m" ? 1 : .001;
        var miss = Required.Where(n => !double.IsFinite(k[n])).ToList();
        if (miss.Count > 0)
        {
            error = "필수 값 누락: " + string.Join(", ", miss.Take(4)) + (miss.Count > 4 ? " 외 " + (miss.Count - 4) + "개" : "");
            return null;
        }
        var nz = new[] { "L2", "L3", "L4", "Lgl", "Lpl" }.Where(n => !(k[n] > 0)).ToList();
        if (nz.Count > 0) { error = "0보다 커야 하는 값: " + string.Join(", ", nz); return null; }
        string Ro(string role) => k.Roles.TryGetValue(role, out var p) && !string.IsNullOrEmpty(p) ? p : DefaultRoles[role];
        V2 Pt(string role) { var p = Ro(role); return new V2(k[p + "x"] * u, k[p + "y"] * u); }
        int Sg(string n) => k[n] < 0 ? -1 : 1;
        var g = new Kinematics(k)
        {
            Ls = k["L1"] * u, Lb = k["L2"] * u, La = k["L3"] * u, Lk = k["L4"] * u, Lgl = k["Lgl"] * u, Lpl = k["Lpl"] * u,
            P = new V2(k["L1"] * u, k["BPH"] * u),
            B0 = Pt("boomBase"), B1 = Pt("boomRod"), A0 = Pt("armBase"), A1 = Pt("armRod"), C0 = Pt("bktBase"), Gp = Pt("guide"), P4 = Pt("push"),
            SB = Sg("sB"), SA = Sg("sA"), SJ = Sg("sJ"), SP = Sg("sP"),
        };
        error = null;
        return g;
    }

    /// <summary>버켓 실린더 길이 → 버켓 상대각</summary>
    public (double Ang, bool Bad) BucketInv(double l)
    {
        var j = CircInt(Gp, Lgl, C0, l, SJ);
        var r = CylAngle(new V2(j.P.X - La, j.P.Y), P4, Lpl, SP);
        return (r.Ang, j.Bad || r.Bad);
    }

    /// <summary>버켓 상대각 → 링크 연결점 J(암 좌표)와 버켓 실린더 길이</summary>
    public (V2 J, double L, bool Ok) BucketFwd(double th)
    {
        var p4 = new V2(La, 0) + Rot(th, P4);
        foreach (var s in new[] { 1, -1 })
        {
            var j = CircInt(Gp, Lgl, p4, Lpl, s);
            if (j.Bad) continue;
            double l = Dist(j.P, C0);
            if (Math.Abs(WrapA(BucketInv(l).Ang - th)) < 1e-6) return (j.P, l, true);
        }
        var j1 = CircInt(Gp, Lgl, p4, Lpl, 1);
        return (j1.P, double.NaN, false);
    }

    /// <summary>실린더 길이 → 상대각 (i = 0 붐, 1 암, 2 버켓)</summary>
    public (double Ang, bool Bad) Inv(int i, double l) => i switch
    {
        0 => CylAngle(B0, B1, l, SB),
        1 => CylAngle(Ba, A1, l, SA),
        _ => BucketInv(l),
    };

    /// <summary>상대각 → 실린더 길이 (i = 0 붐, 1 암, 2 버켓)</summary>
    public double Fwd(int i, double th) => i switch
    {
        0 => CylLen(B0, B1, th),
        1 => CylLen(Ba, A1, th),
        _ => BucketFwd(th).L,
    };

    /// <summary>관절 i의 dL/dθ [m/rad] (가상일 원리: τ = F·dL/dθ)</summary>
    public double DLdTh(int i, double[] th)
    {
        const double h = 1e-5;
        return (Fwd(i, th[i] + h) - Fwd(i, th[i] - h)) / (2 * h);
    }

    /// <summary>순기구학</summary>
    public Pose Fk(double[] th)
    {
        double a1 = th[0], a2 = a1 + th[1], a3 = a2 + th[2];
        var p0 = P; var p1 = p0 + Rot(a1, Lb, 0); var p2 = p1 + Rot(a2, La, 0); var tip = p2 + Rot(a3, Lk, 0);
        V2 W1(V2 p) => p0 + Rot(a1, p);
        V2 W2(V2 p) => p1 + Rot(a2, p);
        var bf = BucketFwd(th[2]);
        return new Pose
        {
            A = new[] { a1, a2, a3 }, P0 = p0, P1 = p1, P2 = p2, Tip = tip,
            BoomCyl = (p0 + B0, W1(B1)), ArmCyl = (W1(A0), W2(A1)),
            BkC = W2(C0), BkJ = W2(bf.J), BkGP = W2(Gp), BkP4 = p2 + Rot(a3, P4),
        };
    }
}

/// <summary>순기구학 결과 (월드 좌표, 붐 핀 높이 포함)</summary>
public sealed class Pose
{
    public required double[] A { get; init; }
    public V2 P0 { get; init; }
    public V2 P1 { get; init; }
    public V2 P2 { get; init; }
    public V2 Tip { get; init; }
    public (V2 Base, V2 Rod) BoomCyl { get; init; }
    public (V2 Base, V2 Rod) ArmCyl { get; init; }
    public V2 BkC { get; init; }
    public V2 BkJ { get; init; }
    public V2 BkGP { get; init; }
    public V2 BkP4 { get; init; }
}
