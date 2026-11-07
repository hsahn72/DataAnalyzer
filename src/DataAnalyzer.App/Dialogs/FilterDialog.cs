using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DataAnalyzer.Core.Analysis;
using DataAnalyzer.Core.Data;
using ScottPlot;
using Orientation = System.Windows.Controls.Orientation;

namespace DataAnalyzer.App.Dialogs;

/// <summary>
/// Filter · 위상지연 없는 필터. 현재 표시 구간을 원본(회색)과 필터 결과(빨강)로 미리 보고,
/// [차트에 추가]를 누르면 '신호_MA11' / '신호_LP5Hz' 신호를 만들고 원본과 함께 차트에 넣는다.
/// </summary>
public sealed class FilterDialog : Window
{
    readonly AppState S;
    readonly Action<string, string> _addChart;
    readonly ComboBox _sig = new() { MinWidth = 260 };
    readonly ComboBox _meth = new() { MinWidth = 260 };
    readonly TextBox _n = new() { Width = 70, Text = "11" };
    readonly TextBox _fc = new() { Width = 70 };
    readonly StackPanel _nRow = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _fcRow = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _info = new() { Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    readonly TextBlock _msg = new() { Foreground = System.Windows.Media.Brushes.DimGray, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    readonly ScottPlot.WPF.WpfPlot _plot = new() { Height = 280 };
    readonly double _fs;

    /// <param name="addChart">(원본, 결과) 신호로 차트 추가</param>
    public FilterDialog(AppState state, string? preselect, Action<string, string> addChart)
    {
        S = state; _addChart = addChart;
        Title = "Filter · 위상지연 없는 필터 (원본과 비교)";
        Width = 720; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var d = S.Data!;
        _fs = ZeroPhaseFilter.SampleRate(d.TimeSeconds());
        _fc.Text = _fs > 0 ? Format.Precision(_fs / 20, 2) : "5";
        _sig.ItemsSource = d.Names.ToList();
        _sig.SelectedItem = preselect != null && d.Signals.ContainsKey(preselect) ? preselect : d.Names.FirstOrDefault();
        _meth.Items.Add("이동평균 (대칭 윈도우)");
        _meth.Items.Add("Low-pass (Butterworth 2차, 정·역방향)");
        _meth.SelectedIndex = 0;
        _plot.UserInputProcessor.Disable();

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(Row("신호 열", _sig));
        root.Children.Add(Row("방식", _meth));
        _nRow.Children.Add(Label("Sample 수")); _nRow.Children.Add(_n);
        _fcRow.Children.Add(Label("Cut-off [Hz]")); _fcRow.Children.Add(_fc);
        root.Children.Add(_nRow); root.Children.Add(_fcRow);
        root.Children.Add(_info);
        root.Children.Add(new Border { Child = _plot, BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new Thickness(1) });
        root.Children.Add(new TextBlock
        {
            Text = "미리보기는 현재 메인 차트의 표시 구간입니다. 회색 = 원본, 빨강 = 필터 결과. [차트에 추가]를 눌러야 신호·차트가 만들어집니다.",
            Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6), FontSize = 12,
        });
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = new Button { Content = "차트에 추가 (확정)", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var close = new Button { Content = "닫기", IsCancel = true };
        apply.Click += (_, _) => Apply();
        close.Click += (_, _) => Close();
        btns.Children.Add(apply); btns.Children.Add(_msg); btns.Children.Add(close);
        root.Children.Add(btns);
        Content = root;

        _sig.SelectionChanged += (_, _) => UpdatePreview();
        _meth.SelectionChanged += (_, _) => UpdatePreview();
        _n.TextChanged += (_, _) => UpdatePreview();
        _fc.TextChanged += (_, _) => UpdatePreview();
        Loaded += (_, _) => UpdatePreview();
    }

    static TextBlock Label(string t) => new() { Text = t, Width = 90, VerticalAlignment = System.Windows.VerticalAlignment.Center };
    static StackPanel Row(string label, UIElement el)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        p.Children.Add(Label(label)); p.Children.Add(el);
        return p;
    }

    FilterKind Kind => _meth.SelectedIndex == 1 ? FilterKind.LowPass : FilterKind.MovingAverage;
    double Param => Kind == FilterKind.MovingAverage
        ? (double.TryParse(_n.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 1)
        : (double.TryParse(_fc.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : double.NaN);

    double[]? Compute() =>
        _sig.SelectedItem is string s && S.Data!.Signals.TryGetValue(s, out var a) ? ZeroPhaseFilter.Apply(a, Kind, Param, _fs) : null;

    void UpdatePreview()
    {
        bool ma = Kind == FilterKind.MovingAverage;
        _nRow.Visibility = ma ? Visibility.Visible : Visibility.Collapsed;
        _fcRow.Visibility = ma ? Visibility.Collapsed : Visibility.Visible;
        string fsText = _fs > 0 ? _fs.ToString("0.00", CultureInfo.InvariantCulture) + " Hz" : "?";
        if (ma)
        {
            double raw = Param; int N = ZeroPhaseFilter.OddWindow(raw);
            _info.Text = $"샘플링 주파수 ≈ {fsText}  ·  이동평균 창 {N} 샘플"
                + (N != Math.Max(1, (int)Math.Round(raw, MidpointRounding.AwayFromZero)) ? " (짝수 → 홀수로 보정, 중심 대칭)" : "")
                + (_fs > 0 ? $" ≈ {(N / _fs).ToString("0.000", CultureInfo.InvariantCulture)} s" : "");
        }
        else
            _info.Text = $"샘플링 주파수 ≈ {fsText}  ·  Cut-off(−3 dB) {_fc.Text} Hz, Nyquist {(_fs > 0 ? (_fs / 2).ToString("0.00", CultureInfo.InvariantCulture) : "?")} Hz  ·  정·역방향 적용으로 위상지연 0";

        var p = _plot.Plot;
        p.Clear();
        var d = S.Data!;
        if (_sig.SelectedItem is not string s || !d.Signals.TryGetValue(s, out var a)) { _plot.Refresh(); return; }
        var (i0, i1) = Series.VisibleRange(d.Time, S.ViewMin, S.ViewMax);
        if (i1 - i0 < 2) { i0 = 0; i1 = d.Count - 1; }
        var orig = p.Add.SignalXY(d.Time, a, Color.FromHex("#9ca3af"));
        orig.LineWidth = 1.2f; orig.MinRenderIndex = i0; orig.MaxRenderIndex = i1;
        var y = Compute();
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        (mn, mx) = Series.MinMax(a, i0, i1);
        if (y != null)
        {
            var f = p.Add.SignalXY(d.Time, y, Color.FromHex("#ef4444"));
            f.LineWidth = 1.8f; f.MinRenderIndex = i0; f.MaxRenderIndex = i1;
            var (fn, fx) = Series.MinMax(y, i0, i1); mn = Math.Min(mn, fn); mx = Math.Max(mx, fx);
        }
        var (lo, hi) = Series.Pad(mn, mx);
        p.Axes.SetLimits(d.Time[i0], d.Time[i1], lo, hi);
        _plot.Refresh();
    }

    void Apply()
    {
        var y = Compute();
        if (y == null || _sig.SelectedItem is not string s) { _msg.Text = "계산할 수 없습니다 (열/Cut-off 확인)"; return; }
        var name = ZeroPhaseFilter.ResultName(s, Kind, Param);
        S.PutSignal(name, y, S.UnitOf(s));
        _addChart(s, name);
        _msg.Text = "추가됨: " + name;
    }
}
