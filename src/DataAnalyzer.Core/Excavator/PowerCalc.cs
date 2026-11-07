using System.Text.RegularExpressions;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Excavator;

/// <summary>Main Pump 하나의 압력·유량 열</summary>
public sealed class PumpCols
{
    public string P { get; set; } = "";
    public string PUnit { get; set; } = "bar";
    public string Q { get; set; } = "";
    /// <summary>lpm, lps, m3s, ccs</summary>
    public string QUnit { get; set; } = "lpm";
}

/// <summary>실린더 속도 열 (없으면 ②의 길이·각도에서 계산)</summary>
public sealed class CylVelCol
{
    public string V { get; set; } = "";
    /// <summary>mps, mmps, cmps, 또는 길이 열 미분: dm, dmm, dcm</summary>
    public string VUnit { get; set; } = "mmps";
}

/// <summary>선회 모터 열과 사양</summary>
public sealed class SwingCols
{
    public string PA { get; set; } = "";
    public string PB { get; set; } = "";
    public string PUnit { get; set; } = "bar";
    /// <summary>"AB" = pA − pB, "BA" = pB − pA</summary>
    public string Dir { get; set; } = "AB";
    public string Speed { get; set; } = "";
    public string SpeedUnit { get; set; } = "dps";
    public double Ratio { get; set; } = double.NaN;
    /// <summary>선회 속도에 감속비를 곱함 (속도 열이 이미 모터 축이면 false)</summary>
    public bool UseRatio { get; set; } = true;
    /// <summary>모터 용적 [cc/rev]</summary>
    public double Disp { get; set; } = double.NaN;
}

/// <summary>소요 Power 계산 설정 (HTML PW.cfg)</summary>
public sealed class PowerConfig
{
    public PumpCols[] Pump { get; set; } = { new(), new() };
    public CylVelCol[] Cyl { get; set; } = { new(), new(), new() };
    public SwingCols Sw { get; set; } = new();
}

/// <summary>소요 Power (펌프 / 실린더 / 선회모터 / Actuator 합산 · 회생). HTML의 pwGuess / pwCalcOn.</summary>
public static class PowerCalc
{
    /// <summary>결과 열 설명</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Pump1_Power_kW"] = "Pump 1 출력", ["Pump2_Power_kW"] = "Pump 2 출력", ["PumpTotal_Power_kW"] = "Pump 합계",
        ["Boom_Power_kW"] = "붐 실린더", ["Arm_Power_kW"] = "암 실린더", ["Bkt_Power_kW"] = "버켓 실린더", ["Swing_Power_kW"] = "선회 모터",
        ["Actuator_Power_kW"] = "Actuator Power (각 ≥0 합)", ["Regen_Power_kW"] = "Regeneratable Power",
        ["Boom_Vel_mps"] = "붐 속도(미분, 중간값)", ["Arm_Vel_mps"] = "암 속도(미분, 중간값)", ["Bkt_Vel_mps"] = "버켓 속도(미분, 중간값)",
        ["Swing_dP_bar"] = "선회 ΔP (중간값)", ["Swing_MotorSpd_rpm"] = "선회모터 속도 (중간값)", ["Swing_Flow_lpm"] = "선회모터 유량 (중간값)",
    };
    /// <summary>중간값 열 (CSV 저장 기본 선택에서 뺌)</summary>
    public static readonly Regex Intermediate = new(@"_Vel_mps$|^Swing_(dP|MotorSpd|Flow)_");
    static readonly Dictionary<string, double> QF = new() { ["lpm"] = 1, ["lps"] = 60, ["m3s"] = 6e4, ["ccs"] = .06 };

    /// <summary>유량 단위 → lpm/lps/m3s/ccs (모르면 "")</summary>
    public static string FlowUnitOf(string? u)
    {
        var s = Regex.Replace((u ?? "").ToLowerInvariant(), @"[\s\[\]()]", "");
        return Regex.IsMatch(s, "^(lpm|l/min|lmin|ℓ/min)$") ? "lpm" : Regex.IsMatch(s, "^(l/s|lps|l/sec)$") ? "lps"
            : Regex.IsMatch(s, "^(m3/s|m³/s)$") ? "m3s" : Regex.IsMatch(s, "^(cc/s|cc/sec|cm3/s|cm³/s|ccs)$") ? "ccs" : "";
    }

    /// <summary>헤더로 열 자동 검색</summary>
    public static PowerConfig Guess(DataSet d, ExcavatorSignals s, KinParams? k)
    {
        string U(string n) => d.Units.GetValueOrDefault(n) ?? "";
        string Tj(string n) => " " + string.Join(" ", Units.Tokens(n)) + " ";
        bool IsQ(string n) => FlowUnitOf(U(n)) != "" || Regex.IsMatch(Tj(n), @"\s(q|flow|flw|lpm|유량)\s");
        bool IsP(string n) => !IsQ(n) && (Units.PressureToPa(U(n)) != null || Regex.IsMatch(Tj(n), @"\s(p|pres|press|pressure|압력)\s"));
        List<string> PumpOf(int i) => d.Names.Where(n =>
        {
            var t = Units.Tokens(n);
            return t.Any(x => Regex.IsMatch(x, @"^(pump|pmp|mp)\d*$")) && (t.Contains(i.ToString()) || t.Any(x => x == "pump" + i || x == "pmp" + i || x == "mp" + i));
        }).ToList();
        var cfg = new PowerConfig();
        for (int i = 0; i < 2; i++)
        {
            var c = PumpOf(i + 1); string p = c.FirstOrDefault(IsP) ?? "", q = c.FirstOrDefault(IsQ) ?? "", pu = Units.Normalize(U(p));
            var fu = FlowUnitOf(U(q));
            cfg.Pump[i] = new PumpCols { P = p, PUnit = Units.PressureToPa(pu) != null ? pu : "bar", Q = q, QUnit = fu != "" ? fu : "lpm" };
        }
        for (int j = 0; j < 3; j++)
        {
            var v = d.Names.Where(n => ExcavatorSignals.HasLink(n, j))
                .FirstOrDefault(n => Regex.IsMatch(n, "vel|spd|speed|rate|속도", RegexOptions.IgnoreCase) && !Regex.IsMatch(n, "ang|deg|rad", RegexOptions.IgnoreCase)) ?? "";
            var vu = ExcavatorSignals.VelUnitOf(U(v));
            if (v != "" && vu == "") vu = ExcavatorSignals.MedAbs(d.Signals[v]) > .8 ? "mmps" : "mps";
            cfg.Cyl[j] = new CylVelCol { V = v, VUnit = vu != "" ? vu : "mmps" };
        }
        string pa = s.P("pSwA"), pun = Units.Normalize(U(pa));
        cfg.Sw = new SwingCols
        {
            PA = pa, PB = s.P("pSwB"), PUnit = Units.PressureToPa(pun) != null ? pun : "bar", Dir = "AB",
            Speed = s.SwingSpeed, SpeedUnit = s.SwingSpeedUnit != "" ? s.SwingSpeedUnit : "dps",
            Ratio = k?["SwRatio"] ?? double.NaN, UseRatio = true, Disp = k?["SwDisp"] ?? double.NaN,
        };
        return cfg;
    }

    /// <summary>
    /// 계산. ex가 null이면 실린더 Power는 건너뛴다 (파라미터 없음).
    /// </summary>
    public static CalcResult Calc(DataSet d, PowerConfig c, ExcavatorAnalysis? ex, double[] ts, int winN)
    {
        var res = new CalcResult(); int n = d.Count; var warn = new List<string>(); var srcs = new List<string>();
        double[]? Col(string k) => k != "" && d.Signals.TryGetValue(k, out var a) ? a : null;
        double[] Mk() { var a = new double[n]; Array.Fill(a, double.NaN); return a; }
        double[]? Put(string k, double[] a, string u) => res.Put(k, a, u) ? a : null;
        double Pf(string u) => (Units.PressureToPa(u) ?? 1e5) / 1e5;

        var pumps = new List<double[]>();
        for (int i = 0; i < 2; i++)
        {
            var p = c.Pump[i]; double[]? pc = Col(p.P), qc = Col(p.Q);
            if (pc == null || qc == null) continue;
            double f = Pf(p.PUnit) * QF.GetValueOrDefault(p.QUnit, 1) / 600; var a = new double[n];
            for (int k = 0; k < n; k++) a[k] = pc[k] * qc[k] * f;
            if (Put("Pump" + (i + 1) + "_Power_kW", a, "kW") is { } x) pumps.Add(x);
        }
        if (pumps.Count > 0)
        {
            var a = Mk();
            for (int k = 0; k < n; k++) { double t = 0; bool m = false; foreach (var x in pumps) if (!double.IsNaN(x[k])) { t += x[k]; m = true; } if (m) a[k] = t; }
            Put("PumpTotal_Power_kW", a, "kW");
        }
        var act = new Dictionary<string, double[]>();
        string[] NMc = ExcavatorSignals.LinkEng, NMk = ExcavatorSignals.LinkKor;
        for (int j = 0; j < 3; j++)
        {
            var q = c.Cyl[j]; var T = ex?.CylThrustKN(j);
            if (T == null) { warn.Add(NMk[j] + " 실린더: ③에서 선택한 압력(Head·Rod) 열과 ①의 Tube/Rod 직경이 필요"); continue; }
            double[] vm; string src;
            if (Col(q.V) is { } vc)
            {
                double f = ExcavatorSignals.VelToMps(q.VUnit);
                if (!double.IsNaN(f)) vm = vc.Select(x => x * f).ToArray();
                else { double ff = ExcavatorSignals.LenToM(q.VUnit.Length > 1 ? q.VUnit[1..] : ""); vm = Dynamics.Ddt(Dynamics.SmoothMA(vc.Select(x => x * ff).ToArray(), winN), ts); }
                src = NMk[j] + ": 선택한 속도 열 \"" + q.V + "\"";
            }
            else
            {
                var r = ex!.CylVelocity(j);
                if (r == null) { warn.Add(NMk[j] + " 실린더: 속도 열이 없고 ②의 길이·각도로도 속도를 계산할 수 없음"); continue; }
                vm = r.Value.V; src = NMk[j] + ": " + r.Value.Src;
            }
            srcs.Add(src);
            Put(NMc[j] + "_Thrust_kN", T, "kN"); Put(NMc[j] + "_Vel_mps", vm, "m/s");
            var a = new double[n]; for (int k = 0; k < n; k++) a[k] = T[k] * vm[k];
            if (Put(NMc[j] + "_Power_kW", a, "kW") is { } x) act[NMc[j]] = x;
        }
        var w = c.Sw; double[]? pA = Col(w.PA), pB = Col(w.PB), sc = Col(w.Speed);
        if (pA != null || pB != null || sc != null)
        {
            if (pA == null || pB == null || sc == null) warn.Add("선회: pSwA, pSwB, 선회 속도 열이 모두 필요");
            else
            {
                double disp = w.Disp, ratio = w.UseRatio ? w.Ratio : 1;
                if (!(disp > 0) || !(ratio > 0)) warn.Add("선회: 용적(cc/rev)과 감속비(>0)를 입력하세요");
                else
                {
                    double fp = Pf(w.PUnit) * (w.Dir == "BA" ? -1 : 1), fs = w.SpeedUnit == "rpm" ? 1 : w.SpeedUnit == "rps" ? 60 / (2 * Math.PI) : 60.0 / 360;
                    double[] dp = new double[n], rp = new double[n], qf = new double[n], a = new double[n];
                    for (int k = 0; k < n; k++) { dp[k] = (pA[k] - pB[k]) * fp; rp[k] = sc[k] * fs * ratio; qf[k] = rp[k] * disp / 1000; a[k] = dp[k] * qf[k] / 600; }
                    Put("Swing_dP_bar", dp, "bar"); Put("Swing_MotorSpd_rpm", rp, "rpm"); Put("Swing_Flow_lpm", qf, "lpm");
                    if (Put("Swing_Power_kW", a, "kW") is { } x) act["Swing"] = x;
                }
            }
        }
        var keys = new[] { "Boom", "Arm", "Bkt", "Swing" }.Where(act.ContainsKey).ToList();
        if (keys.Count > 0)
        {
            double[] A = Mk(), Rg = Mk();
            for (int k = 0; k < n; k++)
            {
                double sa = 0, sr = 0; bool m = false;
                foreach (var q in keys) { double v = act[q][k]; if (!double.IsNaN(v)) { m = true; if (v > 0) sa += v; else sr -= v; } }
                if (m) { A[k] = sa; Rg[k] = sr; }
            }
            Put("Actuator_Power_kW", A, "kW"); Put("Regen_Power_kW", Rg, "kW");
        }
        if (!res.Ok)
        {
            res.Message = "계산된 항목이 없습니다. 열을 지정하세요." + (warn.Count > 0 ? "\n⚠ " + string.Join(" / ", warn) : "");
            res.Warn = true; return res;
        }
        string[][] groups =
        {
            new[] { "Pump1_Power_kW", "Pump2_Power_kW", "PumpTotal_Power_kW" },
            new[] { "Boom_Power_kW", "Arm_Power_kW", "Bkt_Power_kW", "Swing_Power_kW" },
            new[] { "PumpTotal_Power_kW", "Actuator_Power_kW", "Regen_Power_kW" },
        };
        foreach (var g in groups) { var f = g.Where(res.Has).ToArray(); if (f.Length > 0) res.Charts.Add(f); }
        var mm = res.Outputs.Select(o => o.Name).Where(k => k.EndsWith("_Power_kW"));
        res.Message = "✓ 계산 완료 → 차트에 추가: " + string.Join(", ", mm)
            + (srcs.Count > 0 ? "\n추력 = ③ 압력 열로 계산 · 속도 소스: " + string.Join(" / ", srcs) : "")
            + (keys.Count > 0 ? "\nActuator 합산 대상: " + string.Join(", ", keys) : "\n(Actuator 합산 대상 없음)")
            + (warn.Count > 0 ? "\n⚠ " + string.Join(" / ", warn) : "");
        res.Warn = warn.Count > 0;
        return res;
    }
}
