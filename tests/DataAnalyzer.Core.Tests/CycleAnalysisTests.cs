using System.Text.Json;
using DataAnalyzer.Core.Analysis;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Tests;

/// <summary>
/// Fixtures/cycle_joystick.csv: 4개 반복 + 미완성 1개의 합성 조이스틱 데이터 (Arm은 MPa, 나머지 bar).
/// cycle_joystick_expected.json: 같은 CSV를 HTML 뷰어의 cyGuess / cyRun / cyLabel로 처리한 결과.
/// </summary>
public class CycleAnalysisTests
{
    static string Fx(string f) => Path.Combine(AppContext.BaseDirectory, "Fixtures", f);
    static readonly JsonElement Exp = JsonDocument.Parse(File.ReadAllText(Fx("cycle_joystick_expected.json"))).RootElement;
    static DataSet Data() => DataSet.FromRaw(CsvParser.ParseFile(Fx("cycle_joystick.csv")), 0);

    static List<(int, double)> Runs(double[] a)
    {
        var r = new List<(int, double)>();
        for (int i = 0; i < a.Length; i++) if (r.Count == 0 || r[^1].Item2 != a[i]) r.Add((i, a[i]));
        return r;
    }
    static List<(int, double)> ExpRuns(string key) =>
        Exp.GetProperty(key).EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetDouble())).ToList();

    static CycleConfig GuessedConfig(DataSet d)
    {
        var cfg = new CycleConfig();
        CycleAnalysis.GuessAxes(cfg, d.Names);
        return cfg;
    }

    [Fact]
    public void Guess_matches_html()
    {
        var cfg = GuessedConfig(Data());
        foreach (var (k, js) in new[] { (Axis.Boom, "bm"), (Axis.Arm, "am"), (Axis.Bucket, "bk"), (Axis.Swing, "sw") })
        {
            var e = Exp.GetProperty("guess").GetProperty(js);
            Assert.Equal(e.GetProperty("joyP").GetString(), cfg.Axes[k].JoyP);
            Assert.Equal(e.GetProperty("joyN").GetString(), cfg.Axes[k].JoyN);
            Assert.Equal(e.GetProperty("joy").GetString(), cfg.Axes[k].Joy);
            Assert.Equal(e.GetProperty("vel").GetString(), cfg.Axes[k].Vel);
        }
    }

    [Fact]
    public void Segments_and_labels_match_html()
    {
        var d = Data();
        var run = CycleAnalysis.Run(d, d.TimeSeconds(), GuessedConfig(d), out var err);
        Assert.Equal("", err);
        Assert.NotNull(run);
        Assert.Equal(Exp.GetProperty("inc").GetInt32() == 1, run!.Incomplete);
        var segs = Exp.GetProperty("segs").EnumerateArray().ToList();
        Assert.Equal(segs.Count, run.Segments.Count);
        for (int k = 0; k < segs.Count; k++)
        {
            var e = segs[k]; var q = run.Segments[k];
            Assert.Equal((e.GetProperty("s").GetInt32(), e.GetProperty("h").GetInt32(), e.GetProperty("d").GetInt32(),
                          e.GetProperty("r").GetInt32(), e.GetProperty("i").GetInt32(), e.GetProperty("e").GetInt32()),
                         (q.S, q.H, q.D, q.R, q.I, q.E));
        }
        var l0 = CycleAnalysis.Label(run);
        Assert.Equal(ExpRuns("cyc0"), Runs(l0.Cycle));
        Assert.Equal(ExpRuns("act0"), Runs(l0.Action));
        var l1 = CycleAnalysis.Label(run, 0.2);
        Assert.Equal(ExpRuns("cyc1"), Runs(l1.Cycle));
        Assert.Equal(ExpRuns("act1"), Runs(l1.Action));
        Assert.Equal(Exp.GetProperty("shift").GetDouble(), l1.Shift, 9);
        Assert.Equal(4, l0.Rows.Count);
        Assert.Equal(0, l0.NoDump);
        Assert.InRange(l0.Rows[0].Digging, 4.1, 4.3);
    }

    [Fact]
    public void Missing_swing_is_an_error()
    {
        var d = Data();
        var cfg = GuessedConfig(d);
        cfg.Axes[Axis.Swing] = new AxisSignals();
        Assert.Null(CycleAnalysis.Run(d, d.TimeSeconds(), cfg, out var err));
        Assert.Contains("Swing", err);
    }

    [Fact]
    public void Clean_fills_short_gaps_then_drops_short_runs()
    {
        var b = new byte[] { 1, 1, 0, 1, 1, 0, 0, 0, 1, 0, 0 };
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0 }, CycleAnalysis.Clean(b, 2, 2));
    }

    [Fact]
    public void Units_normalize()
    {
        Assert.Equal("mpa", Units.Normalize(" [MPa] "));
        Assert.Equal("deg", Units.Normalize("°"));
        Assert.Equal(1e6, Units.PressureToPa("MPa"));
        Assert.Null(Units.PressureToPa("mm"));
        Assert.Equal(new[] { "boom", "cyl", "head", "p", "1" }, Units.Tokens("BoomCylHeadP1"));
    }
}
