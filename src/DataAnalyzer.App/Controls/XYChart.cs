using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataAnalyzer.Core.Config;
using DataAnalyzer.Core.Data;
using ScottPlot;
using LineStyle = DataAnalyzer.Core.Config.LineStyle;
using SColor = ScottPlot.Color;

namespace DataAnalyzer.App.Controls;

/// <summary>
/// XY 차트: X축 = 신호. 기본은 시계열 차트의 표시 구간만 그리고, '전체 구간'을 켜면 모든 데이터.
/// 커서 A(초록 ●), B(주황 ■) 위치를 표시한다.
/// 조작: 휠 = 확대/축소 · 가운데·오른쪽 드래그 = 이동 · Y축 위 휠 = Y만 확대/축소 · 더블클릭 = 자동 범위.
/// </summary>
public sealed class XYChart : ChartBase
{
    readonly ComboBox _xsel = new() { MinWidth = 120, FontSize = 12 };
    readonly ComboBox _ysel = new() { MinWidth = 120, FontSize = 12, ToolTip = "Y 신호 추가" };
    readonly CheckBox _all = new() { Content = "전체 구간", VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), FontSize = 12 };
    readonly TextBox _xmin = Box(), _xmax = Box(), _ymin = Box(), _ymax = Box();

    public string? XSignal { get; private set; }
    public bool AllRange { get; private set; }
    public double? XMin { get; private set; }
    public double? XMax { get; private set; }
    public double? YMin { get; private set; }
    public double? YMax { get; private set; }

    /// <summary>마우스로 바꾼 범위 (null = 자동)</summary>
    (double X0, double X1, double Y0, double Y1)? _manual;
    (double X0, double X1, double Y0, double Y1) _last;
    bool _fillingCombos;

    static TextBox Box() => new() { Width = 64, FontSize = 12, ToolTip = "비우면 자동" };

    public XYChart(AppState state, string? x = null, IEnumerable<string>? ys = null) : base(state)
    {
        XSignal = x;
        if (ys != null) Signals.AddRange(ys);
        var del = new Button { Content = "✕", ToolTip = "XY 차트 삭제", Padding = new Thickness(6, 0, 6, 0) };
        del.Click += (_, _) => RaiseRemove();
        var row1 = new DockPanel { Margin = new Thickness(6, 4, 4, 0) };
        DockPanel.SetDock(del, Dock.Right); row1.Children.Add(del);
        var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        left.Children.Add(Label("X "));
        left.Children.Add(_xsel);
        left.Children.Add(new TextBlock { Width = 8 });
        left.Children.Add(_ysel);
        left.Children.Add(_all);
        row1.Children.Add(left);
        var row2 = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(6, 2, 4, 0) };
        row2.Children.Add(Label("X ")); row2.Children.Add(_xmin); row2.Children.Add(Label(" ~ ")); row2.Children.Add(_xmax);
        row2.Children.Add(Label("    Y ")); row2.Children.Add(_ymin); row2.Children.Add(Label(" ~ ")); row2.Children.Add(_ymax);
        var root = new DockPanel();
        foreach (var e in new FrameworkElement[] { row1, Chips, row2 }) { DockPanel.SetDock(e, Dock.Top); root.Children.Add(e); }
        root.Children.Add(Wp);
        Child = root;
        SetHeightFactor(2);

        _xsel.SelectionChanged += (_, _) =>
        {
            if (_fillingCombos || _xsel.SelectedItem is not string s) return;
            XSignal = s; _manual = null; RenderChips(); Rebuild(); Redraw();
        };
        _ysel.SelectionChanged += (_, _) =>
        {
            if (_fillingCombos || _ysel.SelectedIndex <= 0 || _ysel.SelectedItem is not string s) return;
            _fillingCombos = true; _ysel.SelectedIndex = 0; _fillingCombos = false;
            AddSignal(s);
        };
        _all.Click += (_, _) => { AllRange = _all.IsChecked == true; Redraw(); };
        Hook(_xmin, v => XMin = v); Hook(_xmax, v => XMax = v); Hook(_ymin, v => YMin = v); Hook(_ymax, v => YMax = v);

        Wp.PreviewMouseDown += OnDown;
        Wp.PreviewMouseMove += OnMove;
        Wp.PreviewMouseUp += OnUp;
        Wp.PreviewMouseWheel += OnWheel;

        FillCombos();
        RenderChips();
        Rebuild();
    }

    static TextBlock Label(string t) => new()
    {
        Text = t, FontSize = 12, VerticalAlignment = System.Windows.VerticalAlignment.Center,
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MuteBrush"],
    };

    void Hook(TextBox tb, Action<double?> set)
    {
        void Apply() { set(Format.ParseNullable(tb.Text)); _manual = null; Redraw(); }
        tb.LostFocus += (_, _) => Apply();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
    }

    /// <summary>시계열 차트 높이(220) 대비 배율</summary>
    public void SetHeightFactor(double k) => Wp.Height = 220 * k;

    /// <summary>데이터가 바뀌면 신호 목록 콤보를 다시 채운다.</summary>
    public void FillCombos()
    {
        _fillingCombos = true;
        var names = S.Data?.Names ?? new List<string>();
        _xsel.ItemsSource = names.ToList();
        if (XSignal == null || !names.Contains(XSignal)) XSignal = names.FirstOrDefault();
        _xsel.SelectedItem = XSignal;
        _ysel.ItemsSource = new[] { "+ Y 신호 추가" }.Concat(names).ToList();
        _ysel.SelectedIndex = 0;
        _fillingCombos = false;
    }

    public void ApplyConfig(XYChartConfig c)
    {
        XSignal = c.X ?? S.Data?.Names.FirstOrDefault();
        AllRange = c.All; _all.IsChecked = c.All;
        XMin = c.XMin; XMax = c.XMax; YMin = c.YMin; YMax = c.YMax;
        static string F(double? v) => v?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _xmin.Text = F(XMin); _xmax.Text = F(XMax); _ymin.Text = F(YMin); _ymax.Text = F(YMax);
        Signals.Clear(); Signals.AddRange(c.Ys);
        Styles.Clear(); foreach (var (k, v) in c.Styles) Styles[k] = v.Clone();
        FillCombos();
        OnSignalsChanged();
    }

    public XYChartConfig ToConfig() => new()
    {
        X = XSignal, Ys = Signals.ToList(), All = AllRange,
        XMin = XMin, XMax = XMax, YMin = YMin, YMax = YMax,
        Styles = Styles.ToDictionary(k => k.Key, k => k.Value.Clone()),
    };

    protected override bool IsXY => true;

    protected override string ChipValue(string s)
    {
        var d = S.Data;
        if (d == null || S.CursorA == null || XSignal == null || !d.Signals.TryGetValue(XSignal, out var xa) || !d.Signals.TryGetValue(s, out var y)) return "—";
        string F(double tm) => $"({Format.Value(Series.ValAt(d.Time, xa, tm))}, {Format.Value(Series.ValAt(d.Time, y, tm))})";
        var txt = "A " + F(S.CursorA.Value) + (S.CursorB == null ? "" : " · B " + F(S.CursorB.Value));
        var r = S.Ref;
        if (r != null && r.Signals.ContainsKey(XSignal) && r.Signals.ContainsKey(s))
        {
            string G(double tm) => $"({Format.Value(r.ValNear(XSignal, tm))}, {Format.Value(r.ValNear(s, tm))})";
            txt += "  ┃ 기준 A " + G(S.CursorA.Value) + (S.CursorB == null ? "" : " · B " + G(S.CursorB.Value));
        }
        return txt;
    }

    public override void Rebuild() { /* XY 차트는 표시 구간에 따라 매번 다시 그린다 */ }

    static MarkerShape Shape(string s) => s switch
    {
        "square" => MarkerShape.FilledSquare,
        "triangle" => MarkerShape.FilledTriangleUp,
        "diamond" => MarkerShape.FilledDiamond,
        "x" => MarkerShape.Eks,
        _ => MarkerShape.FilledCircle,
    };

    public override void Redraw()
    {
        var p = Wp.Plot;
        p.Clear();
        var d = S.Data;
        if (d == null || d.Count == 0 || XSignal == null || !d.Signals.TryGetValue(XSignal, out var xa))
        {
            Wp.Refresh(); UpdateChipValues(); return;
        }
        var t = d.Time;
        var (i0, i1) = AllRange ? (0, t.Length - 1) : Series.VisibleRange(t, S.ViewMin, S.ViewMax);
        var r = S.Ref;
        double[]? xr = r != null && r.Signals.TryGetValue(XSignal, out var xrr) ? xrr : null;
        int ri0 = 0, ri1 = -1;
        if (xr != null) (ri0, ri1) = AllRange ? (0, r!.Time.Length - 1) : Series.VisibleRange(r!.Time, S.ViewMin, S.ViewMax);

        // 자동 범위
        var (xn, xx) = Series.MinMax(xa, i0, i1);
        if (xr != null && ri1 >= ri0) { var (a, b) = Series.MinMax(xr, ri0, ri1); xn = Math.Min(xn, a); xx = Math.Max(xx, b); }
        double yn = double.PositiveInfinity, yx = double.NegativeInfinity;
        foreach (var s in Signals)
        {
            if (!d.Signals.TryGetValue(s, out var a)) continue;
            var (mn, mx) = Series.MinMax(a, i0, i1); yn = Math.Min(yn, mn); yx = Math.Max(yx, mx);
            if (xr != null && r!.Signals.TryGetValue(s, out var b) && ri1 >= ri0)
            { var (rn, rx) = Series.MinMax(b, ri0, ri1); yn = Math.Min(yn, rn); yx = Math.Max(yx, rx); }
        }
        var (x0, x1) = Series.Pad(xn, xx);
        var (y0, y1) = Series.Pad(yn, yx);
        if (XMin != null) x0 = XMin.Value; if (XMax != null) x1 = XMax.Value;
        if (YMin != null) y0 = YMin.Value; if (YMax != null) y1 = YMax.Value;
        if (!(x1 > x0)) { if (XMin != null) x1 = x0 + 1; else x0 = x1 - 1; }
        if (!(y1 > y0)) { if (YMin != null) y1 = y0 + 1; else y0 = y1 - 1; }
        if (_manual != null) (x0, x1, y0, y1) = _manual.Value;
        _last = (x0, x1, y0, y1);

        var hl = Highlight;
        foreach (var s in Signals)
        {
            if (!d.Signals.TryGetValue(s, out var ya)) continue;
            var st = StyleOf(s);
            var col = ToScott(st.Color);
            double alpha = hl == null || hl == s ? 1 : .12, width = hl == s ? st.Width * 2 : st.Width;
            if (xr != null && r!.Signals.TryGetValue(s, out var yr))
            {
                var (rx, ry) = Collect(xr, yr, ri0, ri1);
                if (rx.Length > 1)
                {
                    var sc = p.Add.ScatterLine(rx, ry, col.WithAlpha(.6 * alpha));
                    sc.LineWidth = (float)width; sc.LinePattern = LinePattern.Dashed;
                }
            }
            var (px, py) = Collect(xa, ya, i0, i1);
            if (px.Length == 0) continue;
            if (st.Mode != "marker")
            {
                var sc = p.Add.ScatterLine(px, py, col.WithAlpha(alpha));
                sc.LineWidth = (float)width; sc.LinePattern = Pattern(st.Dash);
            }
            if (st.Mode != "line")
            {
                int step = Math.Max(1, (int)Math.Ceiling(px.Length / 4000.0));
                var mx = px.Where((_, i) => i % step == 0).ToArray();
                var my = py.Where((_, i) => i % step == 0).ToArray();
                var sc = p.Add.ScatterPoints(mx, my, col.WithAlpha(alpha));
                sc.MarkerShape = Shape(st.Shape); sc.MarkerSize = (float)(st.MarkerSize * 2);
            }
        }

        // 커서 위치 마커
        void Cursor(double? tm, System.Windows.Media.Color c, MarkerShape shape, MarkerShape refShape)
        {
            if (tm == null) return;
            var col = new SColor(c.R, c.G, c.B);
            double u = Series.ValAt(t, xa, tm.Value);
            foreach (var s in Signals)
            {
                if (!d.Signals.TryGetValue(s, out var ya)) continue;
                double v = Series.ValAt(t, ya, tm.Value);
                if (!double.IsNaN(u) && !double.IsNaN(v)) p.Add.Marker(u, v, shape, 12, col);
                if (xr != null && r!.Signals.ContainsKey(s))
                {
                    double ur = r.ValNear(XSignal, tm.Value), vr = r.ValNear(s, tm.Value);
                    if (!double.IsNaN(ur) && !double.IsNaN(vr)) { var m = p.Add.Marker(ur, vr, refShape, 12, col); m.MarkerLineWidth = 2.5f; }
                }
            }
        }
        Cursor(S.CursorA, AppState.CursorAColor, MarkerShape.FilledCircle, MarkerShape.OpenCircle);
        Cursor(S.CursorB, AppState.CursorBColor, MarkerShape.FilledSquare, MarkerShape.OpenSquare);

        p.Axes.SetLimits(x0, x1, y0, y1);
        Wp.Refresh();
        UpdateChipValues();
    }

    /// <summary>인덱스 범위의 (x, y) 쌍 중 NaN이 없는 것만, 최대 30,000점으로 솎아 모은다.</summary>
    static (double[] X, double[] Y) Collect(double[] x, double[] y, int i0, int i1)
    {
        int step = Math.Max(1, (i1 - i0 + 1) / 30000);
        var xs = new List<double>(); var ys = new List<double>();
        for (int i = i0; i <= i1; i += step)
        {
            double u = x[i], v = y[i];
            if (double.IsNaN(u) || double.IsNaN(v)) continue;
            xs.Add(u); ys.Add(v);
        }
        return (xs.ToArray(), ys.ToArray());
    }

    // ---------- 마우스 ----------
    Point? _dragStart;
    (double X0, double X1, double Y0, double Y1) _dragFrom;
    bool _dragOnAxis;
    double PlotW => Math.Max(1, Wp.ActualWidth - PadL - PadR);
    double PlotH => Math.Max(1, Wp.ActualHeight - PadT - PadB);

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (S.Data == null) return;
        var pos = e.GetPosition(Wp);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { _manual = null; Redraw(); e.Handled = true; return; }
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _dragStart = pos; _dragFrom = _last; _dragOnAxis = pos.X < PadL;
            Wp.CaptureMouse(); e.Handled = true;
        }
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragStart == null) return;
        var pos = e.GetPosition(Wp);
        var (x0, x1, y0, y1) = _dragFrom;
        double dx = _dragOnAxis ? 0 : (pos.X - _dragStart.Value.X) / PlotW * (x1 - x0);
        double dy = (pos.Y - _dragStart.Value.Y) / PlotH * (y1 - y0);
        _manual = (x0 - dx, x1 - dx, y0 + dy, y1 + dy);
        Redraw();
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null) return;
        _dragStart = null; Wp.ReleaseMouseCapture(); e.Handled = true;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (S.Data == null) return;
        e.Handled = true;
        var pos = e.GetPosition(Wp);
        double k = Math.Exp(-e.Delta * 0.00125);
        var (x0, x1, y0, y1) = _last;
        double fy = Math.Clamp((PadT + PlotH - pos.Y) / PlotH, 0, 1), vy = y0 + fy * (y1 - y0), nsy = (y1 - y0) * k;
        double ny0 = vy - fy * nsy, ny1 = ny0 + nsy;
        if (pos.X < PadL) { _manual = (x0, x1, ny0, ny1); Redraw(); return; }
        double fx = Math.Clamp((pos.X - PadL) / PlotW, 0, 1), vx = x0 + fx * (x1 - x0), nsx = (x1 - x0) * k;
        double nx0 = vx - fx * nsx;
        _manual = (nx0, nx0 + nsx, ny0, ny1);
        Redraw();
    }
}
