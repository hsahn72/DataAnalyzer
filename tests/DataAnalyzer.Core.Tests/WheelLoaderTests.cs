using System.Text.Json;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;
using DataAnalyzer.Core.WheelLoader;

namespace DataAnalyzer.Core.Tests;

/// <summary>
/// wheelloader_expected.json: HTML 뷰어의 WLX(wlPose, wlPoseTL, armAng, bktAng, wlDL, wlGravTq, wlPinForces)와,
/// wheelloader.csv(예시 Z-bar의 Lift Arm·Tilt Lever 각도 + 실린더 압력)를 wlGuess → wlAngTqOn('ang'/'tq')로 처리한 결과.
/// 마지막은 θ4 열 직접 사용 + 센서 오차 주입 상태의 토크.
/// </summary>
public class WheelLoaderTests
{
    static string Fx(string f) => Path.Combine(AppContext.BaseDirectory, "Fixtures", f);
    static readonly JsonElement Exp = JsonDocument.Parse(File.ReadAllText(Fx("wheelloader_expected.json"))).RootElement;

    static void Near(double e, double a, double rel = 1e-9, double abs = 1e-9)
    {
        if (double.IsNaN(e)) { Assert.True(double.IsNaN(a), $"expected NaN, got {a}"); return; }
        Assert.True(Math.Abs(e - a) <= abs + rel * Math.Abs(e), $"expected {e}, got {a}");
    }
    static double D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble();
    static void NearV(JsonElement e, V2 a, double rel = 1e-9, double abs = 1e-9) { Near(D(e[0]), a.X, rel, abs); Near(D(e[1]), a.Y, rel, abs); }

    static KinParams Params()
    {
        var k = WlGeom.Demo(); k["PinPPM"] = 12; k["PinPQM"] = 8; k["PinP41M"] = 10;
        return k;
    }

    [Fact]
    public void Kinematics_and_statics_match_html()
    {
        var K = Params(); var g = WlGeom.Build(K);
        Assert.Null(g.Error);
        foreach (var e in Exp.GetProperty("kin").EnumerateArray())
        {
            double t2 = D(e.GetProperty("t2")), t3 = D(e.GetProperty("t3"));
            var p = g.Pose(t2, t3);
            if (e.TryGetProperty("bad", out _)) { Assert.True(p.Bad); continue; }
            Assert.False(p.Bad);
            NearV(e.GetProperty("B"), p.B); NearV(e.GetProperty("PQ"), p.PQ); NearV(e.GetProperty("PP"), p.PP); NearV(e.GetProperty("tip"), p.Tip);
            Near(D(e.GetProperty("La")), p.La); Near(D(e.GetProperty("Lb")), p.Lb); Near(D(e.GetProperty("tl")), p.Tl); Near(D(e.GetProperty("phi")), p.Phi);
            Near(D(e.GetProperty("tlT3")), g.PoseTL(t2, p.Tl).T3);
            Near(D(e.GetProperty("arm")), g.ArmAng(p.La));
            Near(D(e.GetProperty("bkt")), g.BktAng(p.Lb, t2));
            Assert.Equal(e.GetProperty("sj").GetInt32(), WlGeom.SJOf(p));
            var dl = g.DL(t2, t3); var edl = e.GetProperty("dl");
            Near(D(edl.GetProperty("dLa")), dl.DLa); Near(D(edl.GetProperty("dLb2")), dl.DLb2, 1e-6); Near(D(edl.GetProperty("dLb3")), dl.DLb3, 1e-6);
            var q = g.GravTq(K, t2, t3);
            foreach (var c in e.GetProperty("q").EnumerateObject())
            {
                Near(D(c.Value[0]), q[c.Name].T3, 1e-6, 1e-6); Near(D(c.Value[1]), q[c.Name].T4, 1e-6, 1e-6);
            }
            Assert.Equal(e.GetProperty("q").EnumerateObject().Count(), q.Count);
            var fp = g.PinForces(K, p, 150000)!; var efp = e.GetProperty("fp");
            foreach (var k in WlPinForces.Keys) NearV(efp.GetProperty(k), fp[k], 1e-9, 1e-6);
            Near(D(efp.GetProperty("tqB")), fp.TqB, 1e-9, 1e-6);
        }
    }

    [Fact]
    public void Geometry_errors_match_html()
    {
        var a = WlGeom.Demo(); a.Values.Remove("L3"); a.Values.Remove("PGLx");
        var b = WlGeom.Demo(); b["P31x"] = 0; b["P31y"] = 0;
        Assert.Equal(Exp.GetProperty("err")[0].GetString(), WlGeom.Build(a).Error);
        Assert.Equal(Exp.GetProperty("err")[1].GetString(), WlGeom.Build(b).Error);
    }

    static void CompareSnap(DataSet d, JsonElement snap, double rel, double abs)
    {
        foreach (var p in snap.EnumerateObject())
        {
            Assert.True(d.Signals.ContainsKey(p.Name), "missing " + p.Name);
            var a = d.Signals[p.Name]; int i = 0;
            foreach (var v in p.Value.EnumerateArray()) { Near(D(v), a[i], rel, abs); i += 29; }
        }
    }

    [Fact]
    public void Angle_and_torque_series_match_html()
    {
        var d = DataSet.FromRaw(CsvParser.ParseFile(Fx("wheelloader.csv")), 0);
        var K = Params(); var s = WlSignals.Guess(d); s.AngleOut = "both";
        var sig = Exp.GetProperty("sig");
        foreach (var (js, v) in new[] { ("aA", s.AA), ("bA", s.BA), ("aH", s.AH), ("aR", s.AR), ("bH", s.BH), ("bR", s.BR), ("aPu", s.APu), ("bPu", s.BPu), ("aAu", s.AAu) })
            Assert.Equal(sig.GetProperty(js).GetString(), v);

        var ang = WlAnalysis.AngTorque(d, K, s, "ang"); ang.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("angOut").EnumerateArray().Select(x => x.GetString()), ang.Outputs.Select(o => o.Name));
        CompareSnap(d, Exp.GetProperty("ang"), 1e-9, 1e-9);

        var tq = WlAnalysis.AngTorque(d, K, s, "tq"); tq.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("tqOut").EnumerateArray().Select(x => x.GetString()), tq.Outputs.Select(o => o.Name));
        Assert.False(tq.Warn);
        CompareSnap(d, Exp.GetProperty("tq"), 1e-6, 1e-6);

        s.T4Mode = "col"; s.T4Src = "WL_Bkt_AngRel_deg"; s.T4u = "deg";
        K.Text["ErrOn"] = "on"; K["Eg_aA"] = 2; K["Eo_t4"] = -3; K["Eg_bH"] = 1.5; K["Eo_bR"] = 4;
        WlAnalysis.AngTorque(d, K, s, "tq").ApplyTo(d);
        CompareSnap(d, Exp.GetProperty("tq2"), 1e-6, 1e-6);
    }
}

/// <summary>
/// wheelloader_est_expected.json: 같은 CSV를 HTML의 wlEstBkt → wlEstArm → wlWeighOn → wlLabelRun으로 처리하고,
/// Static·방향 선별·가중 없음·부호 제약 없음·일부 고정 조건으로 wlEstBkt를 다시 실행한 결과.
/// </summary>
public class WheelLoaderEstimationTests
{
    static string Fx(string f) => Path.Combine(AppContext.BaseDirectory, "Fixtures", f);
    static readonly JsonElement Exp = JsonDocument.Parse(File.ReadAllText(Fx("wheelloader_est_expected.json"))).RootElement;
    static double D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? double.NaN : e.GetDouble();
    static void Near(double e, double a, double rel, double abs, string what = "")
    {
        if (double.IsNaN(e)) { Assert.True(double.IsNaN(a), $"{what}: expected NaN, got {a}"); return; }
        Assert.True(Math.Abs(e - a) <= abs + rel * Math.Abs(e), $"{what}: expected {e}, got {a}");
    }
    static void Snap(DataSet d, JsonElement snap, double rel, double abs)
    {
        foreach (var p in snap.EnumerateObject())
        {
            Assert.True(d.Signals.ContainsKey(p.Name), "missing " + p.Name);
            var a = d.Signals[p.Name]; int i = 0;
            foreach (var v in p.Value.EnumerateArray()) { Near(D(v), a[i], rel, abs, p.Name + "[" + i + "]"); i += 23; }
        }
    }
    static void Keys(KinParams k, JsonElement e, double rel)
    {
        foreach (var p in e.EnumerateObject()) Near(D(p.Value), k[p.Name], rel, 1e-9, p.Name);
    }

    [Fact]
    public void Estimation_weighing_labeling_match_html()
    {
        var d = DataSet.FromRaw(CsvParser.ParseFile(Fx("wheelloader.csv")), 0);
        var K = WlGeom.Demo(); K["PinPPM"] = 12; K["PinPQM"] = 8; K["PinP41M"] = 10;
        K["T4min"] = -40; K["T4max"] = 0; K["T3min"] = -5; K["T3max"] = 28; K["PYx"] = .7; K["PYy"] = .35;
        var s = WlSignals.Guess(d); var st = new WlEstState();
        WlAnalysis.AngTorque(d, K, s, "ang").ApplyTo(d);
        WlAnalysis.AngTorque(d, K, s, "tq").ApplyTo(d);

        var r1 = WlEstimation.EstimateBucket(d, K, s, st); r1.ApplyTo(d);
        Assert.True(r1.Ok, r1.Message);
        var eb = Exp.GetProperty("bkt");
        for (int j = 0; j < 10; j++) Near(D(eb.GetProperty(new[] { "X", "Y", "J", "c", "p1", "p2", "v", "d", "jc", "jf" }[j])[0]), st.Bkt!.X[j], 1e-6, 1e-6, "bkt" + j);
        Near(D(eb.GetProperty("rms1")), st.Bkt!.Rms1, 1e-6, 1e-6); Near(D(eb.GetProperty("eps")), st.Bkt.Eps, 1e-9, 1e-12);
        Assert.Equal(eb.GetProperty("n").GetInt32(), st.Bkt.N);
        Keys(K, Exp.GetProperty("K1"), 1e-4);
        Snap(d, Exp.GetProperty("s1"), 1e-6, 1e-6);

        var r2 = WlEstimation.EstimateArm(d, K, s, st); r2.ApplyTo(d);
        Assert.True(r2.Ok, r2.Message);
        var ea = Exp.GetProperty("arm");
        Assert.Equal(ea.GetProperty("n").GetInt32(), st.Arm!.N);
        Near(D(ea.GetProperty("P")[0]), st.Arm.X[0], 1e-6, 1e-6); Near(D(ea.GetProperty("Q")[0]), st.Arm.X[1], 1e-6, 1e-6); Near(D(ea.GetProperty("v")[0]), st.Arm.X[5], 1e-6, 1e-6);
        Near(D(ea.GetProperty("rms1")), st.Arm.Rms1, 1e-6, 1e-6);
        Keys(K, Exp.GetProperty("K2"), 1e-4);
        Snap(d, Exp.GetProperty("s2"), 1e-6, 1e-6);

        var w = WlEstimation.Weigh(d, K, s, 5, 20, "커서 구간"); w.ApplyTo(d);
        Assert.Equal(Exp.GetProperty("wOut").EnumerateArray().Select(x => x.GetString()), w.Outputs.Select(o => o.Name));
        Snap(d, Exp.GetProperty("s3"), 1e-6, 1e-4);
        var stt = WlEstimation.Stat(d.Signals["WL_Payload_kg"], d.Time, 5, 20)!; var es = Exp.GetProperty("stat");
        Assert.Equal(es.GetProperty("n").GetInt32(), stt.N); Near(D(es.GetProperty("med")), stt.Med, 1e-6, 1e-4);

        var lab = WlEstimation.Label(d, K, s, true, false); lab.ApplyTo(d);
        Assert.StartsWith(Exp.GetProperty("lab").GetProperty("t").GetString()!.Split('\n')[0], lab.Message);
        Snap(d, Exp.GetProperty("s4"), 0, 0);

        K.Text["BkMode"] = "static"; K.Text["BkSel"] = "on"; K.Text["BkNN"] = "free";
        K["BkC0"] = -40; K["BkC1"] = -2; K["BkD0"] = -38; K["BkD1"] = 0; K["WTwin"] = 0; K["IRLSn"] = 0; K["uF4p2"] = 150;
        st.RegOn["uF4p2"] = false; st.RegOn["uFjc"] = true; st.DirInv = true;
        var r3 = WlEstimation.EstimateBucket(d, K, s, st); r3.ApplyTo(d);
        Assert.True(r3.Ok, r3.Message);
        var e2 = Exp.GetProperty("bkt2");
        Assert.Equal(e2.GetProperty("on").EnumerateArray().Select(x => x.GetBoolean()), st.Bkt!.On);
        Assert.Equal(e2.GetProperty("n").GetInt32(), st.Bkt.N);
        for (int j = 0; j < 10; j++) Near(D(e2.GetProperty(new[] { "X", "Y", "J", "c", "p1", "p2", "v", "d", "jc", "jf" }[j])[0]), st.Bkt.X[j], 1e-4, 1e-4, "bkt2-" + j);
        Keys(K, Exp.GetProperty("K3"), 1e-4);
        Snap(d, Exp.GetProperty("s5"), 1e-6, 1e-6);
    }
}
