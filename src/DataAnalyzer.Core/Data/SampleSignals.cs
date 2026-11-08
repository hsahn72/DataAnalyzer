namespace DataAnalyzer.Core.Data;

/// <summary>화면 확인용 샘플 데이터 (HTML의 '샘플 데이터'와 같은 신호).</summary>
public static class SampleSignals
{
    public static RawTable Create(int n = 20000, int seed = 1)
    {
        var rnd = new Random(seed);
        double Noise(double amp) => (rnd.NextDouble() - .5) * amp;
        var t = new double[n];
        var c = Enumerable.Range(0, 7).Select(_ => new double[n]).ToArray();
        for (int i = 0; i < n; i++)
        {
            double s = i * .01;
            t[i] = s;
            double sp = Math.Floor(s / 25) % 2 != 0 ? 60 : 20;
            c[0][i] = 150 + 40 * Math.Sin(s * .8) + Noise(4);
            c[1][i] = 30 + 12 * Math.Sin(s * .8 + 1) + Noise(1.5);
            c[2][i] = 35 + s * .12 + Noise(.3);
            c[3][i] = sp + 30 * Math.Sin(s * .25) + 30;
            c[4][i] = sp + 30;
            c[5][i] = 8 + 3 * Math.Abs(Math.Sin(s * .8)) + Noise(.4);
            c[6][i] = 50 + 45 * Math.Sin(s * .8 - .5);
        }
        var ex = Excavator.ExcavatorSample.Columns(t, rnd.NextDouble);
        var names = new[] { "Time_s", "Pressure_bar", "Flow_Lpm", "Temp_C", "Position_mm", "Setpoint_mm", "Motor_A", "Valve_Cmd_pct" }
            .Concat(Excavator.ExcavatorSample.Names).ToArray();
        var cols = new[] { t }.Concat(c).Concat(ex).ToArray();
        return new RawTable(names, cols, new bool[names.Length], new string[names.Length].Select(_ => "").ToArray(), false,
            new Dictionary<string, SortedDictionary<int, string>>());
    }
}
