using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataAnalyzer.Core.Config;
using DataAnalyzer.Core.Data;
using ScottPlot;
using LineStyle = DataAnalyzer.Core.Config.LineStyle;
using Orientation = System.Windows.Controls.Orientation;
using ScottPlot.Plottables;
using SColor = ScottPlot.Color;

namespace DataAnalyzer.App.Controls;

/// <summary>
/// 시계열 차트. X축(표시 구간)과 커서 A/B는 모든 시계열 차트가 공유한다.
/// 조작: 클릭·드래그 = 커서 이동 · 휠 = 확대/축소 · Shift+드래그 또는 오른쪽 드래그 = 이동 ·
/// 가운데 드래그 = X·Y 이동 · Y축 위 휠 = Y 확대/축소 · Y축 더블클릭 = Y 자동.
/// </summary>
public sealed class TimeChart : ChartBase
{
    readonly CheckBox _norm = new() { Content = "개별 스케일", VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), FontSize = 12 };
    readonly TextBox _ymin = new() { Width = 70, FontSize = 12, ToolTip = "비우면 자동" };
    readonly TextBox _ymax = new() { Width = 70, FontSize = 12, ToolTip = "비우면 자동" };

    public bool Normalized { get; private set; }
    public double? YMin { get; private set; }
    public double? YMax { get; private set; }

    /// <summary>마우스로 바꾼 Y 범위 (null = 자동). YMin/YMax를 바꾸면 해제된다.</summary>
    (double Lo, double Hi)? _yManual;
    (double? A, double? B) _yManualFor;
    /// <summary>마지막으로 그린 Y 범위 (일반 모드)</summary>
    (double Lo, double Hi)? _yr;

    /// <summary>신호별 그래프 객체 (본 데이터 + 기준 데이터, NaN 구간마다 하나씩)</summary>
    readonly List<(string Signal, List<SignalXY> Lines)> _lines = new();
    readonly Dictionary<string, (double Scale, double Offset)> _map = new();
    HorizontalSpan? _span;
    VerticalLine? _va, _vb;
    readonly List<IPlottable> _markers = new();

    public TimeChart(AppState state, IEnumerable<string>? signals = null) : base(state)
    {
        if (signals != null) Signals.AddRange(signals);
        var del = new Button { Content = "✕", ToolTip = "차트 삭제", Padding = new Thickness(6, 0, 6, 0) };
        del.Click += (_, _) => RaiseRemove();
        var yr = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        yr.Children.Add(new TextBlock { Text = "Y ", FontSize = 12, VerticalAlignment = System.Windows.VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MuteBrush"] });
        yr.Children.Add(_ymin);
        yr.Children.Add(new TextBlock { Text = " ~ ", VerticalAlignment = System.Windows.VerticalAlignment.Center });
        yr.Children.Add(_ymax);
        var head = new DockPanel { Margin = new Thickness(2, 4, 4, 0), LastChildFill = true };
        DockPanel.SetDock(del, Dock.Right); head.Children.Add(del);
        DockPanel.SetDock(yr, Dock.Right); head.Children.Add(yr);
        DockPanel.SetDock(_norm, Dock.Right); head.Children.Add(_norm);
        head.Children.Add(Chips);
        Wp.Height = 220;
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(Wp);
        Child = root;

        _norm.Click += (_, _) => { Normalized = _norm.IsChecked == true; Redraw(); };
        void HookY(TextBox tb, bool isMin)
        {
            void Apply()
            {
                var v = Format.ParseNullable(tb.Text);
                if (isMin) YMin = v; else YMax = v;
                Redraw();
            }
            tb.LostFocus += (_, _) => Apply();
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        }
        HookY(_ymin, true);
        HookY(_ymax, false);

        Wp.PreviewMouseDown += OnDown;
        Wp.PreviewMouseMove += OnMove;
        Wp.PreviewMouseUp += OnUp;
        Wp.PreviewMouseWheel += OnWheel;
        Wp.SizeChanged += (_, _) => Redraw();

        RenderChips();
        Rebuild();
    }

    public void ApplyConfig(TimeChartConfig c)
    {
        Normalized = c.Normalized; _norm.IsChecked = c.Normalized;
        YMin = c.YMin; YMax = c.YMax;
        _ymin.Text = c.YMin?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _ymax.Text = c.YMax?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        Signals.Clear(); Signals.AddRange(c.Signals);
        Styles.Clear(); foreach (var (k, v) in c.Styles) Styles[k] = v.Clone();
        OnSignalsChanged();
    }

    public TimeChartConfig ToConfig() => new()
    {
        Signals = Signals.ToList(),
        Normalized = Normalized,
        YMin = YMin,
        YMax = YMax,
        Styles = Styles.ToDictionary(k => k.Key, k => k.Value.Clone()),
    };

    protected override string ChipValue(string s) => S.ValueText(s);

    public override void Rebuild()
    {
        var p = Wp.Plot;
        p.Clear();
        _lines.Clear();
        _markers.Clear();
        var d = S.Data;
        if (d == null) { _span = null; _va = _vb = null; return; }
        var r = S.Ref;
        var hl = Highlight != null && Signals.Contains(Highlight) ? Highlight : null;
        var order = hl == null ? Signals.ToList() : Signals.Where(x => x != hl).Append(hl).ToList();
        foreach (var s in order)
        {
            if (!d.Signals.TryGetValue(s, out var a)) continue;
            var st = StyleOf(s);
            double width = st.Width, alpha = 1;
            if (hl != null) { if (s == hl) width *= 2; else alpha = .15; }
            var col = ToScott(st.Color);
            var lines = new List<SignalXY>();
            void Add(double[] t, double[] y, SColor c, LinePattern pat)
            {
                foreach (var (from, to) in Runs(y))
                {
                    var sx = p.Add.SignalXY(t, y, c);
                    sx.LineWidth = (float)width;
                    sx.LinePattern = pat;
                    sx.MinRenderIndex = from;
                    sx.MaxRenderIndex = to;
                    lines.Add(sx);
                }
            }
            if (r != null && r.Signals.TryGetValue(s, out var ra) && r.Time.Length > 1)
                Add(r.Time, ra, col.WithAlpha(.6 * alpha), LinePattern.Dashed);
            Add(d.Time, a, col.WithAlpha(alpha), Pattern(st.Dash));
            _lines.Add((s, lines));
        }
        _span = p.Add.HorizontalSpan(0, 0, SColor.FromHex("#1c2330").WithAlpha(.07));
        _span.LineWidth = 0;
        _span.IsVisible = false;
        _va = MakeCursorLine(AppState.CursorAColor);
        _vb = MakeCursorLine(AppState.CursorBColor);

        var isDate = d.IsDate;
        p.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => isDate ? Format.Time(v, true, S.ViewMax - S.ViewMin) : Format.Precision(v, 10),
            MinimumTickSpacing = isDate ? 90 : 50,
        };
    }

    VerticalLine MakeCursorLine(System.Windows.Media.Color c)
    {
        var col = new SColor(c.R, c.G, c.B);
        var v = Wp.Plot.Add.VerticalLine(0, 1, col, LinePattern.Dashed);
        v.IsVisible = false;
        v.LabelBackgroundColor = col;
        v.LabelFontColor = Colors.White;
        v.LabelFontSize = 11;
        v.EnableAutoscale = false;
        return v;
    }

    public override void Redraw()
    {
        var d = S.Data;
        var p = Wp.Plot;
        foreach (var m in _markers) p.Remove(m);
        _markers.Clear();
        if (d == null || d.Count == 0) { Wp.Refresh(); UpdateChipValues(); return; }

        double x0 = S.ViewMin, x1 = S.ViewMax;
        var (i0, i1) = Series.VisibleRange(d.Time, x0, x1);
        var r = S.Ref;
        int ri0 = 0, ri1 = -1;
        if (r != null && r.Time.Length > 0) (ri0, ri1) = Series.VisibleRange(r.Time, x0, x1);

        // 신호별 표시 구간 최소·최대 (기준 데이터 포함)
        var rng = new Dictionary<string, (double, double)>();
        double gmin = double.PositiveInfinity, gmax = double.NegativeInfinity;
        foreach (var (s, _) in _lines)
        {
            var (mn, mx) = Series.MinMax(d.Signals[s], i0, i1);
            if (r != null && r.Signals.TryGetValue(s, out var b) && ri1 >= ri0)
            {
                var (rn, rx) = Series.MinMax(b, ri0, ri1);
                mn = Math.Min(mn, rn); mx = Math.Max(mx, rx);
            }
            rng[s] = (mn, mx);
            gmin = Math.Min(gmin, mn); gmax = Math.Max(gmax, mx);
        }

        _map.Clear();
        double ya, yb;
        if (Normalized)
        {
            ya = 0; yb = 100;
            _yr = null;
            foreach (var (s, lines) in _lines)
            {
                var (lo, hi) = Series.Pad(rng[s].Item1, rng[s].Item2);
                double k = 100 / (hi - lo), off = -lo * k;
                _map[s] = (k, off);
                foreach (var m in lines) { m.Data.YScale = k; m.Data.YOffset = off; }
            }
            var tg = new ScottPlot.TickGenerators.NumericManual();
            for (int k = 0; k <= 4; k++) tg.AddMajor(k * 25, k * 25 + "%");
            p.Axes.Left.TickGenerator = tg;
        }
        else
        {
            (ya, yb) = Series.Pad(gmin, gmax);
            if (YMin != null) ya = YMin.Value;
            if (YMax != null) yb = YMax.Value;
            if (!(yb > ya)) { if (YMin != null) yb = ya + 1; else ya = yb - 1; }
            if (_yManual != null && _yManualFor != (YMin, YMax)) _yManual = null;
            if (_yManual != null) (ya, yb) = _yManual.Value;
            _yr = (ya, yb);
            foreach (var (s, lines) in _lines)
            {
                _map[s] = (1, 0);
                foreach (var m in lines) { m.Data.YScale = 1; m.Data.YOffset = 0; }
            }
            if (p.Axes.Left.TickGenerator is not ScottPlot.TickGenerators.NumericAutomatic)
                p.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic();
        }
        p.Axes.SetLimits(x0, x1, ya, yb);

        // 커서 A–B 구간 음영
        if (_span != null)
        {
            bool on = S.CursorA != null && S.CursorB != null;
            _span.IsVisible = on;
            if (on) { _span.X1 = Math.Min(S.CursorA!.Value, S.CursorB!.Value); _span.X2 = Math.Max(S.CursorA.Value, S.CursorB.Value); }
        }
        PlaceCursor(_va, S.CursorA, "A", AppState.CursorAColor, d, r);
        PlaceCursor(_vb, S.CursorB, "B", AppState.CursorBColor, d, r);
        Wp.Refresh();
        UpdateChipValues();
    }

    void PlaceCursor(VerticalLine? line, double? cur, string tag, System.Windows.Media.Color cc, DataSet d, ReferenceData? r)
    {
        if (line == null) return;
        bool on = cur != null && cur >= S.ViewMin && cur <= S.ViewMax;
        line.IsVisible = on;
        if (!on) return;
        double x = cur!.Value;
        line.Position = x;
        line.Text = tag + " " + S.TimeText(x);
        var p = Wp.Plot;
        foreach (var (s, _) in _lines)
        {
            var (k, off) = _map[s];
            var col = ToScott(StyleOf(s).Color);
            double v = Series.ValAt(d.Time, d.Signals[s], x);
            if (!double.IsNaN(v))
            {
                var m = p.Add.Marker(x, v * k + off, MarkerShape.FilledCircle, 7, col);
                m.MarkerLineColor = Colors.White;
                m.MarkerLineWidth = 1;
                _markers.Add(m);
            }
            if (r != null && r.Signals.ContainsKey(s))
            {
                double rv = r.ValNear(s, x);
                if (!double.IsNaN(rv))
                {
                    var m = p.Add.Marker(x, rv * k + off, MarkerShape.OpenCircle, 11, col);
                    m.MarkerLineWidth = 2;
                    _markers.Add(m);
                }
            }
        }
    }

    // ---------- 마우스 ----------

    sealed class DragState
    {
        public bool Pan, Mid, OnAxis;
        public Point Start;
        public double V0, V1;
        public (double Lo, double Hi)? Yr;
    }
    DragState? _drag;

    double PlotW => Math.Max(1, Wp.ActualWidth - PadL - PadR);
    double PlotH => Math.Max(1, Wp.ActualHeight - PadT - PadB);
    double TimeAt(double px) => S.ViewMin + (px - PadL) / PlotW * (S.ViewMax - S.ViewMin);

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (S.Data == null) return;
        var pos = e.GetPosition(Wp);
        bool onAxis = pos.X < PadL;
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2 && onAxis)
        {
            _yManual = null; Redraw(); e.Handled = true; return;
        }
        Wp.CaptureMouse();
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Middle)
            _drag = new DragState { Pan = true, Mid = true, Start = pos, V0 = S.ViewMin, V1 = S.ViewMax, Yr = _yr, OnAxis = onAxis };
        else if (e.ChangedButton == MouseButton.Right || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _drag = new DragState { Pan = true, Start = pos, V0 = S.ViewMin, V1 = S.ViewMax };
        else if (e.ChangedButton == MouseButton.Left)
        {
            _drag = new DragState();
            double t = TimeAt(pos.X);
            S.PickCursorNear(t);
            S.SetCursor(t);
        }
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == null) return;
        var pos = e.GetPosition(Wp);
        if (_drag.Pan)
        {
            if (!_drag.OnAxis)
            {
                double dx = (pos.X - _drag.Start.X) / PlotW * (_drag.V1 - _drag.V0);
                S.PanTo(_drag.V0 - dx, _drag.V1 - dx);
            }
            if (_drag.Mid && _drag.Yr != null && !Normalized)
            {
                var (lo, hi) = _drag.Yr.Value;
                double dy = (pos.Y - _drag.Start.Y) / PlotH * (hi - lo);
                SetYManual(lo + dy, hi + dy);
            }
        }
        else S.SetCursor(TimeAt(pos.X));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null) return;
        _drag = null;
        Wp.ReleaseMouseCapture();
        e.Handled = true;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (S.Data == null) return;
        e.Handled = true;
        var pos = e.GetPosition(Wp);
        double factor = Math.Exp(-e.Delta * 0.00125);
        if (pos.X < PadL)
        {
            if (_yr == null || Normalized) return;
            var (a, b) = _yr.Value;
            double f = Math.Clamp((PadT + PlotH - pos.Y) / PlotH, 0, 1), v = a + f * (b - a), ns = (b - a) * factor;
            SetYManual(v - f * ns, v - f * ns + ns);
            return;
        }
        double span = S.ViewMax - S.ViewMin;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            double d = -e.Delta / 120.0 * 0.1 * span;
            S.PanTo(S.ViewMin + d, S.ViewMax + d);
            return;
        }
        double frac = (pos.X - PadL) / PlotW;
        S.ZoomAt(S.ViewMin + frac * span, frac, factor);
    }

    void SetYManual(double lo, double hi)
    {
        _yManual = (lo, hi);
        _yManualFor = (YMin, YMax);
        Redraw();
    }
}
