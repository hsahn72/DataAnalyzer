using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataAnalyzer.Core.Data;
using DataAnalyzer.Core.Excavator;
using Microsoft.Win32;

namespace DataAnalyzer.App.Analysis;

/// <summary>
/// 굴착기 기구·동력 분석 창 (HTML exw). 왼쪽 = ① 파라미터 ② 신호 연결 ③ 각도·동역학 ④ Power ⑥ 관성 추정,
/// 오른쪽 = 커서 위치의 측면·상부 2D 뷰와 Animation(커서 A→B 재생, Tip 궤적 마커).
/// </summary>
public sealed class ExcavatorWindow : Window
{
    readonly AppState S;
    readonly Action<CalcResult> _show;

    KinParams K = KinParams.Demo();
    Kinematics? G;
    readonly ExcavatorSignals Sig = new();
    DataSet? _for;
    PowerConfig? _pw;
    readonly Dictionary<int, InertialSet> _ipCur = new();
    readonly Dictionary<int, InertialConfig> _ipCfg = new();
    readonly Dictionary<int, InertialResult> _ipRes = new();
    int _ipStep = 4;

    readonly Expander _xParam, _xSig, _xDyn, _xPw, _xIp;
    readonly TextBlock _status = new() { Foreground = Ui.Mute, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _calcMsg = Ui.Msg();
    readonly ExcavatorSideView _side = new();
    readonly ExcavatorTopView _top = new();
    readonly TextBlock _readout = new() { FontFamily = new FontFamily("Consolas, Malgun Gothic"), FontSize = 12, Margin = new Thickness(8, 4, 8, 6), TextWrapping = TextWrapping.Wrap };

    // Animation · 마커
    readonly Button _animBtn;
    readonly TextBox _dur = new() { Width = 40, Text = "5" };
    readonly ComboBox _shape = new(), _interval = new();
    readonly ComboBox _color = new();
    readonly CheckBox _auto = new() { Content = "재생마다 도형·색 변경", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly CheckBox _record = new() { Content = "커서 이동 시에도 기록", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly CheckBox _labels = new() { Content = "Pivot 이름", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    static readonly string[] Shapes = { "circle", "triangle", "square", "diamond", "cross" };
    static readonly string[] ShapeNames = { "원", "삼각형", "사각형", "마름모", "X" };
    static readonly string[] Colors = { "#ef4444", "#3b82f6", "#10b981", "#f59e0b", "#a855f7" };
    double? _animT; double _animFrom, _animTo, _animDur; readonly Stopwatch _sw = new(); int _sty;
    int? _lastTip; double? _lastT;
    readonly Dictionary<double[], double> _fmax = new();

    public ExcavatorWindow(AppState state, Action<CalcResult> show)
    {
        S = state; _show = show;
        Title = "굴착기 기구·동력 분석"; Width = 1280; Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");

        _xParam = Ui.Section("① Kinematic 파라미터", new StackPanel(), true);
        _xSig = Ui.Section("② CSV 신호 연결 (각도 / 실린더 길이)", new StackPanel(), true);
        _xDyn = Ui.Section("③ 각도 변환(for Animation) · 동역학 분석(for Tip 외력 계산)", new StackPanel());
        _xPw = Ui.Section("④ 소요 Power 계산 (펌프 · 실린더 · 선회모터)", new StackPanel());
        _xIp = Ui.Section("⑥ Inertial Parameter Estimation (Bucket → Arm → Boom)", new StackPanel());
        var left = new StackPanel { Margin = new Thickness(8) };
        foreach (var x in new[] { _xParam, _xSig, _xDyn, _xPw }) left.Children.Add(x);
        left.Children.Add(Ui.Hint("⑤ Cycle 및 Action 분석은 메인 창의 [Cycle·Action 분석] 버튼을 쓰세요."));
        left.Children.Add(_xIp);
        left.Children.Add(_calcMsg);
        _xPw.Expanded += (_, _) => BuildPower();

        // 오른쪽: Animation 조작 + 2D 뷰 + 읽기
        _animBtn = Ui.Btn("▶ Animation", ToggleAnim, true, "커서 A → B 구간을 재생합니다");
        for (int i = 0; i < Shapes.Length; i++) _shape.Items.Add(new ComboBoxItem { Content = ShapeNames[i], Tag = Shapes[i] });
        _shape.SelectedIndex = 0;
        foreach (var c in Colors) _color.Items.Add(new ComboBoxItem { Content = new Border { Width = 26, Height = 12, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)) }, Tag = c });
        _color.SelectedIndex = 0;
        foreach (var (v, t) in new[] { ("1", "매 샘플"), ("2", "2배"), ("4", "4배"), ("10", "10배"), ("20", "20배"), ("50", "50배"), ("100", "100배") })
            _interval.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        _interval.SelectedIndex = 4;
        _labels.Click += (_, _) => { _side.ShowLabels = _labels.IsChecked == true; _side.InvalidateVisual(); };
        var ctl = Ui.Row(_animBtn, Ui.Labeled("시간", _dur), Ui.Text("초"), Ui.Labeled("마커", _shape), _color,
            Ui.Labeled("마커 간격", _interval), _auto, _record, _labels,
            Ui.Btn("Clear", () => { _side.Markers.Clear(); _lastTip = null; Sync(); }, tip: "Tip 궤적 마커 지우기"),
            Ui.Btn("맞춤", _side.Fit, tip: "더블클릭으로도 가능"));
        ctl.Margin = new Thickness(8, 6, 8, 2);
        var views = new Grid();
        views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        views.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var b1 = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE8)), BorderThickness = new Thickness(1), Margin = new Thickness(8, 4, 4, 4), Child = _side };
        var b2 = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE8)), BorderThickness = new Thickness(1), Margin = new Thickness(4, 4, 8, 4), Child = _top };
        Grid.SetColumn(b2, 1); views.Children.Add(b1); views.Children.Add(b2);
        var right = new DockPanel();
        DockPanel.SetDock(ctl, Dock.Top); right.Children.Add(ctl);
        DockPanel.SetDock(_readout, Dock.Bottom); right.Children.Add(_readout);
        right.Children.Add(views);

        var head = new DockPanel { Margin = new Thickness(10, 6, 10, 2) };
        head.Children.Add(new TextBlock { Text = "굴착기 기구·동력 분석", FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(_status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        var sv = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var split = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE8)) };
        Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
        grid.Children.Add(sv); grid.Children.Add(split); grid.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head); root.Children.Add(grid);
        Content = root;

        LoadLastParams();
        S.Changed += OnStateChanged;
        S.DataChanged += OnDataChanged;
        Closed += (_, _) => { StopAnim(); S.Changed -= OnStateChanged; S.DataChanged -= OnDataChanged; };
        OnDataChanged();
    }

    // ================= 공통 =================

    ExcavatorAnalysis? Ex() => S.Data != null && G != null ? new ExcavatorAnalysis(S.Data, G, Sig) : null;

    void Status(string t, bool err = false) { _status.Text = t; _status.Foreground = err ? Ui.Err : Ui.Mute; }
    void CalcMsg(string t, bool err = false) => Ui.SetMsg(_calcMsg, t, err);

    void Show(CalcResult r)
    {
        if (r.Outputs.Count > 0) _show(r);
        if (r.Message != "") CalcMsg(r.Message, r.Warn);
    }

    void OnStateChanged() { if (_animT == null) Sync(); }

    void OnDataChanged()
    {
        var d = S.Data;
        if (d != _for)
        {
            _for = d; _pw = null; _ipRes.Clear();
            StopAnim(); _side.Markers.Clear(); _lastTip = null; _lastT = null; _fmax.Clear();
            if (d != null) { Sig.Guess(d); Sig.GuessDynamics(d); }
            _xSig.IsExpanded = true;
        }
        RebuildAll();
    }

    void RebuildAll()
    {
        BuildParam(); BuildSig(); BuildDyn(); if (_xPw.IsExpanded) BuildPower(); BuildIp(); Sync();
    }

    static StackPanel Panel(Expander x) { var p = new StackPanel(); ((Border)x.Content).Child = p; return p; }
    static void Header(Expander x, string t) => ((TextBlock)x.Header).Text = t;

    // ================= ① 파라미터 =================

    static string LastFile => Ui.AppDataFile("excavator_last.json");

    static SortedDictionary<string, string> LoadPresets() => AppStore.List("excavator");

    void LoadLastParams()
    {
        try { if (File.Exists(LastFile)) { SetK(KinParams.FromJson(File.ReadAllText(LastFile)), "지난번 파라미터를 불러왔습니다."); return; } } catch { }
        SetK(KinParams.Demo(), "예시 값을 불러왔습니다. 실제 장비 값으로 바꾸려면 [파라미터 편집]을 누르세요.");
    }

    string _kMsg = ""; bool _kErr;

    void SetK(KinParams k, string msg)
    {
        K = k; G = Kinematics.Build(k, out var err);
        _side.Markers.Clear(); _lastTip = null; _lastT = null; _side.Fit();
        if (G == null) { _kMsg = err ?? "파라미터 오류"; _kErr = true; Status("Kinematic 파라미터: " + _kMsg, true); }
        else
        {
            _kMsg = msg; _kErr = false; Status((k.Name == "" ? "(이름 없음)" : k.Name) + (k.Name == KinParams.Demo().Name ? "  ·  예시 값" : ""));
            try { File.WriteAllText(LastFile, k.ToJson()); } catch { }
        }
        if (IsLoaded) RebuildAll();
    }

    void BuildParam()
    {
        var p = Panel(_xParam);
        Header(_xParam, "① Kinematic 파라미터" + (G == null ? "  ⚠ 입력 필요" : ""));
        p.Children.Add(Ui.Row(new TextBlock { Text = K.Name == "" ? "(이름 없음)" : K.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }, Ui.Hint(K.Str("desc"))));
        var pre = new ComboBox { MinWidth = 180 };
        pre.Items.Add(new ComboBoxItem { Content = "예시 값 (20t급, 임의 값)", Tag = "__demo" });
        var presets = LoadPresets();
        foreach (var n in presets.Keys) pre.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        pre.SelectedIndex = 0;
        string Sel() => (string)((ComboBoxItem)pre.SelectedItem).Tag;
        var msg = Ui.Msg(); Ui.SetMsg(msg, AppStore.LastError != "" ? AppStore.LastError : _kMsg, _kErr || AppStore.LastError != "");
        p.Children.Add(Ui.Row(
            Ui.Btn("파라미터 편집…", EditParams, true),
            Ui.Btn("JSON 가져오기…", ImportJson),
            pre,
            Ui.Btn("불러오기", () =>
            {
                var v = Sel();
                if (v == "__demo") SetK(KinParams.Demo(), "예시 값을 불러왔습니다.");
                else if (presets.TryGetValue(v, out var js)) SetK(KinParams.FromJson(js), "불러옴: " + v);
            }),
            Ui.Btn("현재 값 저장", () =>
            {
                var n = K.Name.Trim();
                if (n == "") { Ui.SetMsg(msg, "Machine Name이 있어야 저장할 수 있습니다 ([파라미터 편집])", true); return; }
                AppStore.Put("excavator", n, K.ToJson()); _kMsg = "저장했습니다: " + n + " (" + AppStore.Where + ")"; _kErr = false; BuildParam();
            }, tip: AppStore.SaveTip + " — Machine Name으로"),
            Ui.Btn("삭제", () =>
            {
                var v = Sel();
                if (v == "__demo") { Ui.SetMsg(msg, "예시 값은 삭제할 수 없습니다", true); return; }
                AppStore.Remove("excavator", v); _kMsg = "삭제됨: " + v; _kErr = false; BuildParam();
            })));
        p.Children.Add(msg);
    }

    void EditParams()
    {
        new KinParamDialog("Kinematic 파라미터 편집", K, k =>
        {
            SetK(k, "적용됨");
            if (S.Data != null && Sig.Confirmed) CalcAngles();
            else if (S.Data != null) CalcMsg("파라미터 적용됨. ② 신호 연결을 확인하면 각도 변환이 실행됩니다.");
            return null;
        }) { Owner = this }.ShowDialog();
    }

    void ImportJson()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { SetK(KinParams.FromJson(File.ReadAllText(dlg.FileName)), "불러옴: " + Path.GetFileName(dlg.FileName)); }
        catch (Exception ex) { _kMsg = "파일을 읽을 수 없습니다: " + ex.Message; _kErr = true; BuildParam(); }
    }

    // ================= ② 신호 연결 =================

    void Unconfirm() { Sig.Confirmed = false; BuildSig(); Sync(); }

    void BuildSig()
    {
        var p = Panel(_xSig); var d = S.Data;
        Header(_xSig, "② CSV 신호 연결 (각도 / 실린더 길이)" + (d != null && !Sig.Confirmed ? "  ⚠ 확인 필요" : ""));
        if (d == null) { p.Children.Add(Ui.Hint("CSV를 먼저 여세요")); return; }
        var L = Sig.Links;
        var conf = new Border
        {
            Background = new SolidColorBrush(Sig.Confirmed ? Color.FromRgb(0xEC, 0xFD, 0xF5) : Color.FromRgb(0xFF, 0xF7, 0xED)),
            BorderBrush = new SolidColorBrush(Sig.Confirmed ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0xF5, 0x9E, 0x0B)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 2, 0, 4),
        };
        var cp = new StackPanel(); conf.Child = cp;
        cp.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Sig.Confirmed ? "✓ 신호 연결을 확인했습니다."
                : L.Any(r => r.Ang != "" || r.Cyl != "" || r.Vel != "") ? "CSV 헤더에서 아래와 같이 자동 검색했습니다. 맞는지 확인하고, 틀리면 바꾼 뒤 [맞음 — 확인]을 누르세요."
                : "일치하는 헤더를 찾지 못했습니다. 아래에서 직접 지정한 뒤 [맞음 — 확인]을 누르세요.",
        });
        string Q(string s) => s == "" ? "—" : "\"" + s + "\"";
        var lines = (Sig.SwingAngle != "" ? $"선회:  각도 {Q(Sig.SwingAngle)}  단위 {(Sig.SwingAngleUnit == "" ? "⚠ 선택 필요" : Sig.SwingAngleUnit)}\n" : "")
            + string.Join("\n", L.Select((r, i) => $"{ExcavatorSignals.LinkKor[i]}:  각도 {Q(r.Ang)}  /  실린더 길이 {Q(r.Cyl)}  /  속도 {Q(r.Vel)}  →  {ExcavatorSignals.LinkState(d, r)}"));
        cp.Children.Add(Ui.Hint(lines));
        bool needU = Sig.SwingAngle != "" && Sig.SwingAngleUnit == "";
        var ok = Ui.Btn(Sig.Confirmed ? "✓ 확인됨" : "맞음 — 확인", () => { Sig.Confirmed = true; _xSig.IsExpanded = false; BuildSig(); Sync(); CalcAngles(); }, true);
        ok.IsEnabled = !needU; if (needU) ok.ToolTip = "선회 각도 단위를 선택하세요";
        cp.Children.Add(Ui.Row(ok, Ui.Btn("다시 검색", () => { Sig.Guess(d); BuildSig(); Sync(); })));
        p.Children.Add(conf);

        var rows = new List<UIElement[]>();
        for (int i = 0; i < 3; i++)
        {
            var r = L[i]; int j = i;
            var state = new TextBlock { Text = ExcavatorSignals.LinkState(d, r), Foreground = Ui.Mute, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            rows.Add(new UIElement[]
            {
                Ui.Text(ExcavatorSignals.LinkKor[i]),
                Ui.Columns(d, r.Ang, v => { r.Ang = v; Unconfirm(); }, Sig.AngCandidates[i], 130),
                Ui.Combo(new[] { ("deg", "deg"), ("rad", "rad") }, r.AngUnit, v => { r.AngUnit = v; Sync(); }),
                Ui.Num(r.Offset, v => { r.Offset = double.IsFinite(v) ? v : 0; Sync(); }, 50),
                Ui.Columns(d, r.Cyl, v =>
                {
                    r.Cyl = v; var u = ExcavatorSignals.UnitOf(d, v);
                    r.CylUnit = u is "mm" or "cm" or "m" ? u : v != "" && d.Signals.TryGetValue(v, out var a) ? (ExcavatorSignals.MedAbs(a) > 20 ? "mm" : "m") : r.CylUnit;
                    Unconfirm();
                }, Sig.CylCandidates[i], 130),
                Ui.Combo(new[] { ("mm", "mm"), ("cm", "cm"), ("m", "m") }, r.CylUnit, v => { r.CylUnit = v; Sync(); }),
                Ui.Columns(d, r.Vel, v =>
                {
                    r.Vel = v; r.VelApproved = false; var u = ExcavatorSignals.VelUnitOf(d.Units.GetValueOrDefault(v));
                    r.VelUnit = u != "" ? u : v != "" && d.Signals.TryGetValue(v, out var a) && ExcavatorSignals.MedAbs(a) > .8 ? "mmps" : "mps";
                    Unconfirm();
                }, null, 120),
                Ui.Combo(new[] { ("mps", "m/s"), ("mmps", "mm/s"), ("cmps", "cm/s") }, r.VelUnit, v => { r.VelUnit = v; r.VelApproved = false; Unconfirm(); }),
                state,
            });
        }
        p.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Ui.Table(new[] { "링크", "각도 열", "단위", "오프셋°", "실린더 길이 열", "단위", "실린더 속도 열", "단위", "계산 방식" }, rows),
        });
        for (int j = 0; j < 3; j++) if (IntegrationBlock(d, j) is { } blk) p.Children.Add(blk);

        var swc = Ui.Columns(d, Sig.SwingAngle, v => { Sig.SwingAngle = v; var u = ExcavatorSignals.UnitOf(d, v); Sig.SwingAngleUnit = u is "deg" or "rad" ? u : ""; Unconfirm(); }, Sig.SwingCandidates, 140);
        var swu = Ui.Combo(new[] { ("", "— 반드시 선택 —"), ("deg", "degree (°)"), ("rad", "radian (rad)") }, Sig.SwingAngleUnit, v => { Sig.SwingAngleUnit = v; Unconfirm(); });
        var swRow = Ui.Row(Ui.Labeled("선회 각도 열", swc), Ui.Labeled("단위", swu));
        p.Children.Add(needU ? new Border { BorderBrush = Ui.Err, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(3), Child = swRow } : swRow);
        p.Children.Add(Ui.Row(Ui.Labeled("각도 기준", Ui.Combo(new[] { ("rel", "링크 상대각 (붐=차체, 암=붐, 버켓=암 기준)"), ("abs", "수평 기준 절대각") }, Sig.Absolute ? "abs" : "rel", v => { Sig.Absolute = v == "abs"; Sync(); }))));
        p.Children.Add(Ui.Hint("★ = 헤더 키워드와 일치한 후보. 각도 열이 있으면 각도를 그대로 쓰고, 없으면 실린더 길이로 Kinematics를 계산합니다. 각도·길이 열이 모두 없으면 실린더 속도 열을 적분해 길이를 만들고(오프셋·Drift 보정 후 승인) 같은 방식으로 각도를 계산합니다."));
    }

    /// <summary>속도 열만 있는 링크: 적분 길이 보정·승인 (HTML exIntUI)</summary>
    UIElement? IntegrationBlock(DataSet d, int j)
    {
        var r = Sig.Links[j];
        if (r.Vel == "" || r.Ang != "" || (r.Cyl != "" && r.Cyl != ExcavatorAnalysis.IntegratedName(j)) || !d.Signals.ContainsKey(r.Vel)) return null;
        var ex = Ex();
        var box = new StackPanel();
        box.Children.Add(Ui.Bold($"{ExcavatorSignals.LinkKor[j]} 실린더 — 속도 열 \"{r.Vel}\" 적분 → 길이"));
        if (ex == null) { box.Children.Add(Ui.Hint("① 파라미터가 필요합니다.")); return Wrap(box); }
        var st = Ui.Msg(); var chart = new Canvas { Height = 80, Background = Brushes.White, ClipToBounds = true };
        void Upd()
        {
            var Lx = ex.IntegrateLength(j); var (lmin, lmax) = ex.StrokeRangeMm(j);
            double mn = Lx.Min(), mx = Lx.Max(); int outN = Lx.Count(v => v < lmin || v > lmax);
            st.Text = $"길이 {mn:0.#} ~ {mx:0.#} mm (변화폭 {mx - mn:0.#}) · 입력 범위 " + (double.IsFinite(lmin) && double.IsFinite(lmax) ? $"{lmin:0.#} ~ {lmax:0.#}" : "미입력")
                + $" mm · 범위 밖 {100.0 * outN / Lx.Length:F1} %" + (lmax - lmin < mx - mn ? "  ⚠ 변화폭이 행정보다 큼 → Drift 보정 필요" : "");
            chart.Children.Clear();
            double W = Math.Max(200, chart.ActualWidth), H = 80, lo = Math.Min(mn, double.IsFinite(lmin) ? lmin : mn), hi = Math.Max(mx, double.IsFinite(lmax) ? lmax : mx), pad = (hi - lo) * .08;
            if (!(pad > 0)) pad = 1; double y0 = lo - pad, y1 = hi + pad; double Y(double v) => H - (v - y0) / (y1 - y0) * H;
            if (lmax > lmin) { var band = new System.Windows.Shapes.Rectangle { Width = W, Height = Math.Max(0, Y(lmin) - Y(lmax)), Fill = new SolidColorBrush(Color.FromArgb(34, 34, 197, 94)) }; Canvas.SetTop(band, Y(lmax)); chart.Children.Add(band); }
            var pl = new System.Windows.Shapes.Polyline { Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), StrokeThickness = 1.2 };
            int n = Lx.Length, step = Math.Max(1, n / (int)W);
            for (int i = 0; i < n; i += step) pl.Points.Add(new Point(i / (double)(n - 1) * W, Y(Lx[i])));
            chart.Children.Add(pl);
        }
        chart.SizeChanged += (_, _) => Upd();
        box.Children.Add(Ui.Row(
            Ui.Labeled("오프셋 [mm]", Ui.Num(r.VelOffset, v => { r.VelOffset = double.IsFinite(v) ? v : 0; r.VelApproved = false; Upd(); }, 80)),
            Ui.Labeled("Drift (속도 바이어스) [mm/s]", Ui.Num(r.VelDrift, v => { r.VelDrift = double.IsFinite(v) ? v : 0; r.VelApproved = false; Upd(); }, 80)),
            Ui.Labeled("느린 Drift 제거 창 [s]", Ui.Num(r.VelWindow, v => { r.VelWindow = double.IsFinite(v) ? v : 0; r.VelApproved = false; Upd(); }, 60))));
        box.Children.Add(Ui.Row(
            Ui.Btn("범위 맞춤", () => { ex.FitRange(j); BuildSig(); }, tip: "최소/최대의 중앙이 Lmin~Lmax의 중앙이 되도록 오프셋 설정"),
            Ui.Btn("Drift 자동 추정", () => { ex.EstimateDrift(j); BuildSig(); }, tip: "적분 길이의 선형 추세를 속도 바이어스로 보고 제거한 뒤 범위 맞춤"),
            Ui.Btn("보정 초기화", () => { r.VelOffset = 0; r.VelDrift = 0; r.VelWindow = 0; r.VelApproved = false; BuildSig(); }),
            Ui.Btn(r.VelApproved ? "✓ 승인됨 (다시 승인)" : "승인 → 각도 변환에 사용", () =>
            {
                var nm = ex.ApproveIntegrated(j);
                var res = new CalcResult(); res.Outputs.Add(new OutSignal(nm, d.Signals[nm], "mm")); res.Charts.Add(new[] { nm });
                _show(res);
                if (Sig.Confirmed) CalcAngles(); else CalcMsg($"✓ {ExcavatorSignals.LinkKor[j]} 실린더 길이를 승인했습니다 ({nm}). [맞음 — 확인]을 누르면 각도로 변환합니다.");
                BuildSig();
            }, true)));
        box.Children.Add(chart); box.Children.Add(st);
        return Wrap(box);
        static Border Wrap(UIElement c) => new() { BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE8)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 6), Margin = new Thickness(0, 6, 0, 6), Child = c };
    }

    // ================= ③ 각도 변환 · 동역학 =================

    void CalcAngles()
    {
        var ex = Ex();
        if (ex == null) { CalcMsg(S.Data == null ? "CSV를 먼저 여세요" : "① Kinematic 파라미터를 확인하세요", true); return; }
        if (!Sig.Confirmed) { _xSig.IsExpanded = true; CalcMsg("② 신호 연결을 확인하세요", true); return; }
        Show(ex.CalcAngles());
    }

    void BuildDyn()
    {
        var p = Panel(_xDyn); var d = S.Data;
        if (d == null) { p.Children.Add(Ui.Hint("CSV를 먼저 여세요")); return; }
        p.Children.Add(Ui.Hint("① 파라미터 적용 + ② 신호 확인 후, CSV의 실린더 길이(또는 각도)를 링크 각도로 변환해 신호 목록·차트에 추가합니다."));
        p.Children.Add(Ui.Row(Ui.Btn("각도 변환 → 차트에 추가", CalcAngles, true),
            Ui.Labeled("각도 출력 단위", Ui.Combo(new[] { ("deg", "degree (°)"), ("rad", "radian (rad)"), ("both", "둘 다") }, Sig.AngleOut, v => Sig.AngleOut = v))));
        p.Children.Add(Ui.Bold("동역학용 압력 신호 (이름이 일치하면 자동 선택)"));
        p.Children.Add(Ui.Table(new[] { "신호", "열" }, ExcavatorSignals.PressureKeys.Select(kv =>
            new UIElement[] { Ui.Text($"{kv.Key}  ({kv.Label})"), Ui.Columns(d, Sig.P(kv.Key), v => Sig.Pressure[kv.Key] = v, null, 180) })
            .Append(new UIElement[]
            {
                Ui.Text("선회 속도 (선택)"),
                Ui.Row(Ui.Columns(d, Sig.SwingSpeed, v =>
                {
                    Sig.SwingSpeed = v; var u = Units.Normalize(d.Units.GetValueOrDefault(v));
                    if (u is "dps" or "rps" or "rpm") { Sig.SwingSpeedUnit = u; BuildDyn(); }
                }, null, 180), Ui.Combo(new[] { ("dps", "deg/s"), ("rps", "rad/s"), ("rpm", "rpm") }, Sig.SwingSpeedUnit, v => Sig.SwingSpeedUnit = v)),
            })));
        var sx = Ui.Row(Ui.Bold("추력 입력 보정"));
        for (int j = 0; j < 3; j++) { int q = j; sx.Children.Add(Ui.Check(ExcavatorSignals.LinkKor[j] + " Head↔Rod 교환", Sig.Swap[j], v => Sig.Swap[q] = v, "Head/Rod 압력 열을 반대로 지정했을 때 바로잡기")); }
        sx.Children.Add(Ui.Check("추력 부호 전체 반전", Sig.Negate, v => Sig.Negate = v, "실린더 신장 방향 정의가 반대일 때"));
        p.Children.Add(sx);
        p.Children.Add(Ui.Row(Ui.Labeled("Tip 외력 표시", Ui.Combo(new[] { ("off", "환경 → 버켓 (버켓이 받는 힘)"), ("on", "버켓 → 환경 (굴착력, 반대 방향)") }, Sig.ForceOnEnvironment ? "on" : "off", v => Sig.ForceOnEnvironment = v == "on"))));
        var opt = Ui.Row(Ui.Labeled("평활 창(s)", Ui.Num(Sig.SmoothWindow, v => Sig.SmoothWindow = double.IsFinite(v) ? Math.Max(0, v) : 0, 60)),
            Ui.Check("중력 포함", Sig.Gravity, v => Sig.Gravity = v));
        if (!d.IsDate) opt.Children.Add(Ui.Labeled("시간 단위", Ui.Combo(new[] { ("s", "s"), ("ms", "ms") }, Sig.TimeUnit, v => Sig.TimeUnit = v)));
        p.Children.Add(opt);
        var diag = Ui.Msg();
        p.Children.Add(Ui.Row(
            Ui.Btn("동역학 계산 → Tip 외력 차트", () =>
            {
                var ex = Ex();
                if (ex == null) { CalcMsg("① 파라미터와 CSV가 필요합니다", true); return; }
                if (!Sig.Confirmed) { _xSig.IsExpanded = true; CalcMsg("② 신호 연결을 확인하세요", true); return; }
                _fmax.Clear(); Show(ex.CalcDynamics()); Sync();
            }, true),
            Ui.Btn("Head/Rod 조합 진단 (커서 A–B 구간)", () =>
            {
                var ex = Ex();
                if (ex == null) { Ui.SetMsg(diag, "① 파라미터와 CSV가 필요합니다", true); return; }
                if (S.CursorA == null || S.CursorB == null) { Ui.SetMsg(diag, "커서 A·B가 모두 필요합니다 (메인 창의 [커서 B] 버튼)", true); return; }
                var (t, e) = ex.DiagnoseHeadRod(S.CursorA.Value, S.CursorB.Value, x => S.TimeText(x));
                Ui.SetMsg(diag, t, e);
            }, tip: "커서 A–B 구간에서 Head↔Rod 교환 조합별 Tip 외력 크기와 F·v를 비교")));
        p.Children.Add(diag);
        p.Children.Add(Ui.Hint("각도·각속도·각가속도는 평활(이동평균) 후 수치 미분하므로 창이 너무 작으면 가속도 노이즈가 커집니다. 선회 속도 열이 없으면 선회 관련 외력은 계산하지 않습니다."));
    }

    // ================= ④ Power =================

    static readonly (string V, string T)[] PressUnits = { ("bar", "bar"), ("mpa", "MPa"), ("kpa", "kPa"), ("psi", "psi"), ("pa", "Pa") };

    void BuildPower()
    {
        var p = Panel(_xPw); var d = S.Data;
        if (d == null) { p.Children.Add(Ui.Hint("CSV를 먼저 여세요")); return; }
        _pw ??= PowerCalc.Guess(d, Sig, K);
        var c = _pw;
        bool IsP(string v) => Units.PressureToPa(d.Units.GetValueOrDefault(v)) != null;
        string PUnit(string v) => Units.Normalize(d.Units.GetValueOrDefault(v));
        p.Children.Add(Ui.Hint("각 물리량이 들어 있는 열을 지정하세요 (헤더로 자동 검색한 후보가 미리 선택됩니다). Power > 0 : 유압이 일을 함, Power < 0 : 외력이 Actuator를 구동(회생 가능)."));
        p.Children.Add(Ui.Bold("1. Main Pump  (P[kW] = p[bar]·Q[lpm]/600)"));
        p.Children.Add(Ui.Table(new[] { "", "압력 열", "단위", "유량 열", "단위" }, c.Pump.Select((q, i) => new UIElement[]
        {
            Ui.Text("Pump " + (i + 1)),
            Ui.Columns(d, q.P, v => { q.P = v; if (IsP(v)) { q.PUnit = PUnit(v); BuildPower(); } }, null, 140),
            Ui.Combo(PressUnits, q.PUnit, v => q.PUnit = v),
            Ui.Columns(d, q.Q, v => { q.Q = v; var u = PowerCalc.FlowUnitOf(d.Units.GetValueOrDefault(v)); if (u != "") { q.QUnit = u; BuildPower(); } }, null, 140),
            Ui.Combo(new[] { ("lpm", "L/min"), ("lps", "L/s"), ("m3s", "m³/s"), ("ccs", "cc/sec") }, q.QUnit, v => q.QUnit = v),
        })));
        p.Children.Add(Ui.Bold("2. 실린더  (추력 = (pH·A헤드 − pR·A로드)×개수 ← ③의 압력 열 · P[kW] = 추력[kN] × 속도[m/s])"));
        string[] nmk = { "Boom", "Arm", "Bucket" };
        p.Children.Add(Ui.Table(new[] { "", "Head 열 (③)", "Rod 열 (③)", "속도 열 (없으면 ②에서 계산)", "속도 단위" }, c.Cyl.Select((q, j) => new UIElement[]
        {
            Ui.Text(nmk[j]),
            Ui.Text(Sig.P(ExcavatorSignals.CylPressureKeys[j][0]) is var h && h != "" ? h : "⚠ 미지정"),
            Ui.Text(Sig.P(ExcavatorSignals.CylPressureKeys[j][1]) is var r && r != "" ? r : "⚠ 미지정"),
            Ui.Columns(d, q.V, v => { q.V = v; var u = ExcavatorSignals.VelUnitOf(d.Units.GetValueOrDefault(v)); if (u != "") { q.VUnit = u; BuildPower(); } }, null, 140),
            Ui.Combo(new[] { ("mps", "m/s"), ("mmps", "mm/s"), ("cmps", "cm/s"), ("dm", "길이 열[m] → 미분"), ("dmm", "길이 열[mm] → 미분"), ("dcm", "길이 열[cm] → 미분") }, q.VUnit, v => q.VUnit = v),
        })));
        p.Children.Add(Ui.Hint("속도 열을 고르지 않으면 ②의 속도 열 → 실린더 길이 열(미분) → 각도(기구학으로 길이 환산 후 미분) 순으로 속도를 구합니다. 붐은 실린더 2개 합계 추력과 1개 속도를 곱합니다."));
        p.Children.Add(Ui.Bold("3. 선회 모터  (ΔP[bar] × Q[lpm]/600, Q = 모터 회전수[rpm] × 용적[cc/rev]/1000)"));
        var w = c.Sw;
        p.Children.Add(Ui.Table(new[] { "", "열 / 값", "단위 / 설정" }, new[]
        {
            new UIElement[] { Ui.Text("pSwA"), Ui.Columns(d, w.PA, v => { w.PA = v; if (IsP(v)) { w.PUnit = PUnit(v); BuildPower(); } }, null, 140), Ui.Combo(PressUnits, w.PUnit, v => w.PUnit = v) },
            new UIElement[] { Ui.Text("pSwB"), Ui.Columns(d, w.PB, v => w.PB = v, null, 140), Ui.Combo(new[] { ("AB", "ΔP = pSwA − pSwB"), ("BA", "ΔP = pSwB − pSwA") }, w.Dir, v => w.Dir = v) },
            new UIElement[] { Ui.Text("선회 속도"), Ui.Columns(d, w.Speed, v => { w.Speed = v; var u = Units.Normalize(d.Units.GetValueOrDefault(v)); if (u is "dps" or "rps" or "rpm") { w.SpeedUnit = u; BuildPower(); } }, null, 140),
                Ui.Combo(new[] { ("dps", "deg/s"), ("rps", "rad/s"), ("rpm", "rpm") }, w.SpeedUnit, v => w.SpeedUnit = v) },
            new UIElement[] { Ui.Text("감속비"), Ui.Num(w.Ratio, v => w.Ratio = v, 80), Ui.Check("선회 속도에 곱함 (이미 모터 축 속도면 해제)", w.UseRatio, v => w.UseRatio = v) },
            new UIElement[] { Ui.Text("모터 용적 [cc/rev]"), Ui.Num(w.Disp, v => w.Disp = v, 80), Ui.Text("") },
        }));
        p.Children.Add(Ui.Bold("4. 합산"));
        p.Children.Add(Ui.Hint("Boom·Arm·Bucket·Swing Power를 각각 0 이상으로 제한한 합 = Actuator_Power_kW. 음수였던 값만 모아 부호를 바꿔 더한 값 = Regen_Power_kW (회생 가능 Power)."));
        p.Children.Add(Ui.Row(Ui.Btn("계산 → 차트에 추가", () =>
        {
            var dd = S.Data; if (dd == null) return;
            var ex = Ex(); var ts = ex?.TimeSec() ?? dd.TimeSeconds(Sig.TimeUnit == "ms");
            var r = PowerCalc.Calc(dd, c, ex, ts, ExcavatorAnalysis.WinN(ts, Sig.SmoothWindow));
            if (r.Outputs.Count == 0 && r.Message == "") r.Message = "계산된 항목이 없습니다. 열을 지정하세요.";
            Show(r);
        }, true)));
    }

    // ================= ⑥ 관성 추정 =================

    static string IpFile => Ui.AppDataFile("excavator_inertial.json");
    sealed class IpStore { public Dictionary<string, Dictionary<int, InertialSet>> Sets { get; set; } = new(); public Dictionary<int, InertialSet> Work { get; set; } = new(); }
    static IpStore LoadIp() { try { return JsonSerializer.Deserialize<IpStore>(File.ReadAllText(IpFile)) ?? new(); } catch { return new(); } }
    /// <summary>저장된 세트 (이름 → 링크별 값). 예전 파일에 들어 있던 세트는 처음 한 번 저장소로 옮긴다.</summary>
    static SortedDictionary<string, Dictionary<int, InertialSet>> IpSets()
    {
        var old = LoadIp();
        if (old.Sets.Count > 0)
        {
            var have = AppStore.List(IpKind);
            foreach (var (n, v) in old.Sets) if (!have.ContainsKey(n)) AppStore.Put(IpKind, n, JsonSerializer.Serialize(v));
            old.Sets.Clear(); SaveIp(old);
        }
        var r = new SortedDictionary<string, Dictionary<int, InertialSet>>();
        foreach (var (n, js) in AppStore.List(IpKind))
            try { if (JsonSerializer.Deserialize<Dictionary<int, InertialSet>>(js) is { } v) r[n] = v; } catch (JsonException) { }
        return r;
    }
    const string IpKind = "excavator_inertial";
    static void SaveIp(IpStore s) { try { File.WriteAllText(IpFile, JsonSerializer.Serialize(s)); } catch { } }
    static readonly Dictionary<int, string> IpName = new() { [4] = "Bucket", [3] = "Arm", [2] = "Boom" };

    InertialConfig IpCfg(int k)
    {
        if (!_ipCfg.TryGetValue(k, out var c)) { c = new InertialConfig { M = K["M" + k] }; _ipCfg[k] = c; }
        return c;
    }

    void BuildIp()
    {
        var p = Panel(_xIp); var d = S.Data;
        if (d == null) { p.Children.Add(Ui.Hint("CSV를 먼저 여세요")); return; }
        if (_ipCur.Count == 0) foreach (var (k, v) in LoadIp().Work) _ipCur[k] = v;
        var msg = Ui.Msg();
        var st = IpSets();
        var sets = new ComboBox { MinWidth = 140 }; sets.Items.Add(new ComboBoxItem { Content = "(저장된 세트 선택)", Tag = "" });
        foreach (var n in st.Keys) sets.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        sets.SelectedIndex = 0;
        string SelSet() => (string)((ComboBoxItem)sets.SelectedItem).Tag;
        var setName = new TextBox { Width = 120 };
        void Work() { var s = LoadIp(); s.Work = new(_ipCur); SaveIp(s); }
        p.Children.Add(Ui.Row(Ui.Bold("Inertial 세트"), sets,
            Ui.Btn("불러오기", () => { var n = SelSet(); if (n == "") { Ui.SetMsg(msg, "불러올 세트를 선택하세요", true); return; } _ipCur.Clear(); foreach (var (k, v) in st[n]) _ipCur[k] = v; Work(); BuildIp(); }),
            Ui.Btn("삭제", () => { var n = SelSet(); if (n == "") return; AppStore.Remove(IpKind, n); BuildIp(); }),
            setName,
            Ui.Btn("세트로 저장", () =>
            {
                var n = setName.Text.Trim();
                if (n == "") { Ui.SetMsg(msg, "세트 이름을 입력하세요", true); return; }
                if (_ipCur.Count == 0) { Ui.SetMsg(msg, "저장할 확정 값이 없습니다", true); return; }
                AppStore.Put(IpKind, n, JsonSerializer.Serialize(_ipCur)); Work(); BuildIp();
            }),
            Ui.Btn("① 파라미터에 적용", () =>
            {
                if (_ipCur.Count == 0) { Ui.SetMsg(msg, "적용할 확정 값이 없습니다", true); return; }
                SetK(InertialEstimation.ApplyTo(K, _ipCur), "⑥ 추정값을 적용했습니다");
                CalcMsg("✓ ① 파라미터에 적용: " + string.Join(", ", _ipCur.Keys.Select(k => InertialEstimation.Kor(k))) + " (M, C, Izz)");
            }, tip: "확정된 M, Cx, Cy, Izz를 Kinematic 파라미터에 기입")));
        string F(double x, int dd = 4) => InertialEstimation.Fmt(x, dd);
        p.Children.Add(Ui.Table(new[] { "링크", "M [kg]", "Cx [m]", "Cy [m]", "Izz 핀 [kg·m²]", "Izz COM", "Coulomb [N·m]", "출처" },
            new[] { 4, 3, 2 }.Select(k => _ipCur.TryGetValue(k, out var s)
                ? new UIElement[] { Ui.Text(IpName[k]), Ui.Text(F(s.M)), Ui.Text(F(s.Cx)), Ui.Text(F(s.Cy)), Ui.Text(F(s.Ipiv)), Ui.Text(F(s.Icom)), Ui.Text(F(s.Fc, 1)), Ui.Hint(s.File + " " + s.Date) }
                : new UIElement[] { Ui.Text(IpName[k]), Ui.Hint("미확정 (① 값 사용)") })));
        var tabs = Ui.Row();
        foreach (var (k, i) in new[] { (4, 1), (3, 2), (2, 3) }) { int kk = k; tabs.Children.Add(Ui.Btn($"{i}. {IpName[k]}", () => { _ipStep = kk; BuildIp(); }, _ipStep == k)); }
        p.Children.Add(tabs);
        int K0 = _ipStep; var cfg = IpCfg(K0); string kor = InertialEstimation.Kor(K0); var ex = Ex();
        string need = K0 == 4 ? "없음 (Bucket 단독 동작, 공중)" : K0 == 3 ? "Bucket M4·C4·I4zz (Bucket 확정 값)" : "Arm, Bucket 의 M·C·Izz (이전 단계 확정 값)";
        p.Children.Add(Ui.Hint($"{kor} 단독 동작 시험 데이터(공중, 굴착 외력 없음)를 CSV로 연 상태에서 진행합니다. 필요한 기지 값: {need}"));
        var chk = Ui.Msg();
        p.Children.Add(Ui.Row(Ui.Btn("단독 동작 확인", () =>
        {
            if (ex == null) { Ui.SetMsg(chk, "① 파라미터와 ② 신호 연결이 필요합니다", true); return; }
            var c = InertialEstimation.Check(ex, K0, cfg.Threshold);
            Ui.SetMsg(chk, string.Join("\n", c.Stats.Select(s => $"{s.Name}: RMS {F(s.Rms, 3)} °/s · 움직임 {s.Active * 100:F0} %" + (double.IsFinite(s.Min) ? $" · 각도 {F(s.Min, 1)} ~ {F(s.Max, 1)}°" : "")))
                + "\n" + (c.Ok ? "✓ " + kor + " 단독 동작으로 보입니다." : "⚠ " + (c.Target.Active <= .03 ? kor + "이(가) 거의 움직이지 않습니다." : "다른 링크도 움직입니다: " + string.Join(", ", c.Bad.Select(b => b.Name)) + " ([다른 링크 움직임 샘플 제외]를 켜 두세요)")), !c.Ok);
        }), Ui.Labeled("움직임 판정 [°/s]", Ui.Num(cfg.Threshold, v => cfg.Threshold = double.IsFinite(v) ? Math.Max(0, v) : 2, 50))));
        p.Children.Add(chk);
        TextBox lo = Ui.Num(cfg.Lo, v => cfg.Lo = v, 60), hi = Ui.Num(cfg.Hi, v => cfg.Hi = v, 60);
        p.Children.Add(Ui.Row(Ui.Bold(kor + " 각도 Range [°]"), Ui.Labeled("Min", lo), Ui.Labeled("Max", hi),
            Ui.Btn("데이터 범위 채우기", () => { if (ex != null && InertialEstimation.AngleRange(ex, K0) is { } r) { cfg.Lo = r.Lo; cfg.Hi = r.Hi; lo.Text = Ui.F(r.Lo); hi.Text = Ui.F(r.Hi); } }),
            Ui.Hint("(상대각: " + (K0 == 4 ? "암 대비 버켓" : K0 == 3 ? "붐 대비 암" : "수평 대비 붐") + ")")));
        p.Children.Add(Ui.Row(Ui.Labeled($"M{K0} ({kor} 질량) [kg]", Ui.Num(cfg.M, v => cfg.M = v, 80)),
            Ui.Check("Coulomb 마찰", cfg.Coulomb, v => cfg.Coulomb = v, "sign(ω) 항"), Ui.Check("점성 마찰", cfg.Viscous, v => cfg.Viscous = v, "ω 항"),
            Ui.Check("상수 오프셋", cfg.Offset, v => cfg.Offset = v, "압력 센서 영점 오차 등"), Ui.Check("다른 링크 움직임 샘플 제외", cfg.ExcludeOthers, v => cfg.ExcludeOthers = v)));
        p.Children.Add(Ui.Row(
            Ui.Btn("Least Square 추정 → 차트", () =>
            {
                if (ex == null) { Ui.SetMsg(msg, "① 파라미터와 ② 신호 연결이 필요합니다", true); return; }
                var (res, outp, err) = InertialEstimation.Estimate(ex, K0, cfg, _ipCur, S.FileName);
                if (err != null || res == null) { Ui.SetMsg(msg, err ?? "추정 실패", true); return; }
                _ipRes[K0] = res; _show(outp); BuildIp();
            }, true),
            Ui.Btn("결과 확정", () =>
            {
                if (!_ipRes.TryGetValue(K0, out var r)) { Ui.SetMsg(msg, "먼저 추정을 실행하세요", true); return; }
                _ipCur[K0] = r.ToSet(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)); Work();
                CalcMsg($"✓ {kor} 결과를 확정했습니다. 다음 단계로 진행하세요."); BuildIp();
            })));
        p.Children.Add(msg);
        if (_ipRes.TryGetValue(K0, out var rr))
        {
            var rows = new List<UIElement[]>
            {
                new UIElement[] { Ui.Text($"M{K0} (지정) [kg]"), Ui.Text(F(rr.M)), Ui.Text("") },
                new UIElement[] { Ui.Text($"C{K0}x [m]"), Ui.Text(F(rr.Cx)), Ui.Text(F(rr.SdX)) },
                new UIElement[] { Ui.Text($"C{K0}y [m]"), Ui.Text(F(rr.Cy)), Ui.Text(F(rr.SdY)) },
                new UIElement[] { Ui.Text($"I{K0}zz (링크 시작 핀) [kg·m²]"), Ui.Text(F(rr.Ipiv)), Ui.Text(F(rr.SeJ)) },
                new UIElement[] { Ui.Text($"I{K0}zz (무게중심) [kg·m²]"), Ui.Text(F(rr.Icom)), Ui.Text("") },
            };
            if (cfg.Coulomb) rows.Add(new UIElement[] { Ui.Text("Coulomb 마찰 [N·m]"), Ui.Text(F(rr.Fc, 2)), Ui.Text("") });
            if (cfg.Viscous) rows.Add(new UIElement[] { Ui.Text("점성 마찰 [N·m·s/rad]"), Ui.Text(F(rr.Fv, 2)), Ui.Text("") });
            if (cfg.Offset) rows.Add(new UIElement[] { Ui.Text("상수 오프셋 [N·m]"), Ui.Text(F(rr.B0, 2)), Ui.Text("") });
            rows.Add(new UIElement[] { Ui.Text("샘플 수 · 사용 각도 [°]"), Ui.Text($"{rr.N}" + (rr.Outliers > 0 ? $" (이상치 {rr.Outliers}개 제외)" : "") + $" · {F(rr.Span.Min, 1)} ~ {F(rr.Span.Max, 1)}"), Ui.Text("") });
            rows.Add(new UIElement[] { Ui.Text("RMS 잔차 [N·m] · R² · 조건수"), Ui.Text($"{F(rr.Rms, 2)} · {F(rr.R2, 4)} · {rr.Cond.ToString("0.0e+0", CultureInfo.InvariantCulture)}"), Ui.Text("") });
            p.Children.Add(Ui.Table(new[] { "항목", "값", "±1σ" }, rows));
            if (rr.Warnings.Count > 0) p.Children.Add(new TextBlock { Text = "⚠ " + string.Join("\n⚠ ", rr.Warnings), Foreground = Ui.Warn, TextWrapping = TextWrapping.Wrap });
        }
        p.Children.Add(Ui.Hint("모델: τ_act(압력×dL/dθ) − τ_하류링크(기지) = X·gx + Y·gy + Jp·α [+ Coulomb·tanh(ω/ε) + 점성·ω + 오프셋]. X=M·Cx, Y=M·Cy, Jp=Izz(COM)+M·|C|². 링크 평면 운동만 쓰므로 Izz만 추정됩니다. M은 지정값을 사용합니다."));
    }

    // ================= 2D 뷰 동기화 · Animation =================

    double Fmax()
    {
        var d = S.Data; if (d == null || !d.Signals.TryGetValue("Tip_Fplane_kN", out var a)) return 0;
        if (!_fmax.TryGetValue(a, out var m)) { m = 0; foreach (var x in a) if (x > m) m = x; _fmax[a] = m; }
        return m;
    }

    double SigAt(string n, double t) => S.Data != null && S.Data.Signals.TryGetValue(n, out var a) ? Series.ValAt(S.Data.Time, a, t) : double.NaN;

    void Sync()
    {
        var d = S.Data;
        double? t = _animT ?? S.SelectedCursor;
        PoseAtTime? pose = null, ghost = null;
        if (d != null && G != null && t != null)
        {
            pose = ExcavatorPose.At(G, Sig, d, t.Value);
            if (t != _lastT) { _lastT = t; if (_animT != null || _record.IsChecked == true) Trail(t.Value); }
            if (_animT == null && S.CursorA != null && S.CursorB != null)
            {
                double o = S.SelectedCursor == S.CursorA ? S.CursorB.Value : S.CursorA.Value;
                ghost = ExcavatorPose.At(G, Sig, d, o);
            }
        }
        bool dyn = d != null && d.Signals.ContainsKey("Tip_Fx_kN");
        double fm = Fmax();
        _side.G = G; _side.Pose = pose; _side.Ghost = ghost; _side.HasDynamics = dyn;
        _side.Force = dyn && t != null ? (SigAt("Tip_Fx_kN", t.Value), SigAt("Tip_Fy_kN", t.Value), fm) : null;
        _side.Message = d == null ? "CSV 데이터를 먼저 여세요" : G != null && t == null ? "시계열 차트를 클릭해 커서를 놓으세요" : "";
        _top.G = G; _top.Pose = pose;
        _top.PsiDeg = d != null && t != null ? ExcavatorPose.SwingDeg(Sig, d, t.Value) : double.NaN;
        _top.Lateral = d != null && t != null && d.Signals.ContainsKey("Tip_Flat_kN") ? (SigAt("Tip_Flat_kN", t.Value), fm) : null;
        _side.InvalidateVisual(); _top.InvalidateVisual();
        Readout(pose, t);
    }

    void Readout(PoseAtTime? p, double? t)
    {
        if (S.Data == null) { _readout.Text = "CSV 데이터를 먼저 여세요."; return; }
        if (G == null) { _readout.Text = "① Kinematic 파라미터를 입력하세요."; return; }
        if (p == null || t == null) { _readout.Text = "시계열 차트를 클릭해 커서를 놓으세요."; return; }
        string F(double x, double u, int dd) => double.IsFinite(x) ? (x * u).ToString("F" + dd, CultureInfo.InvariantCulture) : "—";
        var lines = new List<string> { (_animT != null ? "▶ 재생 " : "커서 ") + "t = " + S.TimeText(t.Value) };
        for (int i = 0; i < 3; i++)
            lines.Add($"{ExcavatorSignals.LinkKor[i]}  θ {F(p.Th[i], 1 / Kinematics.Rad, 1)}°  (실린더 {F(p.Cyl[i], 1000, 0)} mm" + (p.Src[i] != "" ? " · " + p.Src[i] + " 입력" : "") + ")" + (p.Bad[i] ? "  ⚠ 기구학 해 없음 (길이·파라미터 확인)" : ""));
        lines.Add("Bucket Tip (x, z) = " + (p.Fk is { } f ? $"({f.Tip.X:F3}, {f.Tip.Y:F3}) m" : "— (신호 연결 확인)") + $"    마커 {_side.Markers.Count}개");
        lines.Add($"선회각 ψ = {F(ExcavatorPose.SwingDeg(Sig, S.Data, t.Value), 1, 1)}°    Tip 외력 Fx {F(SigAt("Tip_Fx_kN", t.Value), 1, 1)}  Fy {F(SigAt("Tip_Fy_kN", t.Value), 1, 1)}  |F| {F(SigAt("Tip_Fplane_kN", t.Value), 1, 1)} kN");
        _readout.Text = string.Join("\n", lines);
    }

    /// <summary>마커 간격(N 샘플)마다 Tip 위치 기록. 지난 위치~현재 위치 사이 샘플을 모두 처리 (HTML exTrail)</summary>
    void Trail(double t)
    {
        var d = S.Data; if (d == null || G == null) return;
        var tt = d.Time; int n = tt.Length;
        if (n == 0 || t < tt[0] || t > tt[^1]) { _lastTip = null; return; }
        int N = int.Parse((string)((ComboBoxItem)_interval.SelectedItem).Tag, CultureInfo.InvariantCulture);
        int k = Series.LowerBound(tt, t); if (k >= n) k = n - 1; if (k > 0 && t - tt[k - 1] <= tt[k] - t) k--;
        var prev = _lastTip; _lastTip = k;
        var list = new List<int>();
        if (prev == null) list.Add(k);
        else if (prev != k) { int dr = k > prev ? 1 : -1; for (int i = prev.Value + dr; dr > 0 ? i <= k : i >= k; i += dr) if (i % N == 0) list.Add(i); }
        if (list.Count > 4000) { int st = (int)Math.Ceiling(list.Count / 4000.0); list = list.Where((_, j) => j % st == 0).ToList(); }
        var shape = (string)((ComboBoxItem)_shape.SelectedItem).Tag; var col = (Color)ColorConverter.ConvertFromString((string)((ComboBoxItem)_color.SelectedItem).Tag);
        foreach (var i in list) { var r = ExcavatorPose.At(G, Sig, d, tt[i]); if (r.Fk is { } f) _side.Markers.Add(new TipMarker(f.Tip.X, f.Tip.Y, shape, col)); }
        if (_side.Markers.Count > 20000) _side.Markers.RemoveRange(0, _side.Markers.Count - 20000);
    }

    void ToggleAnim()
    {
        if (_animT != null) { StopAnim(); return; }
        if (S.Data == null || G == null) { Status("먼저 데이터를 열고 ① 파라미터를 확인하세요", true); return; }
        if (!Sig.Confirmed) { _xSig.IsExpanded = true; Status("② 신호 연결을 확인한 뒤 재생하세요", true); return; }
        if (S.CursorA == null || S.CursorB == null) { Status("커서 A·B가 모두 필요합니다 — 메인 창의 [커서 B] 버튼을 켜세요", true); return; }
        if (S.CursorA == S.CursorB) { Status("커서 A와 B의 위치가 같습니다", true); return; }
        if (_auto.IsChecked == true) { _sty++; _shape.SelectedIndex = _sty % Shapes.Length; _color.SelectedIndex = _sty % Colors.Length; }
        _animFrom = S.CursorA.Value; _animTo = S.CursorB.Value; _animDur = Math.Max(.3, Ui.P(_dur.Text) is var v && double.IsFinite(v) ? v : 5);
        _lastTip = null; _lastT = null; _animT = _animFrom; _animBtn.Content = "■ 정지";
        _sw.Restart(); CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double p = Math.Min(1, _sw.Elapsed.TotalSeconds / _animDur);
        _animT = _animFrom + (_animTo - _animFrom) * p;
        Sync();
        if (p >= 1) StopAnim();
    }

    void StopAnim()
    {
        CompositionTarget.Rendering -= OnFrame;
        if (_animT == null) return;
        _animT = null; _lastT = null; _lastTip = null; _animBtn.Content = "▶ Animation";
        Sync();
    }
}
