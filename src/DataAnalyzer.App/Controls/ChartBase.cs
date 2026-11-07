using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DataAnalyzer.Core.Config;
using ScottPlot;
using LineStyle = DataAnalyzer.Core.Config.LineStyle;
using Orientation = System.Windows.Controls.Orientation;
using SColor = ScottPlot.Color;
using WColor = System.Windows.Media.Color;

namespace DataAnalyzer.App.Controls;

/// <summary>시계열 차트와 XY 차트의 공통 부분: 신호 목록, 선 스타일, 신호 칩, 끌어다 놓기.</summary>
public abstract class ChartBase : Border
{
    public const string ChipFormat = "DataAnalyzer.Chip";
    /// <summary>모든 차트의 차트 영역 여백 (DIP). 시계열 차트끼리 X축이 정확히 겹치도록 고정.</summary>
    public const double PadL = 62, PadR = 12, PadT = 8, PadB = 22;

    protected readonly AppState S;
    protected readonly ScottPlot.WPF.WpfPlot Wp;
    protected readonly WrapPanel Chips = new() { Margin = new Thickness(6, 2, 6, 2) };
    readonly Dictionary<string, TextBlock> _values = new();

    public List<string> Signals { get; } = new();
    public Dictionary<string, LineStyle> Styles { get; } = new();
    /// <summary>강조된 신호 (null = 없음)</summary>
    public string? Highlight { get; set; }

    public event Action<ChartBase>? Activated;
    public event Action<ChartBase>? RemoveRequested;

    protected ChartBase(AppState state)
    {
        S = state;
        Background = Brushes.White;
        BorderBrush = (Brush)Application.Current.Resources["LineBrush"];
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Margin = new Thickness(0, 0, 0, 10);
        Wp = new ScottPlot.WPF.WpfPlot();
        Wp.UserInputProcessor.Disable();
        var p = Wp.Plot;
        p.Layout.Fixed(new PixelPadding((float)PadL, (float)PadR, (float)PadB, (float)PadT));
        p.Grid.MajorLineColor = SColor.FromHex("#e9ecf1");
        p.Axes.Frame(false);
        foreach (var ax in new IAxis[] { p.Axes.Left, p.Axes.Bottom })
        {
            ax.TickLabelStyle.FontSize = 11;
            ax.TickLabelStyle.ForeColor = SColor.FromHex("#6b7586");
            ax.MajorTickStyle.Length = 0;
            ax.MinorTickStyle.Length = 0;
        }
        AllowDrop = true;
        PreviewMouseDown += (_, _) => Activated?.Invoke(this);
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    public void SetActive(bool on) => BorderBrush = on
        ? (Brush)Application.Current.Resources["AccentBrush"]
        : (Brush)Application.Current.Resources["LineBrush"];

    protected void RaiseRemove() => RemoveRequested?.Invoke(this);

    /// <summary>신호의 실제 스타일 (사용자 지정이 없으면 기본값)</summary>
    public LineStyle StyleOf(string s)
    {
        if (Styles.TryGetValue(s, out var st))
        {
            var c = st.Clone();
            c.Color ??= ToHex(S.ColorOf(s));
            return c;
        }
        return new LineStyle { Color = ToHex(S.ColorOf(s)) };
    }

    public static string ToHex(WColor c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    public static SColor ToScott(string? hex) => SColor.FromHex(string.IsNullOrEmpty(hex) ? "#000000" : hex);
    public static WColor ToWpf(string? hex) =>
        string.IsNullOrEmpty(hex) ? System.Windows.Media.Colors.Black : (WColor)ColorConverter.ConvertFromString(hex);

    public static LinePattern Pattern(string dash) => dash switch
    {
        "dash" => LinePattern.Dashed,
        "dot" => LinePattern.Dotted,
        "dashdot" => LinePattern.DenselyDashed,
        _ => LinePattern.Solid,
    };

    public void AddSignal(string s)
    {
        if (Signals.Contains(s)) return;
        Signals.Add(s);
        OnSignalsChanged();
    }

    public void RemoveSignal(string s)
    {
        if (!Signals.Remove(s)) return;
        if (Highlight == s) Highlight = null;
        OnSignalsChanged();
    }

    /// <summary>신호 목록이 바뀌었을 때: 칩과 그래프를 다시 만든다.</summary>
    public virtual void OnSignalsChanged()
    {
        RenderChips();
        Rebuild();
        Redraw();
    }

    /// <summary>데이터가 바뀌었을 때 (그래프 객체 다시 만들기)</summary>
    public abstract void Rebuild();
    /// <summary>표시 구간·커서가 바뀌었을 때</summary>
    public abstract void Redraw();
    /// <summary>칩에 표시할 값 문자열</summary>
    protected abstract string ChipValue(string s);
    /// <summary>칩 이름 (XY 차트는 'Y (X: …)' 형태로 바꿀 수 있음)</summary>
    protected virtual string ChipLabel(string s) => s;
    /// <summary>스타일 대화상자에서 XY 전용 항목(마커)을 보일지</summary>
    protected virtual bool IsXY => false;

    public void RenderChips()
    {
        Chips.Children.Clear();
        _values.Clear();
        if (Signals.Count == 0)
        {
            Chips.Children.Add(new TextBlock
            {
                Text = "신호를 이곳으로 끌어다 놓거나, 이 차트를 선택하고 목록에서 클릭하세요",
                Foreground = (Brush)Application.Current.Resources["MuteBrush"],
                FontSize = 12,
                Margin = new Thickness(2),
            });
            return;
        }
        foreach (var s in Signals) Chips.Children.Add(MakeChip(s));
        UpdateChipValues();
    }

    public void UpdateChipValues()
    {
        foreach (var (s, tb) in _values) tb.Text = ChipValue(s);
    }

    FrameworkElement MakeChip(string s)
    {
        var st = StyleOf(s);
        var col = ToWpf(st.Color);
        var name = new TextBlock { Text = ChipLabel(s), FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = System.Windows.VerticalAlignment.Center };
        var val = new TextBlock
        {
            Text = "—", FontSize = 12, MinWidth = 56, TextAlignment = TextAlignment.Right,
            FontFamily = new FontFamily("Consolas"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center,
        };
        _values[s] = val;
        var x = new Button
        {
            Content = "×", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "신호 제거",
        };
        x.Click += (_, e) => { e.Handled = true; RemoveSignal(s); };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(name); sp.Children.Add(val); sp.Children.Add(x);
        bool hl = Highlight == s;
        var chip = new Border
        {
            Child = sp,
            BorderBrush = new SolidColorBrush(col),
            BorderThickness = new Thickness(hl ? 2 : 1),
            Background = hl ? new SolidColorBrush(WColor.FromArgb(56, col.R, col.G, col.B)) : Brushes.Transparent,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 1, 4, 1),
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand,
            ToolTip = "클릭: 이 신호만 강조 / 다시 클릭: 해제 · 더블클릭: 선 스타일 편집 · 다른 차트로 끌기: 이동 (Ctrl = 복사)",
        };
        Point? down = null;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // 첫 클릭이 강조를 바꿨으므로 되돌린 뒤 스타일 편집
                e.Handled = true; down = null;
                Highlight = Highlight == s ? null : s;
                OpenStyle(s);
                return;
            }
            down = e.GetPosition(chip);
        };
        chip.MouseMove += (_, e) =>
        {
            if (down == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(chip);
            if (Math.Abs(p.X - down.Value.X) < 4 && Math.Abs(p.Y - down.Value.Y) < 4) return;
            down = null;
            var data = new DataObject();
            data.SetData(ChipFormat, new ChipDrag(this, s));
            data.SetData(DataFormats.StringFormat, s);
            DragDrop.DoDragDrop(chip, data, DragDropEffects.Move | DragDropEffects.Copy);
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            if (down == null) return;
            down = null;
            Highlight = Highlight == s ? null : s;
            RenderChips();
            Rebuild();
            Redraw();
        };
        return chip;
    }

    void OpenStyle(string s)
    {
        var dlg = new Dialogs.StyleDialog(s, StyleOf(s), IsXY) { Owner = Window.GetWindow(this) };
        dlg.StyleChanged += st =>
        {
            if (st == null) Styles.Remove(s); else Styles[s] = st;
            RenderChips();
            Rebuild();
            Redraw();
        };
        dlg.ShowDialog();
        RenderChips();
        Rebuild();
        Redraw();
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ChipFormat) is ChipDrag cd)
            e.Effects = cd.Source == this ? DragDropEffects.None
                : (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
        else if (e.Data.GetData(DataFormats.StringFormat) is string s && S.Data?.Signals.ContainsKey(s) == true)
            e.Effects = DragDropEffects.Copy;
        else return;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ChipFormat) is ChipDrag cd)
        {
            e.Handled = true;
            if (cd.Source == this) return;
            if (cd.Source.Styles.TryGetValue(cd.Signal, out var st) && !Styles.ContainsKey(cd.Signal)) Styles[cd.Signal] = st.Clone();
            AddSignal(cd.Signal);
            bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            if (!copy) cd.Source.RemoveSignal(cd.Signal);
            return;
        }
        if (e.Data.GetData(DataFormats.StringFormat) is string s && S.Data?.Signals.ContainsKey(s) == true)
        {
            e.Handled = true;
            AddSignal(s);
        }
    }

    sealed record ChipDrag(ChartBase Source, string Signal);

    /// <summary>
    /// NaN이 있는 신호를 연속 구간으로 나눈다 (NaN에서 선이 끊기도록).
    /// 구간이 너무 많으면 하나로 돌려준다.
    /// </summary>
    protected static List<(int From, int To)> Runs(double[] a, int maxRuns = 200)
    {
        var runs = new List<(int, int)>();
        int start = -1;
        for (int i = 0; i < a.Length; i++)
        {
            bool ok = !double.IsNaN(a[i]);
            if (ok && start < 0) start = i;
            else if (!ok && start >= 0) { runs.Add((start, i - 1)); start = -1; }
        }
        if (start >= 0) runs.Add((start, a.Length - 1));
        if (runs.Count > maxRuns) return new() { (0, a.Length - 1) };
        return runs;
    }
}
