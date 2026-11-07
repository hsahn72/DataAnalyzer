using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataAnalyzer.Core.Analysis;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.App.Dialogs;

/// <summary>
/// Cycle 및 Action 분석 (HTML ⑤). 축별 조이스틱 열과 전이 조건을 정하고 실행하면
/// 신호 [Cycle](1…N)과 [Action](1 Digging, 2 Hauling, 3 Dumping, 4 Returning, 0 Idling)을 만든다.
/// </summary>
public sealed class CycleDialog : Window
{
    readonly AppState S;
    readonly CycleConfig C;
    readonly Action _onLabeled;
    readonly StackPanel _axesHost = new();
    readonly TextBlock _msg = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
    readonly DataGrid _table = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, MaxHeight = 220, HeadersVisibility = DataGridHeadersVisibility.Column,
        CanUserAddRows = false, FontSize = 12, Margin = new Thickness(0, 4, 0, 0),
    };
    readonly TextBlock _shiftNow = new() { Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    CycleRun? _run;
    DataSet? _runFor;

    static readonly (Axis Axis, string Name, string Rule)[] AxisInfo =
    {
        (Axis.Boom, "Boom", "+ = Boom Up(신장) / − = Down(수축)"),
        (Axis.Arm, "Arm", "+ = Crowd(신장) / − = Dump(수축)"),
        (Axis.Bucket, "Bucket", "+ = Crowd(신장) / − = Dump(수축)"),
        (Axis.Swing, "Swing", "양/음 = 좌/우 선회 (정지 = 0)"),
    };

    public CycleDialog(AppState state, CycleConfig cfg, Action onLabeled)
    {
        S = state; C = cfg; _onLabeled = onLabeled;
        Title = "Cycle 및 Action 분석";
        Width = 900; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Hint("각 축은 조이스틱 열로 동작 여부를 판단합니다. Swing 열과 Arm 열은 필수입니다. " +
            "조이스틱이 Extend/Retract 두 열로 분리된 경우 Extend(Swing: Left) 열이 켜지면 + 방향, Retract(Swing: Right) 열이 켜지면 − 방향으로 보며 열 값의 부호는 무관합니다. " +
            "단위가 MPa 등 압력이면 bar로 환산합니다."));

        var split = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        split.Children.Add(new TextBlock { Text = "조이스틱 신호가 Extend / Retract (Swing은 Left / Right)로 분리되어 있습니까?  ", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var yes = new RadioButton { Content = "예 — 각각 별도 열", IsChecked = C.Split, GroupName = "split", Margin = new Thickness(0, 0, 10, 0) };
        var no = new RadioButton { Content = "아니오 — 축마다 1열 (부호로 구분)", IsChecked = !C.Split, GroupName = "split" };
        yes.Checked += (_, _) => { C.Split = true; BuildAxes(); };
        no.Checked += (_, _) => { C.Split = false; BuildAxes(); };
        split.Children.Add(yes); split.Children.Add(no);
        root.Children.Add(split);
        root.Children.Add(_axesHost);
        BuildAxes();

        root.Children.Add(Hint("Action 전이 조건 (이전 시간 스텝의 Action 상태에서 조건이 만족되면 다음 Action으로 진입). 판정값은 Joystick 신호의 절대값 [bar]입니다. Swing은 Left/Right 중 큰 쪽."));
        var T = C.Tr;
        root.Children.Add(Rule("Digging → Hauling", "Swing ≥", Num(() => T.T1, v => T.T1 = v), "bar"));
        root.Children.Add(Rule("Hauling → Dumping", "Swing ≤", Num(() => T.T2a, v => T.T2a = v), "bar 이고, Arm Dump ≥", Num(() => T.T2b, v => T.T2b = v), "bar"));
        root.Children.Add(Rule("Dumping → Returning", "Swing ≥", Num(() => T.T3a, v => T.T3a = v), "bar 이고, Boom Down ≥", Num(() => T.T3b, v => T.T3b = v), "bar"));
        root.Children.Add(Rule("Returning → Idling (Action 0)", "모든 Joystick ≤", Num(() => C.IdleP, v => C.IdleP = v), "bar 가", Num(() => C.IdleT, v => C.IdleT = v), "초 이상 지속"));
        root.Children.Add(Rule("Returning(또는 Idling) → Digging (Cycle +1)", "Swing ≤", Num(() => T.T4a, v => T.T4a = v), "bar 이고, Arm Crowd ≥", Num(() => T.T4b, v => T.T4b = v), "bar"));
        root.Children.Add(Hint("첫 Cycle은 Returning → Digging 조건이 처음 만족되는 시점에서 시작합니다. Boom 신호가 없으면 Returning 조건에서 Boom Down 항은 무시합니다."));

        var runRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        runRow.Children.Add(new TextBlock { Text = "Action 최소 지속 시간 [s] ", VerticalAlignment = VerticalAlignment.Center, ToolTip = "각 Action은 최소 이 시간 동안 유지된 뒤에야 다음 Action으로 바뀝니다 (노이즈 제거 겸용)" });
        runRow.Children.Add(Num(() => C.MinT, v => C.MinT = v));
        var run = new Button { Content = "Cycle 및 Action 분석", Style = (Style)Application.Current.Resources["PrimaryButton"], Margin = new Thickness(10, 2, 2, 2) };
        run.Click += (_, _) => Run();
        runRow.Children.Add(run);
        root.Children.Add(runRow);

        var leadRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        leadRow.Children.Add(new TextBlock { Text = "후처리: Action 전이 시점을 ", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        leadRow.Children.Add(Num(() => C.Lead, v => C.Lead = v));
        leadRow.Children.Add(new TextBlock { Text = " 초 앞으로 (음수 = 뒤로) ", VerticalAlignment = VerticalAlignment.Center });
        var lead = new Button { Content = "당김" };
        lead.Click += (_, _) =>
        {
            if (_run == null || _runFor != S.Data) { ShowMsg("먼저 [Cycle 및 Action 분석]을 실행하세요.", true); return; }
            Label(C.Lead);
        };
        leadRow.Children.Add(lead);
        leadRow.Children.Add(_shiftNow);
        root.Children.Add(leadRow);

        root.Children.Add(_msg);
        foreach (var (h, prop) in new[]
        {
            ("Cycle", nameof(RowView.Cycle)), ("시작", nameof(RowView.Start)), ("Digging [s]", nameof(RowView.Dig)),
            ("Hauling [s]", nameof(RowView.Haul)), ("Dumping [s]", nameof(RowView.Dump)), ("Returning [s]", nameof(RowView.Ret)),
            ("Idling [s]", nameof(RowView.Idle)), ("합계 [s] (Idling 제외)", nameof(RowView.Total)),
        })
            _table.Columns.Add(new DataGridTextColumn { Header = h, Binding = new System.Windows.Data.Binding(prop) });
        root.Children.Add(_table);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public sealed record RowView(string Cycle, string Start, string Dig, string Haul, string Dump, string Ret, string Idle, string Total);

    static TextBlock Hint(string t) => new() { Text = t, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) };

    static TextBox Num(Func<double> get, Action<double> set)
    {
        var tb = new TextBox { Width = 56, Text = get().ToString(CultureInfo.InvariantCulture), Margin = new Thickness(2, 1, 2, 1) };
        tb.TextChanged += (_, _) =>
        {
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) set(v);
            else set(double.NaN);
        };
        return tb;
    }

    static StackPanel Rule(string title, params object[] parts)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        p.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Width = 270, VerticalAlignment = VerticalAlignment.Center });
        foreach (var x in parts)
            p.Children.Add(x is string s ? new TextBlock { Text = " " + s + " ", VerticalAlignment = VerticalAlignment.Center } : (UIElement)x);
        return p;
    }

    void BuildAxes()
    {
        _axesHost.Children.Clear();
        var d = S.Data;
        if (d == null) return;
        var opts = new[] { "" }.Concat(d.Names).ToList();
        string Disp(string n) => n.Length == 0 ? "(없음)" : (S.UnitOf(n) is { Length: > 0 } u ? $"{n}  [{u}]" : n);
        var g = new Grid();
        bool sp = C.Split;
        var heads = sp
            ? new[] { "축", "조이스틱 Extend 열 (Swing: Left)", "조이스틱 Retract 열 (Swing: Right)", "부호 반전", "부호 규약" }
            : new[] { "축", "조이스틱 열", "부호 반전", "부호 규약" };
        foreach (var _ in heads) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition());
        for (int j = 0; j < heads.Length; j++)
        {
            var h = new TextBlock { Text = heads[j], FontWeight = FontWeights.SemiBold, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 10, 2) };
            Grid.SetColumn(h, j); g.Children.Add(h);
        }
        int row = 1;
        foreach (var (ax, name, rule) in AxisInfo)
        {
            var a = C.Axes[ax];
            g.RowDefinitions.Add(new RowDefinition());
            var cells = new List<UIElement> { new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center } };
            ComboBox Pick(string cur, Action<string> set)
            {
                var cb = new ComboBox { Width = 230, Margin = new Thickness(0, 1, 10, 1) };
                foreach (var o in opts) cb.Items.Add(new ComboBoxItem { Content = Disp(o), Tag = o });
                cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur) ?? cb.Items[0];
                cb.SelectionChanged += (_, _) => set((string)((ComboBoxItem)cb.SelectedItem).Tag);
                return cb;
            }
            if (sp) { cells.Add(Pick(a.JoyP, v => a.JoyP = v)); cells.Add(Pick(a.JoyN, v => a.JoyN = v)); }
            else cells.Add(Pick(a.Joy, v => a.Joy = v));
            var inv = new CheckBox { IsChecked = a.Invert, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            inv.Click += (_, _) => a.Invert = inv.IsChecked == true;
            cells.Add(inv);
            cells.Add(new TextBlock { Text = rule, Foreground = Brushes.DimGray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            for (int j = 0; j < cells.Count; j++) { Grid.SetRow(cells[j], row); Grid.SetColumn(cells[j], j); g.Children.Add(cells[j]); }
            row++;
        }
        _axesHost.Children.Add(g);
    }

    void ShowMsg(string text, bool error)
    {
        _msg.Text = text;
        _msg.Foreground = error ? Brushes.Red : Brushes.Black;
    }

    void Run()
    {
        var d = S.Data;
        if (d == null) { ShowMsg("CSV를 먼저 여세요", true); return; }
        _run = CycleAnalysis.Run(d, d.TimeSeconds(), C, out var err);
        if (_run == null) { ShowMsg(err, true); return; }
        _runFor = d;
        Label(0);
    }

    void Label(double shift)
    {
        var d = S.Data!;
        var lab = CycleAnalysis.Label(_run!, shift);
        S.PutSignal("Cycle", lab.Cycle);
        S.PutSignal("Action", lab.Action);
        _runFor = S.Data; // PutSignal은 같은 DataSet에 추가
        _onLabeled();
        string F(double v) => double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";
        var rows = lab.Rows.Select(r => new RowView(r.Cycle.ToString(), S.TimeText(d.Time[r.StartIndex]), F(r.Digging), F(r.Hauling), F(r.Dumping), F(r.Returning), F(r.Idling), F(r.Total))).ToList();
        rows.Add(new RowView("평균 ± σ", "",
            CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Digging)), CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Hauling)),
            CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Dumping)), CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Returning)),
            CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Idling)), CycleAnalysis.MeanSd(lab.Rows.Select(r => r.Total))));
        _table.ItemsSource = rows;
        int k0 = (int)Math.Round(lab.Shift / _run!.Dt);
        var text = $"✓ {lab.Rows.Count}개 Cycle 라벨링 완료 → 신호 [Cycle] (1…N), [Action] (1=Digging, 2=Hauling, 3=Dumping, 4=Returning, 0=Idling 또는 구간 밖; Idling·구간 밖은 Cycle도 0)";
        if (k0 != 0) text += $"\n※ 후처리 적용: Action 전이 시점을 {Math.Abs(lab.Shift):0.000} s {(k0 > 0 ? "앞당김" : "뒤로 이동")} (분석 직후 결과 기준)";
        if (_run.Incomplete) text += "\n⚠ 마지막 미완성 Cycle은 라벨 제외(0)";
        if (lab.NoDump > 0) text += $"\n⚠ {lab.NoDump}개 Cycle에서 Arm Dump 시작을 찾지 못해 Dumping 구간이 비어 있습니다";
        text += "\n상단 [Cycle]/[Action] 콤보박스와 [Cycle 기준 View]로 필터·겹쳐보기를 할 수 있습니다.";
        ShowMsg(text, false);
        _shiftNow.Text = $"(현재 적용: {lab.Shift:0.000} s)";
    }
}
