using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.WheelLoader;

/// <summary>휠로더 Z-bar 자세 (월드 좌표, 원점 = Lift Arm 회전 중심)</summary>
public sealed class WlPose
{
    public bool Bad { get; init; }
    public V2 B { get; init; }
    public V2 P31 { get; init; }
    public V2 P41 { get; init; }
    public V2 Tip { get; init; }
    public V2 Gw { get; init; }
    public V2 PQ { get; init; }
    public V2 PP { get; init; }
    public double Phi { get; init; } = double.NaN;
    public double La { get; init; } = double.NaN;
    public double Lb { get; init; } = double.NaN;
    /// <summary>Lift Arm 각도 θ3 (수평 기준)</summary>
    public double T2 { get; init; }
    /// <summary>Bucket 상대각 θ4 (Lift Arm 기준)</summary>
    public double T3 { get; init; } = double.NaN;
    /// <summary>Tilt Lever 상대각 α</summary>
    public double Tl { get; init; } = double.NaN;
}

/// <summary>중력 부품 하나</summary>
public sealed record WlComp(string Key, double M, V2 C);

/// <summary>핀 반력 [N] (월드). P40 Arm→실린더, PP 실린더→Tilt Lever, PGL Arm→Tilt Lever, PQ Tilt Lever→Push Link, P41 Push Link→Bucket</summary>
public sealed record WlPinForces(V2 P40, V2 PP, V2 PGL, V2 PQ, V2 P41, double TqB)
{
    public static readonly string[] Keys = { "P40", "PGL", "PP", "PQ", "P41" };
    public V2 this[string k] => k switch { "P40" => P40, "PGL" => PGL, "PP" => PP, "PQ" => PQ, _ => P41 };
}

/// <summary>
/// 휠로더 Front Attachment(Z-bar) 기구학·정역학 (HTML의 WLX: wlGeom, wlPose, wlPoseTL, armAng, bktAng, wlComps, wlGravTq, wlDL, wlPinForces).
/// 파라미터는 KinParams(숫자 값 + 문자열 sA/sP/sJ/sK = "+"/"-")를 쓴다.
/// </summary>
public sealed class WlGeom
{
    public const double Grav = 9.80665;
    const double Rad = Kinematics.Rad;

    public KinParams K { get; }
    public double U { get; private init; }
    public double L3 { get; private init; }
    public double L4 { get; private init; }
    public double Lgl { get; private init; }
    public double Lql { get; private init; }
    public double Lpl { get; private init; }
    public double Psi { get; private init; }
    public V2 P30 { get; private init; }
    public V2 P31 { get; private init; }
    public V2 P40 { get; private init; }
    public V2 Gc { get; private init; }
    public V2 P41 { get; private init; }
    public int SA { get; set; }
    public int SP { get; set; }
    public int SJ { get; set; }
    public int SK { get; set; }
    public string? Error { get; private set; }
    double a20, al20, b21, be21, b41, be41;

    WlGeom(KinParams k) { K = k; }

    static double Cr(V2 a, V2 b) => a.X * b.Y - a.Y * b.X;
    static double Hyp(V2 a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);
    static V2 Rot(double a, V2 p) => Kinematics.Rot(a, p);
    static V2 Mul(V2 a, double s) => new(a.X * s, a.Y * s);
    static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

    /// <summary>두 원 교점 (교점이 없으면 null)</summary>
    static V2? Cc(V2 c0, double r0, V2 c1, double r1, int s)
    {
        double dx = c1.X - c0.X, dy = c1.Y - c0.Y, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12 || d > r0 + r1 + 1e-12 || d < Math.Abs(r0 - r1) - 1e-12) return null;
        double a = (r0 * r0 - r1 * r1 + d * d) / (2 * d), h = Math.Sqrt(Math.Max(0, r0 * r0 - a * a)), mx = c0.X + a * dx / d, my = c0.Y + a * dy / d;
        return new V2(mx - s * h * dy / d, my + s * h * dx / d);
    }

    public static WlGeom Build(KinParams k)
    {
        string unit = k.Str("unit", "m");
        double u = unit == "mm" ? .001 : unit == "cm" ? .01 : 1;
        double L(string n) => k[n] * u;
        V2 P(string a, string b) => new(L(a), L(b));
        int Sg(string n) => k.Str(n) == "-" ? -1 : 1;
        var g = new WlGeom(k)
        {
            U = u, L3 = L("L3"), L4 = L("L4"), Lgl = L("Lgl"), Lql = L("Lql"), Lpl = L("Lpl"), Psi = k["psiTL"] * Rad,
            P30 = P("P30x", "P30y"), P31 = P("P31x", "P31y"), P40 = P("P40x", "P40y"), Gc = P("PGLx", "PGLy"), P41 = P("P41x", "P41y"),
            SA = Sg("sA"), SP = Sg("sP"), SJ = Sg("sJ"), SK = Sg("sK"),
        };
        bool Fin(V2 v) => double.IsFinite(v.X) && double.IsFinite(v.Y);
        var miss = new List<string>();
        foreach (var (name, ok) in new (string, bool)[]
        {
            ("L3", double.IsFinite(g.L3)), ("L4", double.IsFinite(g.L4)), ("Lgl", double.IsFinite(g.Lgl)), ("Lql", double.IsFinite(g.Lql)), ("Lpl", double.IsFinite(g.Lpl)),
            ("psiTL", double.IsFinite(g.Psi)), ("P30", Fin(g.P30)), ("P31", Fin(g.P31)), ("P40", Fin(g.P40)), ("PGL", Fin(g.Gc)), ("P41", Fin(g.P41)),
        }) if (!ok) miss.Add(name);
        if (miss.Count > 0) g.Error = "필수 값이 비어 있습니다: " + string.Join(", ", miss);
        else
        {
            g.a20 = Hyp(g.P30); g.al20 = Math.Atan2(g.P30.Y, g.P30.X); g.b21 = Hyp(g.P31); g.be21 = Math.Atan2(g.P31.Y, g.P31.X); g.b41 = Hyp(g.P41); g.be41 = Math.Atan2(g.P41.Y, g.P41.X);
            if (!(g.a20 > 0 && g.b21 > 0 && g.b41 > 0)) g.Error = "P30, P31, P41은 원점(관절)과 달라야 합니다.";
        }
        return g;
    }

    /// <summary>Lift Arm 각도 → Arm 실린더 길이</summary>
    public double ArmLen(double t2) => Hyp(Rot(t2, P31) - P30);

    /// <summary>Arm 실린더 길이 → Lift Arm 각도 (해 없으면 NaN)</summary>
    public double ArmAng(double la)
    {
        double c = (a20 * a20 + b21 * b21 - la * la) / (2 * a20 * b21);
        if (!(Math.Abs(c) <= 1 + 1e-9)) return double.NaN;
        return al20 - be21 + SA * Math.Acos(Math.Max(-1, Math.Min(1, c)));
    }

    V2 BOf(double t2) => new(L3 * Math.Cos(t2), L3 * Math.Sin(t2));

    /// <summary>θ3, θ4 → 자세</summary>
    public WlPose Pose(double t2, double t3)
    {
        var B = BOf(t2); var p31 = Rot(t2, P31); var gw = Rot(t2, Gc); double a3 = t2 + t3;
        var p41 = B + Rot(a3, P41); var tip = B + Rot(a3, new V2(L4, 0));
        V2? pq = null;
        foreach (var s in new[] { 1, -1 })
        {
            var q = Cc(gw, Lql, p41, Lpl, s);
            if (q is { } qq && Cr(p41 - qq, qq - gw) * SJ > 0) { pq = qq; break; }
        }
        if (pq is not { } PQ) return new WlPose { Bad = true, B = B, P31 = p31, P41 = p41, Tip = tip, Gw = gw, T2 = t2, T3 = t3 };
        double phi = Math.Atan2(PQ.Y - gw.Y, PQ.X - gw.X) - Psi; var PP = gw + new V2(Lgl * Math.Cos(phi), Lgl * Math.Sin(phi));
        return new WlPose { B = B, P31 = p31, P41 = p41, Tip = tip, Gw = gw, PQ = PQ, PP = PP, Phi = phi, La = Hyp(p31 - P30), Lb = Hyp(PP - P40), T2 = t2, T3 = t3, Tl = Wrap(phi - t2) };
    }

    (double T3, V2 PQ, V2 P41)? BktFromPhi(double phi, double t2)
    {
        var gw = Rot(t2, Gc); var PQ = gw + new V2(Lql * Math.Cos(phi + Psi), Lql * Math.Sin(phi + Psi)); var B = BOf(t2);
        foreach (var s in new[] { 1, -1 })
        {
            var p = Cc(B, b41, PQ, Lpl, s);
            if (p is { } pp && Cr(pp - B, PQ - B) * SK > 0) return (Wrap(Math.Atan2(pp.Y - B.Y, pp.X - B.X) - t2 - be41), PQ, pp);
        }
        return null;
    }

    /// <summary>Bucket 실린더 길이 + Lift Arm 각도 → Bucket 상대각 (해 없으면 NaN)</summary>
    public double BktAng(double lb, double t2)
    {
        var gw = Rot(t2, Gc); V2? PP = null;
        foreach (var s in new[] { 1, -1 })
        {
            var q = Cc(P40, lb, gw, Lgl, s);
            if (q is { } qq && Cr(qq - P40, qq - gw) * SP > 0) { PP = qq; break; }
        }
        if (PP is not { } pp) return double.NaN;
        var r = BktFromPhi(Math.Atan2(pp.Y - gw.Y, pp.X - gw.X), t2);
        return r?.T3 ?? double.NaN;
    }

    /// <summary>Lift Arm 각도 + Tilt Lever 상대각 α → 자세 (Bucket 각은 내부에서 구함)</summary>
    public WlPose PoseTL(double t2, double al)
    {
        double phi = t2 + al; var r = BktFromPhi(phi, t2);
        if (r is not { } rr) return new WlPose { Bad = true, T2 = t2, Tl = al };
        var B = BOf(t2); var p31 = Rot(t2, P31); var gw = Rot(t2, Gc); var PP = gw + new V2(Lgl * Math.Cos(phi), Lgl * Math.Sin(phi)); var tip = B + Rot(t2 + rr.T3, new V2(L4, 0));
        return new WlPose { B = B, P31 = p31, P41 = rr.P41, Tip = tip, Gw = gw, PQ = rr.PQ, PP = PP, Phi = phi, La = Hyp(p31 - P30), Lb = Hyp(PP - P40), T2 = t2, T3 = rr.T3, Tl = Wrap(al) };
    }

    /// <summary>자세의 Tilt Lever→Push Link 분기 부호</summary>
    public static int SJOf(WlPose p) => Math.Sign(Cr(p.P41 - p.PQ, p.PQ - p.Gw));

    /// <summary>중력 부품 목록 (질량 [kg], 월드 위치). m은 질량 파라미터 (Lift Arm·Bucket 몸체를 빼려면 NoBody)</summary>
    public List<WlComp> Comps(KinParams m, WlPose po)
    {
        static V2 Un(V2 a, V2 b) { var d = b - a; double l = Hyp(d); return new V2(d.X / l, d.Y / l); }
        double M(string k) { double v = m[k]; return double.IsFinite(v) && v > 0 ? v : 0; }
        double Cd(string k, double d) { double v = m[k]; return double.IsFinite(v) ? v * U : d; }
        double na0 = m["AmN"], nb0 = m["BkN"];
        int Na = Math.Max(1, (int)Math.Floor((na0 == 0 || double.IsNaN(na0) ? 2 : na0) + .5)), Nb = Math.Max(1, (int)Math.Floor((nb0 == 0 || double.IsNaN(nb0) ? 1 : nb0) + .5));
        var ub = Un(P40, po.PP); var ua = Un(P30, po.P31); var up = Un(po.PQ, po.P41); var L = new List<WlComp>();
        void A(string key, double mass, V2 c) { if (mass > 0) L.Add(new WlComp(key, mass, c)); }
        A("BktCylTube", M("BkTubeM") * Nb, P40 + Mul(ub, Cd("BkTubeC", .25 * po.Lb)));
        A("BktCylRod", M("BkRodM") * Nb, po.PP - Mul(ub, Cd("BkRodC", .25 * po.Lb)));
        A("TiltLever", M("TLM"), po.Gw + Rot(po.Phi, new V2(Cd("TLcx", 0), Cd("TLcy", 0))));
        A("PushLink", M("PLM"), po.PQ + Mul(up, Cd("PLc", .5 * Lpl)));
        A("ArmCylTube", M("AmTubeM") * Na, P30 + Mul(ua, Cd("AmTubeC", .25 * po.La)));
        A("ArmCylRod", M("AmRodM") * Na, po.P31 - Mul(ua, Cd("AmRodC", .25 * po.La)));
        A("LiftArm", M("LAM"), Rot(po.T2, new V2(Cd("LAcx", 0), Cd("LAcy", 0))));
        A("PinPP", M("PinPPM"), po.PP); A("PinPQ", M("PinPQM"), po.PQ); A("PinP41", M("PinP41M"), po.P41);
        A("BucketBody", M("BKM"), po.B + Rot(po.T2 + po.T3, new V2(Cd("BKcx", 0), Cd("BKcy", 0))));
        return L;
    }

    /// <summary>Lift Arm·Bucket 몸체 질량을 뺀 파라미터 (HTML wlNoBody)</summary>
    public static KinParams NoBody(KinParams k) { var c = k.Clone(); c.Values.Remove("LAM"); c.Values.Remove("BKM"); return c; }

    /// <summary>부품별 중력 등가 토크 [N·m]: (θ3에 작용, θ4에 작용), 각도 증가 방향 +</summary>
    public Dictionary<string, (double T3, double T4)> GravTq(KinParams m, double t2, double t3)
    {
        const double h = 1e-5;
        List<WlComp> P(double a, double b) => Comps(m, Pose(a, b));
        var p2 = P(t2 + h, t3); var m2 = P(t2 - h, t3); var p3 = P(t2, t3 + h); var m3 = P(t2, t3 - h);
        var o = new Dictionary<string, (double, double)>();
        for (int i = 0; i < p2.Count; i++)
        {
            var c = p2[i];
            o[c.Key] = (-c.M * Grav * (c.C.Y - m2[i].C.Y) / (2 * h), -c.M * Grav * (p3[i].C.Y - m3[i].C.Y) / (2 * h));
        }
        return o;
    }

    /// <summary>실린더 길이의 각도 미분 [m/rad]: dLa/dθ3, dLb/dθ3, dLb/dθ4</summary>
    public (double DLa, double DLb2, double DLb3) DL(double t2, double t3)
    {
        const double h = 1e-6;
        double Lb(double a, double b) => Pose(a, b).Lb;
        return (a20 * b21 * Math.Sin(t2 + be21 - al20) / ArmLen(t2), (Lb(t2 + h, t3) - Lb(t2 - h, t3)) / (2 * h), (Lb(t2, t3 + h) - Lb(t2, t3 - h)) / (2 * h));
    }

    /// <summary>핀 반력 (정적: 자중 + Bucket 실린더 유압력 F[N], 신장 +). 관성 무시.</summary>
    public WlPinForces? PinForces(KinParams m, WlPose p, double F)
    {
        var cm = Comps(m, p);
        WlComp? W(string k) => cm.FirstOrDefault(x => x.Key == k);
        static V2 Wt(WlComp c) => new(0, -c.M * Grav);
        static double Dot(V2 a, V2 b) => a.X * b.X + a.Y * b.Y;
        var ub0 = p.PP - P40; double Lb = Hyp(ub0);
        if (!(Lb > 1e-9) || double.IsNaN(F)) return null;
        var ub = new V2(ub0.X / Lb, ub0.Y / Lb); var nb = new V2(-ub.Y, ub.X);
        V2 Ws = default; double Mo = 0;
        foreach (var k in new[] { "BktCylTube", "BktCylRod" }) if (W(k) is { } c) { var f = Wt(c); Ws += f; Mo += Cr(c.C - P40, f); }
        var rod = W("BktCylRod");
        double Qa = -(F + (rod != null ? Dot(Wt(rod), ub) : 0)), Qt = -Mo / Lb;
        var RPP = new V2(Qa * ub.X + Qt * nb.X, Qa * ub.Y + Qt * nb.Y); var R40 = new V2(-(RPP.X + Ws.X), -(RPP.Y + Ws.Y)); var Fp = new V2(-RPP.X, -RPP.Y);
        var u0 = p.P41 - p.PQ; double Lp = Hyp(u0);
        if (!(Lp > 1e-9)) return null;
        var u = new V2(u0.X / Lp, u0.Y / Lp); var nl = new V2(-u.Y, u.X);
        V2 Wl = default; double Ml = 0;
        foreach (var k in new[] { "PushLink", "PinPQ", "PinP41" }) if (W(k) is { } c) { var f = Wt(c); Wl += f; Ml += Cr(c.C - p.PQ, f); }
        double Rbt = -Ml / Lp; V2 Wtl = default; double Mg = 0;
        foreach (var k in new[] { "TiltLever", "PinPP" }) if (W(k) is { } c) { var f = Wt(c); Wtl += f; Mg += Cr(c.C - p.Gw, f); }
        var rq = p.PQ - p.Gw; double den = Cr(rq, u);
        if (Math.Abs(den) < 1e-6 * Hyp(rq)) return null;
        double Rba = -(Cr(p.PP - p.Gw, Fp) + Cr(rq, new V2(Rbt * nl.X + Wl.X, Rbt * nl.Y + Wl.Y)) + Mg) / den;
        var Rb = new V2(Rba * u.X + Rbt * nl.X, Rba * u.Y + Rbt * nl.Y); var Fq = Rb + Wl;
        var Rg = new V2(-(Fp.X + Fq.X + Wtl.X), -(Fp.Y + Fq.Y + Wtl.Y)); var P41F = new V2(-Rb.X, -Rb.Y);
        return new WlPinForces(R40, Fp, Rg, new V2(-Fq.X, -Fq.Y), P41F, Cr(p.P41 - p.B, P41F));
    }

    /// <summary>예시 휠로더 파라미터 (HTML WLDEMO)</summary>
    public static KinParams Demo()
    {
        var k = new KinParams();
        k.Text["name"] = "WL-Demo"; k.Text["unit"] = "m";
        var v = new Dictionary<string, double>
        {
            ["L3"] = 2.6, ["L4"] = 1.5, ["Lgl"] = .81, ["Lql"] = .43, ["psiTL"] = 134, ["Lpl"] = 1.08, ["P30x"] = .54, ["P30y"] = -1.02, ["P31x"] = 1.55, ["P31y"] = -.39,
            ["P40x"] = .32, ["P40y"] = .36, ["PGLx"] = 1.81, ["PGLy"] = .91, ["P41x"] = .16, ["P41y"] = .29,
            ["AmLmin"] = 1.05, ["AmLmax"] = 2.05, ["AmDt"] = .14, ["AmDr"] = .08, ["AmN"] = 2, ["BkLmin"] = 1.15, ["BkLmax"] = 2.15, ["BkDt"] = .16, ["BkDr"] = .09, ["BkN"] = 1,
            ["AmTubeM"] = 90, ["AmTubeC"] = .5, ["AmRodM"] = 55, ["AmRodC"] = .5, ["BkTubeM"] = 60, ["BkTubeC"] = .6, ["BkRodM"] = 35, ["BkRodC"] = .6, ["TLM"] = 80, ["TLcx"] = .3, ["TLcy"] = .1,
            ["PLM"] = 45, ["PLc"] = .5, ["LAM"] = 1800, ["LAcx"] = 1.2, ["LAcy"] = .05, ["BKM"] = 900, ["BKcx"] = .75, ["BKcy"] = .2,
            ["FrAc"] = 1, ["FrAv"] = 7.5, ["FrAp"] = 2, ["FrBc"] = 1, ["FrBv"] = 8, ["FrBp"] = 2,
        };
        foreach (var (a, b) in v) k.Values[a] = b;
        k.Text["sA"] = "+"; k.Text["sP"] = "+"; k.Text["sJ"] = "-"; k.Text["sK"] = "+";
        return k;
    }
}
