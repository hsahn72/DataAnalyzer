using System.Text.Json;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.Tests;

/// <summary>
/// excavator_expected.json: HTML 뷰어의 buildGeom / fk / dynInertial / tipForce와,
/// excavator.csv(예시 파라미터로 만든 노이즈 없는 실린더 길이·압력·선회 속도 + 펌프)를
/// exGuess → exCalcAngles → exCalcDyn → pwCalcOn → exDiagHR → ipEstimate(버켓)로 처리한 결과.
/// </summary>
public class ExcavatorTests
{
    static string Fx(string f) => Path.Combine(AppContext.BaseDirectory, "Fixtures", f);
    static readonly JsonElement Exp = JsonDocument.Parse(File.ReadAllText(Fx("excavator_expected.json"))).RootElement;

    static void Near(double e, double a, double rel = 1e-9, double abs = 1e-9)
    {
        if (double.IsNaN(e)) { Assert.True(double.IsNaN(a), $"expected NaN, got {a}"); return; }
        Assert.True(Math.Abs(e - a) <= abs + rel * Math.Abs(e), $"expected {e}, got {a}");
    }
    static double D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble();
    static void NearV(JsonElement e, V2 a, double rel = 1e-9) { Near(D(e[0]), a.X, rel); Near(D(e[1]), a.Y, rel); }
    static double[] Arr(JsonElement e) => e.EnumerateArray().Select(D).ToArray();

    static Kinematics Geom() => Kinematics.Build(KinParams.Demo(), out _)!;

    [Fact]
    public void Kinematics_matches_html()
    {
        var g = Geom(); var dp = Dynamics.Params(g);
        foreach (var k in Exp.GetProperty("kin").EnumerateArray())
        {
            var th = Arr(k.GetProperty("th"));
            var p = g.Fk(th);
            NearV(k.GetProperty("tip"), p.Tip); NearV(k.GetProperty("P1"), p.P1); NearV(k.GetProperty("P2"), p.P2);
            NearV(k.GetProperty("J"), p.BkJ); NearV(k.GetProperty("P4"), p.BkP4);
            NearV(k.GetProperty("boomRod"), p.BoomCyl.Rod); NearV(k.GetProperty("armBase"), p.ArmCyl.Base);
            var L = Arr(k.GetProperty("L")); var inv = Arr(k.GetProperty("inv")); var dl = Arr(k.GetProperty("dl"));
            for (int j = 0; j < 3; j++)
            {
                Near(L[j], g.Fwd(j, th[j]));
                Near(inv[j], g.Inv(j, L[j]).Ang, 1e-9, 1e-9);
                Near(dl[j], g.DLdTh(j, th), 1e-6, 1e-9);
            }
        }
    }

    [Fact]
    public void Dynamics_matches_html()
    {
        var g = Geom(); var dp = Dynamics.Params(g);
        var e = Exp.GetProperty("dp");
        for (int i = 0; i < 3; i++)
        {
            Near(D(e.GetProperty("m")[i]), dp.M[i]);
            NearV(e.GetProperty("c")[i], dp.C[i]);
            for (int q = 0; q < 3; q++) Near(D(e.GetProperty("I")[i][q]), dp.I[i][q]);
            var c = e.GetProperty("cyl")[i];
            Assert.Equal(c.GetProperty("N").GetInt32(), dp.Cyl[i].N);
            Near(D(c.GetProperty("AH")), dp.Cyl[i].AH); Near(D(c.GetProperty("AR")), dp.Cyl[i].AR); Near(D(c.GetProperty("Lmax")), dp.Cyl[i].Lmax);
        }
        Near(D(e.GetProperty("sw").GetProperty("I1")), dp.Sw.I1);
        Near(D(e.GetProperty("pU")), dp.PU);

        double[][] W = { new[] { .1, -.2, .3 }, new[] { 0.0, 0, 0 }, new[] { -.3, .4, .5 }, new[] { .2, .2, -.2 }, new[] { .05, -.1, .6 } };
        double[][] A = { new[] { .5, -.3, 1 }, new[] { 0.0, 0, 0 }, new[] { 1, -2, .5 }, new[] { -.4, .3, .2 }, new[] { 2.0, 1, -1 } };
        int n = 0;
        foreach (var k in Exp.GetProperty("kin").EnumerateArray())
        {
            var th = Arr(k.GetProperty("th"));
            var r = Dynamics.Inertial(g, dp, th, W[n], A[n], true);
            var r0 = Dynamics.Inertial(g, dp, th, W[n], A[n], false);
            var tau = Arr(k.GetProperty("tau")); var tau0 = Arr(k.GetProperty("tau0"));
            for (int j = 0; j < 3; j++) { Near(tau[j], r.Tau[j], 1e-9, 1e-6); Near(tau0[j], r0.Tau[j], 1e-9, 1e-6); }
            Near(D(k.GetProperty("swJ")), Dynamics.SwingJ(dp, r));
            NearV(k.GetProperty("tf"), Dynamics.TipForce(r, new[] { 1e4, -5e3, 2e3 }), 1e-8);
            NearV(k.GetProperty("tf2"), Dynamics.TipForce(r, new[] { double.NaN, -5e3, 2e3 }), 1e-8);
            n++;
        }
    }

    [Fact]
    public void Build_reports_missing_values_like_html()
    {
        var k = KinParams.Demo(); k.Values.Remove("L2");
        Assert.Null(Kinematics.Build(k, out var e1));
        var k2 = KinParams.Demo(); k2["L3"] = 0; k2["Lpl"] = -1;
        Assert.Null(Kinematics.Build(k2, out var e2));
        Assert.Equal(Exp.GetProperty("err")[0].GetString(), e1);
        Assert.Equal(Exp.GetProperty("err")[1].GetString(), e2);
    }

    static (DataSet D, ExcavatorAnalysis Ex) Pipeline()
    {
        var d = DataSet.FromRaw(CsvParser.ParseFile(Fx("excavator.csv")), 0);
        var s = new ExcavatorSignals(); s.Guess(d); s.GuessDynamics(d);
        s.Confirmed = true; s.AngleOut = "both";
        return (d, new ExcavatorAnalysis(d, Geom(), s));
    }

    static void CompareSnap(DataSet d, JsonElement snap, double rel, double abs)
    {
        foreach (var p in snap.EnumerateObject())
        {
            Assert.True(d.Signals.ContainsKey(p.Name), "missing " + p.Name);
            var a = d.Signals[p.Name]; int i = 0;
            foreach (var v in p.Value.EnumerateArray()) { Near(D(v), a[i], rel, abs); i += 37; }
        }
    }

    [Fact]
    public void Guess_matches_html()
    {
        var (_, ex) = Pipeline(); var g = Exp.GetProperty("guess");
        for (int j = 0; j < 3; j++)
        {
            var e = g.GetProperty("links")[j]; var r = ex.S.Links[j];
            Assert.Equal(e.GetProperty("ang").GetString(), r.Ang);
            Assert.Equal(e.GetProperty("cyl").GetString(), r.Cyl);
            Assert.Equal(e.GetProperty("cu").GetString(), r.CylUnit);
            Assert.Equal(e.GetProperty("vel").GetString(), r.Vel);
        }
        foreach (var p in g.GetProperty("p").EnumerateObject()) Assert.Equal(p.Value.GetString(), ex.S.P(p.Name));
        Assert.Equal(g.GetProperty("sw").GetString(), ex.S.SwingSpeed);
        Assert.Equal(g.GetProperty("swu").GetString(), ex.S.SwingSpeedUnit);
        Assert.Equal(g.GetProperty("swa").GetProperty("col").GetString(), ex.S.SwingAngle);
    }

    [Fact]
    public void Angles_dynamics_power_match_html()
    {
        var (d, ex) = Pipeline();
        var ang = ex.CalcAngles(); ang.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("angMsg").GetString(), ang.Message);
        CompareSnap(d, Exp.GetProperty("ang"), 1e-9, 1e-9);

        Assert.Equal(Exp.GetProperty("winN").GetInt32(), ex.WinN(ex.TimeSec()));
        var dyn = ex.CalcDynamics(); dyn.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("dynMsg").GetString(), dyn.Message);
        CompareSnap(d, Exp.GetProperty("dyn"), 1e-7, 1e-7);

        var pc = PowerCalc.Guess(d, ex.S, ex.G.K);
        var pw = PowerCalc.Calc(d, pc, ex, ex.TimeSec(), ex.WinN(ex.TimeSec())); pw.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("pwOut").EnumerateArray().Select(x => x.GetString()), pw.Outputs.Select(o => o.Name));
        CompareSnap(d, Exp.GetProperty("pw"), 1e-7, 1e-7);

        var (txt, err) = ex.DiagnoseHeadRod(10, 30, x => Format.Precision(x, 10));
        Assert.False(err);
        Assert.Equal(Exp.GetProperty("diag").GetString(), txt);
    }

    [Fact]
    public void Inertial_estimation_matches_html()
    {
        var (d, ex) = Pipeline();
        ex.CalcAngles().ApplyTo(d);
        var cur = new Dictionary<int, InertialSet>();
        foreach (var (key, excl, thr) in new[] { ("ip4", false, 2.0), ("ip4b", true, 50.0) })
        {
            var cfg = new InertialConfig { M = 700, Lo = -120, Hi = 10, ExcludeOthers = excl, Threshold = thr };
            var (r, outp, error) = InertialEstimation.Estimate(ex, 4, cfg, cur, "data.csv");
            Assert.Null(error);
            var e = Exp.GetProperty(key);
            Assert.Equal(e.GetProperty("n").GetInt32(), r!.N);
            Assert.Equal(e.GetProperty("nOut").GetInt32(), r.Outliers);
            foreach (var (js, v) in new[] { ("cx", r.Cx), ("cy", r.Cy), ("Ipiv", r.Ipiv), ("Icom", r.Icom), ("fc", r.Fc), ("rms", r.Rms), ("r2", r.R2), ("sdx", r.SdX), ("seJ", r.SeJ), ("cond", r.Cond) })
                Near(D(e.GetProperty(js)), v, 1e-6, 1e-9);
            Assert.Equal(e.GetProperty("wn").EnumerateArray().Select(x => x.GetString()), r.Warnings);
            if (key == "ip4") Assert.Equal(Exp.GetProperty("ipMsg").GetString(), outp.Message);
        }
    }

    [Fact]
    public void PoseAt_sample_times_matches_angle_conversion()
    {
        var (d, ex) = Pipeline();
        ex.CalcAngles().ApplyTo(d);
        var tx = d.Signals["Tip_X_m"]; var ty = d.Signals["Tip_Y_m"]; int checkedN = 0;
        for (int i = 0; i < d.Count; i += 97)
        {
            var p = ExcavatorPose.At(ex.G, ex.S, d, d.Time[i]);
            if (double.IsNaN(tx[i])) { Assert.False(p.Ok); continue; }
            Assert.True(p.Ok);
            Assert.Equal(tx[i], p.Fk!.Tip.X, 9); Assert.Equal(ty[i], p.Fk.Tip.Y, 9);
            checkedN++;
        }
        Assert.True(checkedN > 5);
    }
}
