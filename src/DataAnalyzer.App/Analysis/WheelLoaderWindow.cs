using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;
using DataAnalyzer.Core.WheelLoader;
using Microsoft.Win32;

namespace DataAnalyzer.App.Analysis;

/// <summary>
/// 휠로더 Z-bar 기구·동력 분석 창 (HTML wdlg). a. 파라미터 · b. 신호열 · c. 2D 형상 · d-1 각도 · d-2 토크 ·
/// e. Dynamics Parameter Estimation (Labeling, 관측 Vector, Bucket, Sine/마찰 Fitting, Arm) · f. Weighing.
/// </summary>
public sealed class WheelLoaderWindow : Window
{
    readonly AppState S;
    readonly Action<CalcResult> _show;
    KinParams K = WlGeom.Demo();
    WlSignals Sig = new();
    DataSet? _for;
    readonly WlEstState St = new();
    WlBrEst? _br;
    readonly Dictionary<string, (string T, bool Err)> _msgs = new();
    readonly Dictionary<string, Expander> _sec = new();
    readonly StackPanel _root = new() { Margin = new Thickness(8) };

    // c. 2D
    readonly WheelLoaderView _view = new() { Height = 360 };
    readonly TextBlock _info = new() { FontFamily = new FontFamily("Consolas, Malgun Gothic"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0) };
    readonly Slider _sA = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center }, _sB = new() { Width = 220, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _rA = Ui.Text(""), _rB = Ui.Text("");
    bool _cursorPose = true, _sliding;
    double _pinScale; // 0 = 자동
    double[]? _pvL;

    public WheelLoaderWindow(AppState state, Action<CalcResult> show)
    {
        S = state; _show = show;
        Title = "휠로더 Z-bar 기구·동력 분석"; Width = 1100; Height = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        foreach (var (id, t, open) in new[] { ("a", "a. Kinetics · Dynamics Parameter 입력", true), ("b", "b. 신호열 선택", true), ("c", "c. 2D 형상 · Animation", true),
            ("d1", "d-1. 각도 계산 (Bucket θ4)", false), ("d2", "d-2. 토크 계산", false), ("e", "e. Dynamics Parameter Estimation", false), ("f", "f. Weighing", false) })
        {
            var x = Ui.Section(t, new StackPanel(), open); _sec[id] = x; _root.Children.Add(x);
        }
        _root.Children.Add(Ui.Hint("센서 오차 주입(검증용)은 아직 옮기지 않았습니다."));
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _sA.ValueChanged += (_, _) => { if (_sliding) return; _pvL ??= new double[2]; _pvL[0] = _sA.Value; _cursorPose = false; Preview(); };
        _sB.ValueChanged += (_, _) => { if (_sliding) return; _pvL ??= new double[2]; _pvL[1] = _sB.Value; _cursorPose = false; Preview(); };
        try { if (File.Exists(LastFile)) K = KinParams.FromJson(File.ReadAllText(LastFile)); } catch { }
        S.Changed += OnState; S.DataChanged += OnData;
        Closed += (_, _) => { SaveLast(); S.Changed -= OnState; S.DataChanged -= OnData; };
        OnData();
    }

    static string LastFile => Ui.AppDataFile("wheelloader_last.json");
    void SaveLast() { try { File.WriteAllText(LastFile, K.ToJson()); } catch { } }

    void OnState() { if (_cursorPose) Preview(); }

    void OnData()
    {
        var d = S.Data;
        if (d != _for) { _for = d; if (d != null) Sig = WlSignals.Guess(d, Sig); St.Bkt = null; St.Arm = null; _br = null; _cursorPose = true; }
        Rebuild();
    }

    void Rebuild() { BuildA(); BuildB(); BuildC(); BuildD(); BuildE(); BuildF(); Preview(); }

    StackPanel Panel(string id) { var p = new StackPanel(); ((Border)_sec[id].Content).Child = p; return p; }

    TextBlock MsgBox(string id)
    {
        var t = Ui.Msg();
        if (_msgs.TryGetValue(id, out var m)) Ui.SetMsg(t, m.T, m.Err);
        return t;
    }
    void Msg(string id, string t, bool err, TextBlock? box = null) { _msgs[id] = (t, err); if (box != null) Ui.SetMsg(box, t, err); }

    TextBox N(string key, double w = 80) => Ui.Num(K[key], v => { K[key] = v; if (IsGeomKey(key)) { _view.Fit(); Preview(); } }, w);
    ComboBox SelK(string key, string def, params (string V, string T)[] opts) => Ui.Combo(opts, K.Str(key, def), v => { K.Text[key] = v; Preview(); });
    static bool IsGeomKey(string k) => !k.StartsWith('u') && !k.StartsWith('T') && !k.StartsWith("Bk") && !k.StartsWith("Ar");

    void Show(CalcResult r, string id, TextBlock box)
    {
        if (r.Outputs.Count > 0) _show(r);
        Msg(id, r.Message != "" ? r.Message : r.Outputs.Count > 0 ? "✓ 완료" : "계산된 항목이 없습니다", r.Warn || r.Outputs.Count == 0, box);
    }

    // ================= a. 파라미터 =================

    static readonly (string Key, string Label)[] LinkDims = { ("L3", "Lift Arm 길이 L3"), ("L4", "Bucket 길이 L4"), ("Lgl", "Lgl: PP–PGL 길이"), ("Lql", "Lql: PQ–PGL 길이"), ("psiTL", "psiTL [deg]: ∠PP–PGL–PQ"), ("Lpl", "Lpl: Push Link 길이") };
    static readonly (string Key, string Label)[] Pivots = { ("P30", "P30 〈Body〉 Arm Cyl Head"), ("P31", "P31 〈Lift Arm 좌표〉 Arm Cyl Rod"), ("P40", "P40 〈Body〉 Bucket Cyl Head"), ("PGL", "PGL 〈Lift Arm 좌표〉 Tilt Lever 회전중심"), ("P41", "P41 〈Bucket 좌표〉 Push Link 연결") };
    static readonly (string Label, string M, string? C, string Desc, string? C2)[] Mass =
    {
        ("Lift Arm 실린더 Tube (1개)", "AmTubeM", "AmTubeC", "P30 → Rod 방향 COM 거리", null), ("Lift Arm 실린더 Rod (1개)", "AmRodM", "AmRodC", "P31 → Tube 방향 COM 거리", null),
        ("Bucket 실린더 Tube", "BkTubeM", "BkTubeC", "P40 → Rod 방향 COM 거리", null), ("Bucket 실린더 Rod", "BkRodM", "BkRodC", "PP → Tube 방향 COM 거리", null),
        ("Tilt Lever", "TLM", "TLcx", "PGL 기준 COM x (PGL→PP 방향)", "TLcy"), ("Push Link", "PLM", "PLc", "PQ → P41 방향 COM 거리", null),
        ("Lift Arm", "LAM", "LAcx", "Lift Arm 좌표 COM x", "LAcy"), ("빈 Bucket", "BKM", "BKcx", "Bucket 좌표 COM x", "BKcy"),
        ("Joint Pin: Bucket 실린더–Tilt Lever (PP)", "PinPPM", null, "점질량, PP 위치", null), ("Joint Pin: Tilt Lever–Push Link (PQ)", "PinPQM", null, "점질량, PQ 위치", null),
        ("Joint Pin: Push Link–Bucket (P41)", "PinP41M", null, "점질량, P41 위치", null),
    };
    static readonly (string V, string T)[] Sg = { ("+", "+"), ("-", "−") };

    static SortedDictionary<string, string> LoadPresets() => AppStore.List("wheelloader");

    void SetK(KinParams k) { K = k; _pvL = null; _view.Fit(); SaveLast(); Rebuild(); }

    void BuildA()
    {
        var p = Panel("a"); var msg = MsgBox("a");
        var ps = new ComboBox { MinWidth = 150 }; ps.Items.Add(new ComboBoxItem { Content = "(저장된 장비 선택)", Tag = "" });
        foreach (var n in LoadPresets().Keys) ps.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        ps.SelectedIndex = 0;
        string Sel() => (string)((ComboBoxItem)ps.SelectedItem).Tag;
        p.Children.Add(Ui.Row(ps,
            Ui.Btn("불러오기", () => { if (LoadPresets().TryGetValue(Sel(), out var js)) SetK(KinParams.FromJson(js)); }),
            Ui.Btn("저장", () =>
            {
                var nm = K.Name.Trim(); if (nm == "") { Msg("a", "장비 이름을 입력하세요", true, msg); return; }
                AppStore.Put("wheelloader", nm, K.ToJson());
                Msg("a", "저장됨: " + nm + " (" + AppStore.Where + ")", false); BuildA();
            }, tip: AppStore.SaveTip),
            Ui.Btn("삭제", () => { var n = Sel(); if (n == "") return; AppStore.Remove("wheelloader", n); BuildA(); }),
            Ui.Btn("JSON 내보내기", () =>
            {
                var dlg = new SaveFileDialog { FileName = "wheelloader_" + string.Concat((K.Name == "" ? "params" : K.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json", Filter = "JSON (*.json)|*.json" };
                if (dlg.ShowDialog(this) == true) File.WriteAllText(dlg.FileName, K.ToJson());
            }),
            Ui.Btn("JSON 가져오기", () =>
            {
                var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
                if (dlg.ShowDialog(this) != true) return;
                try { SetK(KinParams.FromJson(File.ReadAllText(dlg.FileName))); Msg("a", "JSON을 가져왔습니다", false); BuildA(); }
                catch (Exception ex) { Msg("a", "JSON을 읽을 수 없습니다: " + ex.Message, true, msg); }
            }),
            Ui.Btn("예제값 채우기", () => SetK(WlGeom.Demo()))));
        p.Children.Add(Ui.Hint("좌표계: Lift Arm 회전 중심을 원점, 전방 +x, 위 +y. 각도는 반시계 +. P31·PGL은 Lift Arm 좌표, P41은 Bucket 좌표(Bucket pivot이 원점, Tip 방향이 +x), P30·P40은 Body 좌표. θ3 = Lift Arm 수평 기준 각도, θ4 = Bucket의 Lift Arm 기준 상대각, α = Tilt Lever(PGL→PP)의 Lift Arm 기준 상대각."));
        var name = new TextBox { Width = 200, Text = K.Name }; name.LostFocus += (_, _) => K.Name = name.Text.Trim();
        p.Children.Add(Ui.Row(Ui.Labeled("장비 이름", name), Ui.Labeled("길이 단위", Ui.Combo(new[] { ("m", "m"), ("mm", "mm"), ("cm", "cm") }, K.Str("unit", "m"), v => { K.Text["unit"] = v; _pvL = null; _view.Fit(); Preview(); }))));
        p.Children.Add(Ui.Bold("링크 치수"));
        p.Children.Add(Ui.Row(LinkDims.Select(x => (UIElement)Ui.Labeled(x.Label, N(x.Key, 70))).ToArray()));
        p.Children.Add(Ui.Bold("Pivot 좌표  (원점 = Lift Arm 회전 중심, x = 전방, y = 위)"));
        p.Children.Add(Ui.Table(new[] { "점", "x", "y" }, Pivots.Select(x => new UIElement[] { Ui.Text(x.Label), N(x.Key + "x", 70), N(x.Key + "y", 70) })));
        p.Children.Add(Ui.Bold("실린더  (Lift Arm 실린더는 2개 · Bucket 실린더는 1개가 기본)"));
        p.Children.Add(Ui.Table(new[] { "", "Lmin", "Lmax", "Tube 직경", "Rod 직경", "개수" }, new[] { ("Lift Arm 실린더", "Am"), ("Bucket 실린더", "Bk") }
            .Select(x => new UIElement[] { Ui.Text(x.Item1), N(x.Item2 + "Lmin", 70), N(x.Item2 + "Lmax", 70), N(x.Item2 + "Dt", 70), N(x.Item2 + "Dr", 70), N(x.Item2 + "N", 45) })));
        p.Children.Add(Ui.Bold("부품 무게 · 무게중심  (질량 kg, COM은 길이 단위. 비워 두면 해당 부품은 계산에서 제외)"));
        p.Children.Add(Ui.Table(new[] { "부품", "질량 [kg]", "COM 거리 / x", "COM y", "" }, Mass.Select(m => new UIElement[]
        {
            Ui.Text(m.Label), N(m.M, 70), m.C != null ? N(m.C, 70) : Ui.Text(""), m.C2 != null ? N(m.C2, 70) : Ui.Text(""), Ui.Hint(m.Desc),
        })));
        p.Children.Add(Ui.Bold("Bucket · Lift Arm 관성 모멘트  (무게중심 기준 I_zz, kg·m²)"));
        p.Children.Add(Ui.Row(Ui.Labeled("I4zz (Bucket)", N("I4zz")), Ui.Labeled("I3zz (Lift Arm)", N("I3zz"))));
        p.Children.Add(Ui.Bold("조립 형상 분기 (같은 실린더 길이에 대해 해가 둘일 때 선택)"));
        p.Children.Add(Ui.Row(Ui.Labeled("Lift Arm 실린더", SelK("sA", "+", Sg)), Ui.Labeled("Bucket 실린더→Tilt Lever", SelK("sP", "+", Sg)),
            Ui.Labeled("Tilt Lever→Push Link", SelK("sJ", "+", Sg)), Ui.Labeled("Push Link→Bucket", SelK("sK", "+", Sg)),
            Ui.Btn("데이터로 분기 자동 선택", () => { var (t, e) = AutoBranch(); Msg("a", t, e); BuildA(); Preview(); })));
        p.Children.Add(msg);
    }

    /// <summary>분기 자동 선택 (HTML wlAutoBranch)</summary>
    (string, bool) AutoBranch()
    {
        var g0 = WlGeom.Build(K); if (g0.Error != null) return (g0.Error, true);
        var d = S.Data; const double R = Kinematics.Rad;
        if (d != null && d.Signals.TryGetValue(Sig.AA, out var A) && d.Signals.TryGetValue(Sig.BA, out var Bv))
        {
            int st = Math.Max(1, A.Length / 300); var an = new List<(double, double)>();
            for (int i = 0; i < A.Length; i += st)
            {
                double x = A[i] * (Sig.AAu == "rad" ? 1 : R), y = (Bv[i] + Sig.BOff) * (Sig.BAu == "rad" ? 1 : R);
                if (double.IsFinite(x) && double.IsFinite(y)) an.Add((x, y));
            }
            if (an.Count > 0)
            {
                (int ok, string sK, int sj)? best = null; var sc = new Dictionary<string, int>();
                foreach (var sK in new[] { "+", "-" })
                {
                    var kk = K.Clone(); kk.Text["sK"] = sK; var g = WlGeom.Build(kk); int ok = 0, sj = 0;
                    foreach (var (x, y) in an) { var p = g.PoseTL(x, y); if (!p.Bad) { ok++; if (sj == 0) sj = WlGeom.SJOf(p); } }
                    sc[sK] = ok;
                    if (best == null || ok > best.Value.ok || (ok == best.Value.ok && sK == K.Str("sK"))) best = (ok, sK, sj);
                }
                if (best!.Value.ok == 0) return ("어떤 분기로도 CSV 각도의 자세를 조립할 수 없습니다. 각도 단위·Pivot 좌표를 확인하세요.", true);
                K.Text["sK"] = best.Value.sK; if (best.Value.sj != 0) K.Text["sJ"] = best.Value.sj > 0 ? "+" : "-";
                return ($"분기 자동 선택: Push Link→Bucket {best.Value.sK}, Tilt Lever→Push Link {K.Str("sJ")} (조립 가능 + : {sc["+"]}, − : {sc["-"]} / {an.Count})"
                    + (sc["+"] == sc["-"] ? "\n⚠ 두 분기가 모두 가능해 구분할 수 없습니다 — 2D 형상으로 실제 Bucket 모양과 맞는지 확인하세요." : ""), false);
            }
        }
        double u = g0.U, a = (K["AmLmin"] + K["AmLmax"]) / 2 * u, b = (K["BkLmin"] + K["BkLmax"]) / 2 * u;
        if (!double.IsFinite(a) || !double.IsFinite(b)) return ("자동 선택에는 CSV의 각도 열(또는 Lmin/Lmax)이 필요합니다", true);
        (int ok, string[] s)? bb = null;
        foreach (var sA in new[] { "+", "-" }) foreach (var sP in new[] { "+", "-" }) foreach (var sJ in new[] { "+", "-" }) foreach (var sK in new[] { "+", "-" })
        {
            var kk = K.Clone(); kk.Text["sA"] = sA; kk.Text["sP"] = sP; kk.Text["sJ"] = sJ; kk.Text["sK"] = sK; var g = WlGeom.Build(kk); int ok = 0;
            double a2 = g.ArmAng(a);
            if (double.IsFinite(a2)) { double b2 = g.BktAng(b, a2); if (double.IsFinite(b2)) { var p = g.Pose(a2, b2); if (!p.Bad && Math.Abs(p.Lb - b) < 1e-6 * Math.Max(1, b) && Math.Abs(p.La - a) < 1e-6 * Math.Max(1, a)) ok++; } }
            if (bb == null || ok > bb.Value.ok) bb = (ok, new[] { sA, sP, sJ, sK });
        }
        if (bb == null || bb.Value.ok == 0) return ("어떤 분기 조합으로도 Lmin~Lmax 중간 길이의 자세를 만들 수 없습니다. 파라미터를 확인하세요.", true);
        string[] ks = { "sA", "sP", "sJ", "sK" }; for (int i = 0; i < 4; i++) K.Text[ks[i]] = bb.Value.s[i];
        return ($"분기 자동 선택(Lmin~Lmax 중간 길이 기준): Lift Arm {bb.Value.s[0]}, Bucket 실린더 {bb.Value.s[1]}, Tilt Lever→Push Link {bb.Value.s[2]}, Push Link→Bucket {bb.Value.s[3]}. 2D 형상으로 실제 형상과 맞는지 확인하세요.", false);
    }

    // ================= b. 신호열 =================

    static readonly (string V, string T)[] AU = { ("deg", "deg"), ("rad", "rad") };
    static readonly (string V, string T)[] PU = { ("bar", "bar"), ("mpa", "MPa"), ("kpa", "kPa"), ("psi", "psi"), ("pa", "Pa") };

    void BuildB()
    {
        var p = Panel("b"); var d = S.Data;
        if (d == null) { p.Children.Add(Ui.Hint("CSV를 먼저 여세요 (파라미터 편집과 2D 형상은 CSV 없이도 가능)")); return; }
        string Au(string v) => Units.Normalize(d.Units.GetValueOrDefault(v));
        bool IsP(string v) => Units.PressureToPa(d.Units.GetValueOrDefault(v)) != null;
        p.Children.Add(Ui.Table(new[] { "", "각도 열", "각도 단위", "실린더", "압력 Head 열", "압력 Rod 열", "압력 단위" }, new[]
        {
            new UIElement[] { Ui.Text("Lift Arm (Body 기준)"), Ui.Columns(d, Sig.AA, v => { Sig.AA = v; if (Au(v) is "deg" or "rad") Sig.AAu = Au(v); BuildB(); Preview(); }, null, 140),
                Ui.Combo(AU, Sig.AAu, v => { Sig.AAu = v; Preview(); }), Ui.Text("Arm"),
                Ui.Columns(d, Sig.AH, v => { Sig.AH = v; if (IsP(v)) { Sig.APu = Au(v); BuildB(); } }, null, 130), Ui.Columns(d, Sig.AR, v => Sig.AR = v, null, 130), Ui.Combo(PU, Sig.APu, v => Sig.APu = v) },
            new UIElement[] { Ui.Text("Tilt Lever (Lift Arm 기준 상대각)"), Ui.Columns(d, Sig.BA, v => { Sig.BA = v; if (Au(v) is "deg" or "rad") Sig.BAu = Au(v); BuildB(); Preview(); }, null, 140),
                Ui.Combo(AU, Sig.BAu, v => { Sig.BAu = v; Preview(); }), Ui.Text("Bucket"),
                Ui.Columns(d, Sig.BH, v => { Sig.BH = v; if (IsP(v)) { Sig.BPu = Au(v); BuildB(); } }, null, 130), Ui.Columns(d, Sig.BR, v => Sig.BR = v, null, 130), Ui.Combo(PU, Sig.BPu, v => Sig.BPu = v) },
        }));
        p.Children.Add(Ui.Row(Ui.Labeled("Tilt Lever 각도 Offset", Ui.Num(Sig.BOff, v => { Sig.BOff = double.IsFinite(v) ? v : 0; Preview(); }, 70)), Ui.Text($"[{(Sig.BAu == "rad" ? "rad" : "deg")}]  (열 값에 더함)"),
            Ui.Labeled("각도 출력·저장 단위", Ui.Combo(new[] { ("deg", "degree (°)"), ("rad", "radian (rad)"), ("both", "둘 다") }, Sig.AngleOut, v => Sig.AngleOut = v))));
        var opt = Ui.Row(Ui.Labeled("평활 창(s)", Ui.Num(Sig.SmoothWindow, v => Sig.SmoothWindow = double.IsFinite(v) ? Math.Max(0, v) : .2, 60)));
        if (!d.IsDate) opt.Children.Add(Ui.Labeled("시간 단위", Ui.Combo(new[] { ("s", "s"), ("ms", "ms") }, Sig.TimeUnit, v => Sig.TimeUnit = v)));
        p.Children.Add(opt);
        p.Children.Add(Ui.Hint("추력 = (pH·A헤드 − pR·A로드) × 개수 — [실린더]의 Tube/Rod 직경과 압력 열로 계산합니다. Bucket 각도 θ4는 Tilt Lever 각도 α로부터 Push Link 연결 기구학으로 계산하고, 실린더 길이·속도는 각도에서 기구학으로 계산합니다."));
    }

    // ================= c. 2D 형상 =================

    void BuildC()
    {
        var p = Panel("c");
        p.Children.Add(Ui.Hint("실린더 길이 슬라이더(범위 Lmin ~ Lmax)로 형상을 확인하거나, [CSV 커서 시각 자세]를 누르면 차트 커서 시각의 각도로 그립니다 (커서를 움직이면 따라감). 휠 = 확대, 드래그 = 이동, 더블클릭 = 맞춤."));
        var fsc = new TextBox { Width = 60, Text = _pinScale > 0 ? Ui.F(_pinScale) : "", ToolTip = "핀 반력 화살표 길이 스케일 [px/kN]. 비우면 자동(데이터 최대 반력 = 70px)" };
        fsc.LostFocus += (_, _) => { var v = Ui.P(fsc.Text); _pinScale = v > 0 ? v : 0; Preview(); };
        p.Children.Add(Ui.Row(Ui.Labeled("Arm 실린더", _sA), _rA, Ui.Labeled("Bucket 실린더", _sB), _rB));
        p.Children.Add(Ui.Row(Ui.Btn("CSV 커서 시각 자세", () => { _cursorPose = true; Preview(); }, true),
            Ui.Labeled("핀 반력 화살표 스케일 [px/kN]", fsc),
            Ui.Btn("×2", () => { _pinScale = Math.Max(1e-3, _view.PinScale * 2); fsc.Text = Ui.F(_pinScale); Preview(); }),
            Ui.Btn("×½", () => { _pinScale = Math.Max(1e-3, _view.PinScale * .5); fsc.Text = Ui.F(_pinScale); Preview(); }),
            Ui.Btn("자동", () => { _pinScale = 0; fsc.Text = ""; Preview(); })));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        if (_view.Parent is Border old) old.Child = null;
        var vb = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE8)), BorderThickness = new Thickness(1), Child = _view };
        if (_info.Parent is Panel op) op.Children.Remove(_info);
        var ib = new StackPanel(); ib.Children.Add(_info); Grid.SetColumn(ib, 1);
        g.Children.Add(vb); g.Children.Add(ib);
        p.Children.Add(g);
    }

    /// <summary>시각 t의 (θ3, α 또는 θ4, θ4 열 사용 여부) (HTML wlAngAt)</summary>
    (double T2, double B, bool Ov)? AngAt(DataSet d, double t)
    {
        if (!d.Signals.TryGetValue(Sig.AA, out var a)) return null;
        double t2 = Series.ValAt(d.Time, a, t) * (Sig.AAu == "rad" ? 1 : Kinematics.Rad);
        if (Sig.T4Mode == "col" && d.Signals.TryGetValue(Sig.T4Src, out var c)) return (t2, Series.ValAt(d.Time, c, t) * (Sig.T4u == "rad" ? 1 : Kinematics.Rad), true);
        if (!d.Signals.TryGetValue(Sig.BA, out var b)) return null;
        return (t2, (Series.ValAt(d.Time, b, t) + Sig.BOff) * (Sig.BAu == "rad" ? 1 : Kinematics.Rad), false);
    }

    (double[] A, double[] B)? Ranges(WlGeom g)
    {
        double u = g.U;
        double[] a = { K["AmLmin"], K["AmLmax"] }, b = { K["BkLmin"], K["BkLmax"] };
        if (a.All(double.IsFinite) && b.All(double.IsFinite) && a[1] > a[0] && b[1] > b[0]) return (a, b);
        double alo = 1e9, ahi = -1e9, blo = 1e9, bhi = -1e9;
        for (int t2 = -40; t2 <= 60; t2 += 5) for (int t3 = -150; t3 <= 150; t3 += 5)
        {
            var p = g.Pose(t2 * Kinematics.Rad, t3 * Kinematics.Rad); if (p.Bad) continue;
            alo = Math.Min(alo, p.La / u); ahi = Math.Max(ahi, p.La / u); blo = Math.Min(blo, p.Lb / u); bhi = Math.Max(bhi, p.Lb / u);
        }
        return ahi > alo && bhi > blo ? (new[] { alo, ahi }, new[] { blo, bhi }) : null;
    }

    void Preview()
    {
        if (!IsInitialized) return;
        var g = WlGeom.Build(K); const double R = Kinematics.Rad; string f3(double x) => x.ToString("F3", CultureInfo.InvariantCulture);
        _view.G = null; _view.Pose = null; _view.Pins = null;
        if (g.Error != null) { _view.Message = g.Error; _info.Text = ""; _view.InvalidateVisual(); return; }
        var rg = Ranges(g);
        if (rg == null) { _view.Message = "형상을 조립할 수 없습니다 (링크 길이·Pivot 좌표 확인)"; _info.Text = ""; _view.InvalidateVisual(); return; }
        double u = g.U; string note = ""; WlPose? cpose = null; var d = S.Data; double? tm = S.SelectedCursor;
        if (_cursorPose && d != null && tm != null && AngAt(d, tm.Value) is { } an)
        {
            var p0 = an.Ov ? g.Pose(an.T2, an.B) : g.PoseTL(an.T2, an.B);
            if (!p0.Bad) { cpose = p0; _pvL = new[] { p0.La / u, p0.Lb / u }; note = $"[CSV 커서 시각 {S.TimeText(tm.Value)}의 각도: θ3 {an.T2 / R:F2}°, {(an.Ov ? "선택 열 θ4" : "Tilt Lever α")} {an.B / R:F2}° → θ4 {p0.T3 / R:F2}°]\n"; }
            else note = "[커서 시각의 각도로 조립할 수 없습니다 — Push Link 분기·파라미터 확인]\n";
        }
        if (_pvL == null || !_pvL.All(double.IsFinite))
        {
            WlPose? q = null;
            foreach (var t2 in new[] { 15, 10, 20, 5, 25, 0, 30, -5, 40 }) { for (int t3 = -10; t3 >= -60 && q == null; t3 -= 5) { var pp = g.Pose(t2 * R, t3 * R); if (!pp.Bad) q = pp; } if (q != null) break; }
            _pvL = q != null ? new[] { q.La / u, q.Lb / u } : new[] { (rg.Value.A[0] + rg.Value.A[1]) / 2, (rg.Value.B[0] + rg.Value.B[1]) / 2 };
        }
        _sliding = true;
        _sA.Minimum = rg.Value.A[0]; _sA.Maximum = rg.Value.A[1]; _sA.Value = Math.Clamp(_pvL[0], _sA.Minimum, _sA.Maximum);
        _sB.Minimum = rg.Value.B[0]; _sB.Maximum = rg.Value.B[1]; _sB.Value = Math.Clamp(_pvL[1], _sB.Minimum, _sB.Maximum);
        _sliding = false;
        string un = K.Str("unit", "m");
        _rA.Text = " " + f3(_pvL[0]) + " " + un; _rB.Text = " " + f3(_pvL[1]) + " " + un;
        double La = _pvL[0] * u, Lb = _pvL[1] * u;
        double t2v = cpose?.T2 ?? g.ArmAng(La), t3v = cpose?.T3 ?? (double.IsFinite(t2v) ? g.BktAng(Lb, t2v) : double.NaN);
        if (!double.IsFinite(t2v)) { _view.Message = "Arm 실린더 길이로 Lift Arm 각도를 구할 수 없습니다"; _info.Text = note + $"Arm 실린더 {f3(_pvL[0])} {un} → 해 없음 (Pivot P30·P31 좌표·분기 확인)"; _view.InvalidateVisual(); return; }
        if (!double.IsFinite(t3v)) { _view.Message = "이 길이 조합은 조립할 수 없습니다"; _info.Text = note + $"Arm 실린더 {f3(_pvL[0])} {un} → θ3 = {t2v / R:F2}°\nBucket 실린더 {f3(_pvL[1])} {un} → 해 없음"; _view.InvalidateVisual(); return; }
        if (cpose != null) g.SJ = WlGeom.SJOf(cpose);
        var p = cpose ?? g.Pose(t2v, t3v);
        _view.G = g; _view.Pose = p; _view.Message = p.Bad ? "이 자세는 링크 길이로 조립할 수 없습니다" : "";
        string pinTxt = "";
        if (cpose != null && d != null && tm != null && d.Signals.TryGetValue("WL_HF4_kN", out var h4))
        {
            double F = Series.ValAt(d.Time, h4, tm.Value) * 1e3;
            var fp = double.IsFinite(F) ? g.PinForces(WlGeom.NoBody(K), p, F) : null;
            if (fp != null)
            {
                double mx = 0;
                foreach (var k in WlPinForces.Keys) if (d.Signals.TryGetValue("WL_Fpin_" + k + "_kN", out var a)) for (int i = 0; i < a.Length; i += Math.Max(1, a.Length / 4000)) if (a[i] > mx) mx = a[i];
                _view.Pins = fp; _view.PinScale = _pinScale > 0 ? _pinScale : 70 / (mx > 0 ? mx : 1);
                pinTxt = "\n[핀 반력 (정적: 유압력 + 자중), kN]\n  " + string.Join(" · ", WlPinForces.Keys.Select(k => k + " " + (fp[k].Length / 1e3).ToString("F2", CultureInfo.InvariantCulture))) + "\n";
            }
        }
        var tx = note + $"Arm 실린더 {f3(_pvL[0])} {un}  →  θ3 = {t2v / R:F2}° ({t2v:F4} rad)\nBucket 실린더 {f3(_pvL[1])} {un}  →  θ4(상대) = {t3v / R:F2}°,  수평 기준 {(t2v + t3v) / R:F2}°\nTilt Lever 상대각 α = {p.Tl / R:F2}°\n";
        var q2 = g.GravTq(K, t2v, t3v);
        if (q2.Count > 0)
        {
            string Kn(double x) => (x >= 0 ? "+" : "") + x.ToString("F3", CultureInfo.InvariantCulture);
            tx += $"\n[부품 무게에 의한 중력 등가 토크 (각도 증가 방향 +), kN·m]\n  Resultant Arm Torque    = {Kn(q2.Values.Sum(v => v.T3) / 1e3)}\n  Resultant Bucket Torque = {Kn(q2.Values.Sum(v => v.T4) / 1e3)}\n";
            foreach (var (k, v) in q2) tx += $"    · {k}:  Arm {Kn(v.T3 / 1e3)} / Bucket {Kn(v.T4 / 1e3)}\n";
        }
        else tx += "\n(부품 질량이 비어 있어 중력 토크는 표시하지 않습니다)\n";
        _info.Text = tx + pinTxt + "색: 파랑 Lift Arm · 주황 Bucket · 초록 Tilt Lever · 보라 Push Link · 회색 실린더";
        _view.InvalidateVisual();
    }

    // ================= d. 각도 · 토크 =================

    UIElement T4Row()
    {
        var d = S.Data; var row = Ui.Row(Ui.Text("Bucket 각도 θ4 (T4)"));
        row.Children.Add(Ui.Combo(new[] { ("calc", "d-1 계산값 (Tilt Lever 각도 → Push Link 기구학)"), ("col", "CSV의 다른 열 선택") }, Sig.T4Mode, v =>
        {
            Sig.T4Mode = v;
            if (v == "col" && Sig.T4Src == "" && d != null)
            {
                var gs = d.Names.FirstOrDefault(n => !n.StartsWith("WL_") && System.Text.RegularExpressions.Regex.IsMatch(n, "bk|bucket|버켓|버킷", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && (Units.Normalize(d.Units.GetValueOrDefault(n)) is "deg" or "rad" || System.Text.RegularExpressions.Regex.IsMatch(n, "ang|deg|rad", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) ?? "";
                Sig.T4Src = gs; var u = Units.Normalize(d.Units.GetValueOrDefault(gs)); if (u is "deg" or "rad") Sig.T4u = u;
            }
            BuildD(); Preview();
        }));
        if (Sig.T4Mode == "col" && d != null)
        {
            row.Children.Add(Ui.Columns(d, Sig.T4Src, v => { Sig.T4Src = v; var u = Units.Normalize(d.Units.GetValueOrDefault(v)); if (u is "deg" or "rad") Sig.T4u = u; BuildD(); Preview(); }, null, 140));
            row.Children.Add(Ui.Combo(AU, Sig.T4u, v => Sig.T4u = v));
            row.Children.Add(Ui.Hint("Arm에 대한 Bucket 상대각 θ4"));
        }
        return row;
    }

    void BuildD()
    {
        var p1 = Panel("d1"); var m1 = MsgBox("d1");
        p1.Children.Add(Ui.Hint("[b. 신호열 선택]의 Lift Arm 각도 열과 Tilt Lever 각도 열(+ Offset)로 Bucket 각도 θ4를 계산하고, 실린더 길이·Tip 위치와 함께 차트에 추가합니다. θ4를 CSV의 다른 열로 대신 쓰면 이후 모든 계산이 선택한 θ4 기준으로 수행됩니다."));
        p1.Children.Add(T4Row());
        p1.Children.Add(Ui.Row(Ui.Btn("각도 계산", () => Run("ang", "d1", m1), true)));
        p1.Children.Add(m1);
        var p2 = Panel("d2"); var m2 = MsgBox("d2"); var d = S.Data;
        p2.Children.Add(Ui.Hint("d-1의 각도(θ3, θ4)와 압력 열로 실린더 추력(HF3, HF4)과 각 링크에 가해지는 등가 토크(Tq3_·Tq4_… 및 합 Tq3_Eq, Tq4_Eq)를 계산해 차트에 추가합니다."));
        if (d != null) p2.Children.Add(Ui.Row(Ui.Text("Lift Arm 각도 θ3 (T3)"), Ui.Columns(d, Sig.AA, v => { Sig.AA = v; var u = Units.Normalize(d.Units.GetValueOrDefault(v)); if (u is "deg" or "rad") Sig.AAu = u; BuildB(); BuildD(); }, null, 140), Ui.Combo(AU, Sig.AAu, v => Sig.AAu = v)));
        p2.Children.Add(T4Row());
        p2.Children.Add(Ui.Row(Ui.Btn("토크 계산", () => Run("tq", "d2", m2), true)));
        p2.Children.Add(m2);
    }

    void Run(string mode, string id, TextBlock box)
    {
        if (S.Data == null) { Msg(id, "CSV를 먼저 여세요", true, box); return; }
        Show(WlAnalysis.AngTorque(S.Data, K, Sig, mode), id, box);
        Preview();
    }

    // ================= e. Parameter Estimation =================

    static readonly (string Label, string Key)[] BktRows =
    {
        ("M4·C4x [kg·m]", "uM4C4x"), ("M4·C4y [kg·m]", "uM4C4y"), ("I4zz + M4·(C4x²+C4y²) [kg·m²]", "uJ4"), ("cF4_Coulomb [N]", "uF4c"),
        ("cF4_PressureCoulomb1 [N/bar] (Head)", "uF4p1"), ("cF4_PressureCoulomb2 [N/bar] (Rod)", "uF4p2"), ("cV4_Viscous [N·s/m]", "uF4v"),
        ("dF4_ForceOffset [N] (기본 해제)", "uF4d"), ("cJ4_JointCoulomb [N·m] (기본 해제)", "uFjc"), ("kJ4_JointForceProp [m] (기본 해제)", "uFjf"),
    };
    static readonly (string Label, string Key)[] ArmRows =
    {
        ("M4·L3 + M3·C3x [kg·m]", "uP"), ("M3·C3y [kg·m]", "uQ"), ("I3zz + M3·(C3x²+C3y²) [kg·m²]", "uJ3"), ("cF3_Coulomb [kN]", "uF3c"), ("cF3_PressureCoulomb [mm]", "uF3p"), ("cV3_Viscous [kN·s/m]", "uF3v"),
    };

    static string Fmt(double v, double s = double.NaN) =>
        double.IsFinite(v) ? double.Parse(v.ToString("G4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            + (double.IsFinite(s) ? " ± " + double.Parse(s.ToString("G2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : "") : "—";

    /// <summary>a.의 입력값으로 계산한 기준값 (HTML wlEstRows)</summary>
    double[] InputVals()
    {
        var g = WlGeom.Build(K); double u = g.Error != null ? 1 : g.U, M4 = K["BKM"], M3 = K["LAM"], cx4 = K["BKcx"] * u, cy4 = K["BKcy"] * u, cx3 = K["LAcx"] * u, cy3 = K["LAcy"] * u, L3 = g.Error != null ? double.NaN : g.L3;
        double n = double.NaN;
        return new[] { M4 * cx4, M4 * cy4, K["I4zz"] + M4 * (cx4 * cx4 + cy4 * cy4), n, n, n, n, n, n, n, M4 * L3 + M3 * cx3, M3 * cy3, K["I3zz"] + M3 * (cx3 * cx3 + cy3 * cy3), n, n, n };
    }

    Grid EstTable(bool bkt)
    {
        var inp = InputVals(); var rows = bkt ? BktRows : ArmRows; int off = bkt ? 0 : 10;
        return Ui.Table(new[] { bkt ? "☑ 회귀 대상 · 파라미터" : "파라미터", "a.에서 입력한 값", "추정값 (±1σ)", "차이 [%]", "사용값" }, rows.Select((r, j) =>
        {
            double est = double.NaN, se = double.NaN;
            if (bkt && St.Bkt != null) { est = St.Bkt.X[j]; se = St.Bkt.Se[j]; }
            if (!bkt && St.Arm != null) { est = St.Arm.X[j]; se = St.Arm.Se[j]; }
            double iv = inp[off + j];
            UIElement lab = bkt ? Ui.Check(r.Label, St.IsOn(r.Key), v => St.RegOn[r.Key] = v) : Ui.Text(r.Label);
            return new UIElement[] { lab, Ui.Text(Fmt(iv)), Ui.Text((bkt ? St.Bkt : (object?)St.Arm) == null ? "—" : Fmt(est, se)),
                Ui.Text(double.IsFinite(est) && double.IsFinite(iv) && Math.Abs(iv) > 1e-12 ? ((est - iv) / Math.Abs(iv) * 100).ToString("F1", CultureInfo.InvariantCulture) + " %" : "—"), N(r.Key, 90) };
        }));
    }

    UIElement Rng(string l1, string k1, string l2, string k2) => Ui.Row(Ui.Labeled(l1, N(k1, 70)), Ui.Labeled(l2, N(k2, 70)));
    UIElement SelRow(string label, string key, string def, (string V, string T)[] opts, Action<string>? cb = null) =>
        Ui.Row(Ui.Labeled(label, Ui.Combo(opts, K.Str(key, def), v => { K.Text[key] = v; cb?.Invoke(v); })));

    UIElement[] SelBlock(string p, string t1, string t2, string a, string[] mn, string[] mx) => new UIElement[]
    {
        SelRow("방향·각도 구간으로 Regression 데이터 선별", p + "Sel", "off", new[] { ("off", "사용 안 함"), ("on", "사용") }),
        Rng($"{t1} 동작에서 사용할 {a} min [deg]", p + mn[0], $"{t1} max [deg]", p + mx[0]),
        Rng($"{t2} 동작에서 사용할 {a} min [deg]", p + mn[1], $"{t2} max [deg]", p + mx[1]),
        Ui.Hint($"「사용」이면 {t1} 동작(각도 증가 방향 기준)의 샘플은 {t1} 구간 안에서만, {t2} 동작의 샘플은 {t2} 구간 안에서만 Regression에 씁니다. 비워 둔 한계는 제한 없음, 정지 구간은 제외합니다."),
    };

    bool AskEst(bool bkt, string id, TextBlock box)
    {
        var d = S.Data; if (d == null) { Msg(id, "CSV를 먼저 여세요", true, box); return false; }
        string a = bkt ? "T4min" : "T3min", b = bkt ? "T4max" : "T3max";
        if (!(double.IsFinite(K[a]) && double.IsFinite(K[b]) && K[a] < K[b])) { Msg(id, (bkt ? "T4" : "T3") + " Range의 min, max를 모두 입력하세요 (min < max, 단위 deg).", true, box); return false; }
        var (B, err) = WlAnalysis.Base(d, K, Sig); if (B == null) { Msg(id, err ?? "계산 실패", true, box); return false; }
        static double Span(double[] x, double k) { var f = x.Where(double.IsFinite).ToList(); return f.Count > 0 ? (f.Max() - f.Min()) * k : double.NaN; }
        double r3 = Span(B.T2, 1 / Kinematics.Rad), r4 = Span(B.T3, 1 / Kinematics.Rad), rl = Span(B.Lb, 1000);
        string F(double x) => double.IsFinite(x) ? x.ToString("F1", CultureInfo.InvariantCulture) + "°" : "?";
        var q = $"현재 열린 CSV「{S.FileName}」는 " + (bkt ? "Lift Arm이 정지한 상태에서 Bucket만 단독으로" : "Bucket 실린더가 정지한 상태에서 Lift Arm만 단독으로") + " 움직인 데이터입니까?\n\n"
            + $"Lift Arm 각도 변화폭 {F(r3)} · Bucket 각도 변화폭 {F(r4)} · Bucket 실린더 길이 변화폭 " + (double.IsFinite(rl) ? rl.ToString("F1", CultureInfo.InvariantCulture) + " mm" : "?")
            + (bkt && r3 > 3 ? "\n⚠ Lift Arm이 꽤 움직였습니다" : "") + (!bkt && rl > 3 ? "\n⚠ Bucket 실린더가 꽤 움직였습니다" : "");
        if (MessageBox.Show(this, q, "Parameter Estimation", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) return true;
        Msg(id, "취소했습니다.", false, box); return false;
    }

    void BuildE()
    {
        var p = Panel("e"); var d = S.Data;
        p.Children.Add(Ui.Hint("단독 동작 데이터에서 Dynamic Equation을 최소제곱으로 풀어 값을 구합니다 (관성 파라미터 + 쿨롱·압력쿨롱·점성 마찰). 추정에는 e-1에서 입력한 T4·T3 Range 안의 샘플만 사용합니다 (Range 필수)."));

        // e-1
        var e1 = new StackPanel(); var m0 = MsgBox("e0");
        e1.Children.Add(Ui.Hint("[Labeling]은 Range 안의 데이터에 Cycle·동작(Up/Down, Crowd/Dump) Label을 붙입니다 (WL_Cycle, WL_Motion 신호, Range 밖은 0)."));
        e1.Children.Add(Rng("Bucket T4 Range min [deg]", "T4min", "max [deg]", "T4max"));
        e1.Children.Add(Rng("Arm T3 Range min [deg]", "T3min", "max [deg]", "T3max"));
        e1.Children.Add(Ui.Row(Ui.Labeled("동작 방향 기준 (Labeling·구간 선별 공통)", Ui.Combo(new[] { ("up", "각도 증가 = Crowd(Bucket) / Up(Arm)"), ("dn", "각도 증가 = Dump(Bucket) / Down(Arm)") }, St.DirInv ? "dn" : "up", v => St.DirInv = v == "dn"))));
        e1.Children.Add(Ui.Row(
            Ui.Btn("Labeling: Bucket이 반복 동작", () => { if (d == null) return; Show(WlEstimation.Label(d, K, Sig, true, St.DirInv), "e0", m0); }),
            Ui.Btn("Labeling: Lift Arm이 반복 동작", () => { if (d == null) return; Show(WlEstimation.Label(d, K, Sig, false, St.DirInv), "e0", m0); })));
        e1.Children.Add(m0);
        p.Children.Add(Ui.Section("e-1. Range 데이터 Labeling", e1, true));

        // e-2
        var e2 = new StackPanel(); var mo = MsgBox("eo");
        e2.Children.Add(Ui.Hint("d-2의 각도·속도·압력과 Jacobian(J31, J32, J4)으로 회귀에 쓰는 관측 Vector 26종(H1–H5, M1–M6, N1–N3, Haaf1–4, Hkaf1–4, Hkkf1–4)과 Jacobian을 계산해 신호 목록에 추가합니다 (Bucket 추정 때도 추가됩니다)."));
        e2.Children.Add(Ui.Row(Ui.Btn("관측 Vector 계산", () => ObsRun(mo), true)));
        e2.Children.Add(mo);
        p.Children.Add(Ui.Section("e-2. 관측 Vector 계산", e2));

        // e-3
        var e3 = new StackPanel(); var mb = MsgBox("e1");
        e3.Children.Add(Rng("압력 안정도 가중 창 [s] (0=사용 안 함, 비움=0.5)", "WTwin", "IRLS 최대 반복 (비움=20)", "IRLSn"));
        e3.Children.Add(SelRow("Regression 모드 · Static: 속도·가속도 무시 / Dynamic: 전체", "BkMode", "dyn", new[] { ("dyn", "Dynamic"), ("static", "Static") },
            v => { foreach (var k in new[] { "uF4p1", "uF4p2", "uF4v" }) St.RegOn[k] = v != "static"; BuildE(); }));
        e3.Children.Add(SelRow("마찰·관성 계수 부호 제약 (I4zz, c, p1, p2, v ≥ 0)", "BkNN", "nn", new[] { ("nn", "비음수 제약"), ("free", "제약 없음") }));
        e3.Children.Add(Ui.Row(Ui.Labeled("Coulomb tanh 전이 속도 ε [mm/s] (비움: 자동)", N("VEps", 70))));
        e3.Children.Add(Ui.Row(Ui.Btn("압력 안정도 평가 · Charting", () => StabView(mb))));
        foreach (var x in SelBlock("Bk", "Crowd", "Dump", "θ4", new[] { "C0", "D0" }, new[] { "C1", "D1" })) e3.Children.Add(x);
        e3.Children.Add(Ui.Row(
            Ui.Btn("Bucket Parameter Estimation", () => { if (!AskEst(true, "e1", mb)) return; Show(WlEstimation.EstimateBucket(S.Data!, K, Sig, St), "e1", mb); BuildE(); }, true),
            Ui.Btn("Recalculate (사용값으로 Tq4 재계산)", () => Recalc(true, mb))));
        e3.Children.Add(EstTable(true));
        e3.Children.Add(Ui.Hint("체크박스: Regression 대상 선택 (해제하면 [사용값]으로 고정). dF4는 일정한 추력 오프셋, cJ4·kJ4는 Bucket–Arm Joint 회전 마찰입니다 (기본 해제)."));
        e3.Children.Add(mb);
        p.Children.Add(Ui.Section("e-3. Bucket Parameter Estimation", e3));

        // e-4
        var e4 = new StackPanel(); var ms = MsgBox("eb"); var mf = MsgBox("ef");
        e4.Children.Add(Ui.Hint("① [Sine Fitting]: Crowd·Dump 평균 곡선(마찰 상쇄)을 중력 Sine(T34, k=1)에 맞춰 M4·C4x, M4·C4y와 오프셋 C를 구합니다. ② [마찰력 Fitting]: 체크한 마찰 계수로 잔차(Tq4_Eq − SineFit)를 Fitting합니다. T4 Range·방향 기준(e-1), 구간 선별·가중·ε·부호 제약(e-3) 설정을 따릅니다."));
        e4.Children.Add(SelRow("Sine 피팅 오프셋 C", "BrC", "free", new[] { ("free", "허용"), ("zero", "0 고정") }));
        e4.Children.Add(SelRow("Sine 피팅에 추력 오프셋 dF4·J4 항 포함", "BrD", "off", new[] { ("off", "제외"), ("on", "포함") }));
        e4.Children.Add(Ui.Row(Ui.Btn("Sine Fitting", () =>
        {
            if (d == null) { Msg("eb", "CSV를 먼저 여세요", true, ms); return; }
            var r = WlSineFric.Sine(d, K, Sig, St, out var est); if (est != null) _br = est; Show(r, "eb", ms); BuildE();
        }, true)));
        var b = _br;
        e4.Children.Add(Ui.Table(new[] { "Sine Fitting 결과", "추정값 (±1σ)" }, new[]
        {
            new UIElement[] { Ui.Text("M4·C4x [kg·m]"), Ui.Text(b == null ? "—" : Fmt(b.X.V, b.X.Se)) },
            new UIElement[] { Ui.Text("M4·C4y [kg·m]"), Ui.Text(b == null ? "—" : Fmt(b.Y.V, b.Y.Se)) },
            new UIElement[] { Ui.Text("Sine 오프셋 C [kN·m]"), Ui.Text(b == null ? "—" : Fmt(b.C.V, b.C.Se)) },
            new UIElement[] { Ui.Text("dF4_ForceOffset [N]"), Ui.Text(b == null ? "—" : Fmt(b.D.V, b.D.Se)) },
            new UIElement[] { Ui.Text("k 자유 피팅 최적 k"), Ui.Text(b == null ? "—" : Fmt(b.KBest)) },
        }));
        e4.Children.Add(ms);
        e4.Children.Add(Ui.Row(
            Ui.Btn("마찰력 Fitting", () =>
            {
                if (d == null || _br == null) { Msg("ef", "먼저 [Sine Fitting]을 실행하세요.", true, mf); return; }
                Show(WlSineFric.Friction(d, K, Sig, St, _br, _regOn2), "ef", mf); BuildE();
            }, true),
            Ui.Btn("사용값 → 표로 복사 (e-3 [사용값])", () =>
            {
                if (_br == null) { Msg("ef", "먼저 [Sine Fitting]을 실행하세요.", true, mf); return; }
                WlSineFric.CopyToK(K, _br, _regOn2); Msg("ef", "[사용값]에 M4C4x, M4C4y와 Regression 대상 마찰 계수를 복사했습니다.", false); BuildE();
            })));
        string[] fk = { "uF4c", "uF4p1", "uF4p2", "uF4v", "uF4d", "uFjc", "uFjf" };
        string[] fn = { "cF4_Coulomb [N]", "cF4_PressureCoulomb1 [N/bar] (Head)", "cF4_PressureCoulomb2 [N/bar] (Rod)", "cV4_Viscous [N·s/m]", "dF4_ForceOffset [N] (Sine 단계 dF4 사용 시 고정)", "cJ4_JointCoulomb [N·m]", "kJ4_JointForceProp [m]" };
        bool useD = b?.UseD == true;
        e4.Children.Add(Ui.Table(new[] { "☑ 마찰력 Fitting 대상 · 계수", "추정값 (±1σ)" }, fk.Select((k, j) =>
        {
            var c = Ui.Check(fn[j], RegOn2(k) && !(useD && j == 4), v => _regOn2[k] = v); c.IsEnabled = !(useD && j == 4);
            var val = b?.Fric != null ? b.Fric[j] : (double.NaN, double.NaN); if (j == 4 && b != null) val = b.D;
            return new UIElement[] { c, Ui.Text(Fmt(val.Item1, val.Item2)) };
        })));
        e4.Children.Add(mf);
        p.Children.Add(Ui.Section("e-4. Sine 피팅 기반 Bucket 추정 (평균 → 중력 Sine → 마찰 Fitting)", e4));

        // e-5
        var e5 = new StackPanel(); var ma = MsgBox("e2");
        foreach (var x in SelBlock("Ar", "Up", "Down", "θ3", new[] { "U0", "D0" }, new[] { "U1", "D1" })) e5.Children.Add(x);
        e5.Children.Add(Ui.Row(
            Ui.Btn("Arm Parameter Estimation", () => { if (!AskEst(false, "e2", ma)) return; Show(WlEstimation.EstimateArm(S.Data!, K, Sig, St), "e2", ma); BuildE(); }, true),
            Ui.Btn("Recalculate (사용값으로 Tq3 재계산)", () => Recalc(false, ma))));
        e5.Children.Add(EstTable(false));
        e5.Children.Add(Ui.Row(Ui.Btn("입력값 → 사용값 복사", () =>
        {
            var iv = InputVals(); var keys = BktRows.Concat(ArmRows).Select(r => r.Key).ToArray();
            for (int j = 0; j < keys.Length; j++) if (double.IsFinite(iv[j])) K[keys[j]] = double.Parse(iv[j].ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            BuildE();
        })));
        e5.Children.Add(ma);
        p.Children.Add(Ui.Section("e-5. Arm Parameter Estimation", e5));
    }

    readonly Dictionary<string, bool> _regOn2 = new();
    bool RegOn2(string k) => _regOn2.TryGetValue(k, out var v) ? v : k is not ("uF4d" or "uFjc" or "uFjf");

    void ObsRun(TextBlock box)
    {
        var d = S.Data; if (d == null) { Msg("eo", "CSV를 먼저 여세요", true, box); return; }
        var (B, err) = WlAnalysis.Base(d, K, Sig); if (B == null) { Msg("eo", err ?? "계산 실패", true, box); return; }
        var kn = WlAnalysis.Kin(d, Sig, B);
        var (l3, h3) = WlEstimation.AngRange(K, "T3min", "T3max"); var (l4, h4) = WlEstimation.AngRange(K, "T4min", "T4max");
        double e3 = WlEstimation.EpsOf(K, B.VA, i => B.T2[i] >= l3 && B.T2[i] <= h3), e4 = WlEstimation.EpsOf(K, B.VB, i => B.T3[i] >= l4 && B.T3[i] <= h4);
        var O = WlEstimation.Obs(B, kn, e3, e4); var res = new CalcResult();
        WlEstimation.PutObs(res, O, new List<string>());
        var miss = WlEstimation.ObsKeys.Where(k => !O.TryGetValue(k, out var a) || !a.Any(double.IsFinite)).ToList();
        if (res.Outputs.Count == 0) { Msg("eo", "계산된 관측 Vector가 없습니다 (d-1 각도·형상 설정을 확인하세요).", true, box); return; }
        _show(res);
        Msg("eo", "✓ 관측 Vector 26종 + Jacobian 3종 계산 → 신호 목록: H1–H5, M1–M6, N1–N3, Haaf1–4, Hkaf1–4, Hkkf1–4, J31, J32, J4\n"
            + $"  ε(Arm) = {e3 * 1e3:G3} mm/s, ε(Bucket) = {e4 * 1e3:G3} mm/s" + (miss.Count > 0 ? "\n⚠ 계산되지 않은 항목: " + string.Join(", ", miss) + " (해당 실린더 압력 열·직경 필요)" : ""), miss.Count > 0, box);
    }

    void StabView(TextBlock box)
    {
        var d = S.Data; if (d == null) { Msg("e1", "CSV를 먼저 여세요", true, box); return; }
        var (B, err) = WlAnalysis.Base(d, K, Sig); if (B == null) { Msg("e1", err ?? "계산 실패", true, box); return; }
        if (B.CB == null) { Msg("e1", "Bucket 실린더 압력(Head/Rod 열)과 Tube/Rod 직경이 필요합니다.", true, box); return; }
        var kn = WlAnalysis.Kin(d, Sig, B); double winS = double.IsFinite(K["WTwin"]) ? K["WTwin"] : .5;
        var (lo, hi) = WlEstimation.AngRange(K, "T4min", "T4max"); var ix = new List<int>();
        for (int i = 0; i < B.N; i++) if (B.T3[i] >= lo && B.T3[i] <= hi) ix.Add(i);
        if (ix.Count < 10) { Msg("e1", "T4 Range 안의 샘플이 부족합니다.", true, box); return; }
        if (!(winS > 0)) { Msg("e1", "압력 안정도 가중 창이 0이면 평가하지 않습니다 (창 [s]를 입력하세요).", true, box); return; }
        var W = WlEstimation.StabW(new[] { B.CB.HP, B.CB.RP }, kn.Ts, winS, ix);
        var sm = WlEstimation.SelMask(true, B, kn, K, St.DirInv);
        var use = new double[B.N]; Array.Fill(use, double.NaN);
        for (int i = 0; i < B.N; i++) if (B.T3[i] >= lo && B.T3[i] <= hi) use[i] = sm != null ? (sm[i] ? 1 : 0) : 1;
        var r = new CalcResult();
        r.Outputs.Add(new("WL_Bkt_pHead_HF_bar", W.Hf[0].Select(v => v / 1e5).ToArray(), "bar")); r.Outputs.Add(new("WL_Bkt_pRod_HF_bar", W.Hf[1].Select(v => v / 1e5).ToArray(), "bar"));
        r.Outputs.Add(new("WL_Bkt_pStab_Head", W.Zl[0], "")); r.Outputs.Add(new("WL_Bkt_pStab_Rod", W.Zl[1], "")); r.Outputs.Add(new("WL_Bkt_pStab_Idx", W.Z, ""));
        r.Outputs.Add(new("WL_Bkt_pStab_Weight", W.W, "")); r.Outputs.Add(new("WL_Bkt_Use", use, ""));
        r.Charts.Add(new[] { "WL_Bkt_pHead_HF_bar", "WL_Bkt_pRod_HF_bar" }); r.Charts.Add(new[] { "WL_Bkt_pStab_Head", "WL_Bkt_pStab_Rod", "WL_Bkt_pStab_Idx" });
        r.Charts.Add(new[] { "WL_Bkt_pStab_Weight" }); r.Charts.Add(new[] { "WL_Bkt_Use" });
        _show(r);
        double sw = ix.Sum(i => W.W[i]); int lw = ix.Count(i => W.W[i] < .3);
        Msg("e1", $"✓ 압력 안정도 평가 완료 (Range {ix.Count}개 샘플, 창 {winS} s)\n  WL_Bkt_pStab_Weight = 1/(1+(Idx/2)²) → Range 평균 {sw / ix.Count:F2}, 0.3 미만 {100.0 * lw / ix.Count:F1} %\n  WL_Bkt_Use = 방향·각도 선별 결과 (1 사용, 0 제외)", false, box);
    }

    void Recalc(bool bkt, TextBlock box)
    {
        string id = bkt ? "e1" : "e2"; var d = S.Data; if (d == null) { Msg(id, "CSV를 먼저 여세요", true, box); return; }
        var need = bkt ? new[] { "uM4C4x", "uM4C4y" } : new[] { "uM4C4x", "uM4C4y", "uJ4", "uP", "uQ", "uJ3" };
        var miss = need.Where(k => !double.IsFinite(K[k])).ToList();
        if (miss.Count > 0) { Msg(id, "[사용값]이 비어 있습니다 (" + string.Join(", ", miss) + "). 값을 입력하거나 Parameter Estimation을 먼저 실행하세요.", true, box); return; }
        var r = WlEstimation.EstSignals(d, K, Sig, St, bkt);
        if (r.Outputs.Count == 0 && r.Message == "") r.Message = "재계산할 수 있는 샘플이 없습니다 (각도·압력·실린더 열, 직경을 확인하세요).";
        if (r.Outputs.Count > 0 && r.Message == "") r.Message = "✓ Recalculate 완료: [사용값]으로 " + r.Outputs[0].Name + " 를 다시 계산했습니다.";
        Show(r, id, box);
    }

    // ================= f. Weighing =================

    void BuildF()
    {
        var p = Panel("f"); var m = MsgBox("f");
        p.Children.Add(Ui.Hint("e.의 [사용값]으로 Bucket에 실린 적재물 질량을 시계열로 추정합니다 (Arm·Bucket 두 동역학식을 함께 최소제곱 → WL_Payload_kg). 적재물 무게중심은 Bucket 좌표(길이 단위)이며 비우면 (0.5·L4, 0.3·L4). 통계 구간은 커서 A–B (없으면 전체)입니다."));
        p.Children.Add(Ui.Row(Ui.Labeled("적재물 무게중심 PYx (Bucket 좌표)", N("PYx", 70)), Ui.Labeled("PYy", N("PYy", 70))));
        p.Children.Add(Ui.Row(Ui.Btn("Weighing", () =>
        {
            var d = S.Data; if (d == null) { Msg("f", "CSV를 먼저 여세요", true, m); return; }
            double a = d.Start, b = d.End; string lb = "전체 구간";
            if (S.CursorA != null && S.CursorB != null) { a = Math.Min(S.CursorA.Value, S.CursorB.Value); b = Math.Max(S.CursorA.Value, S.CursorB.Value); lb = "커서 A–B 구간"; }
            Show(WlEstimation.Weigh(d, K, Sig, a, b, lb), "f", m);
        }, true)));
        p.Children.Add(m);
    }
}
