using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DataAnalyzer.App.Controls;
using DataAnalyzer.App.Analysis;
using DataAnalyzer.App.Dialogs;
using DataAnalyzer.Core.Config;
using DataAnalyzer.Core.Data;
using Microsoft.Win32;

namespace DataAnalyzer.App;

public partial class MainWindow : Window
{
    readonly AppState S = new();
    readonly List<TimeChart> _charts = new();
    readonly List<XYChart> _xy = new();
    ChartBase? _active;
    bool _sortAZ;
    bool _fillingTime;
    bool _fillingCycle;
    /// <summary>Cycle·Action 분석 설정 (데이터가 바뀌면 신호 이름으로 다시 추측)</summary>
    DataAnalyzer.Core.Analysis.CycleConfig? _cycleConfig;
    DataSet? _cycleConfigFor;

    public MainWindow()
    {
        InitializeComponent();
        S.Changed += OnViewChanged;
        S.DataChanged += OnDataChanged;
        PreviewKeyDown += OnKey;
        Drop += OnFileDrop;
        DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        };
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += (_, _) => LoadCsv(args[1]);
    }

    // ---------- 데이터 열기 ----------

    void OpenCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|모든 파일|*.*" };
        if (dlg.ShowDialog(this) == true) LoadCsv(dlg.FileName);
    }

    void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;
        LoadCsv(files[0]);
    }

    void LoadCsv(string path)
    {
        RawTable raw;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            raw = CsvParser.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "CSV를 읽을 수 없습니다.\n" + ex.Message, "CSV 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally { Mouse.OverrideCursor = null; }
        SetData(raw, System.IO.Path.GetFileName(path));
    }

    void Sample_Click(object sender, RoutedEventArgs e) => SetData(SampleSignals.Create(), "sample.csv");

    void SetData(RawTable raw, string name)
    {
        var keep = KeepConfigBox.IsChecked == true && _charts.Count > 0 ? GetConfig() : null;
        S.Load(raw, name, 0);
        FileNameText.Text = name;
        _fillingTime = true;
        TimeCombo.ItemsSource = raw.Names.ToList();
        TimeCombo.SelectedIndex = 0;
        _fillingTime = false;
        ResetCharts(keep);
    }

    void TimeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingTime || TimeCombo.SelectedIndex < 0 || S.Raw == null) return;
        var keep = KeepConfigBox.IsChecked == true && _charts.Count > 0 ? GetConfig() : null;
        S.ApplyTimeColumn(TimeCombo.SelectedIndex);
        ResetCharts(keep);
    }

    /// <summary>새 데이터: 기본 차트 구성(신호 1–2, 3–4, XY 1→2) 또는 유지할 구성으로 다시 만든다.</summary>
    void ResetCharts(ChartConfig? keep)
    {
        RemoveAllCharts();
        var d = S.Data;
        if (d == null) return;
        if (keep != null) ApplyConfig(keep);
        else
        {
            var sn = d.Names;
            AddChart(sn.Take(2));
            if (sn.Count > 2) AddChart(sn.Skip(2).Take(2));
            if (sn.Count > 1) AddXY(sn[0], new[] { sn[1] });
        }
        UpdateRefUI();
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (S.Data == null) return;
        if (MessageBox.Show(this, "불러온 데이터와 차트를 모두 지울까요?", "Clear", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RemoveAllCharts();
        S.Clear();
        FileNameText.Text = "";
        _fillingTime = true; TimeCombo.ItemsSource = null; _fillingTime = false;
        UpdateRefUI();
    }

    // ---------- 기준 CSV ----------

    void OpenRef_Click(object sender, RoutedEventArgs e)
    {
        if (S.Data == null) { RefInfo.Text = "먼저 현재 CSV를 여세요"; RefInfo.Foreground = Brushes.Red; return; }
        var dlg = new OpenFileDialog { Filter = "CSV (*.csv;*.txt;*.tsv)|*.csv;*.txt;*.tsv|모든 파일|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        string err;
        try { err = S.LoadReference(CsvParser.ParseFile(dlg.FileName), System.IO.Path.GetFileName(dlg.FileName)); }
        catch (Exception ex) when (ex is IOException or FormatException) { err = "기준 CSV를 읽을 수 없습니다"; }
        UpdateRefUI(err);
    }

    void UpdateRefUI(string error = "")
    {
        var r = S.Reference;
        RefPanel.Visibility = r != null ? Visibility.Visible : Visibility.Collapsed;
        if (error.Length > 0 || S.ReferenceMessage.Length > 0)
        {
            RefInfo.Text = error.Length > 0 ? error : S.ReferenceMessage;
            RefInfo.Foreground = Brushes.Red;
            S.ReferenceMessage = "";
            return;
        }
        RefInfo.Foreground = (Brush)FindResource("MuteBrush");
        RefInfo.Text = r == null ? "" : $"기준: {r.FileName} ({r.Time.Length}행) · 점선 = 기준 Data";
        RefOffsetBox.Text = r == null ? "0" : r.Offset.ToString(CultureInfo.InvariantCulture);
    }

    void RefShow_Click(object sender, RoutedEventArgs e) => S.SetShowReference(RefShowBox.IsChecked == true);
    void RefAlign_Click(object sender, RoutedEventArgs e) { S.AlignReference(); UpdateRefUI(); }
    void RefClear_Click(object sender, RoutedEventArgs e) { S.ClearReference(); UpdateRefUI(); }
    void RefOffset_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyRefOffset(); }
    void RefOffset_LostFocus(object sender, RoutedEventArgs e) => ApplyRefOffset();
    void ApplyRefOffset()
    {
        if (S.Reference == null) return;
        var v = Format.ParseNullable(RefOffsetBox.Text) ?? 0;
        if (v != S.Reference.Offset) S.SetReferenceOffset(v);
    }

    // ---------- 상태 변경 ----------

    void OnDataChanged()
    {
        BuildSignalList();
        foreach (var x in _xy) x.FillCombos();
        foreach (var c in AllCharts()) { c.RenderChips(); c.Rebuild(); }
        EmptyText.Visibility = S.Data == null ? Visibility.Visible : Visibility.Collapsed;
        FillCycleCombos();
    }

    // ---------- Cycle / Action 거르기 ----------

    void FillCycleCombos()
    {
        bool hasC = S.CycleColumn != null, hasA = S.ActionColumn != null;
        CyclePanel.Visibility = hasC || hasA ? Visibility.Visible : Visibility.Collapsed;
        _fillingCycle = true;
        var cItems = new List<ComboBoxItem> { new() { Content = "All Cycles", Tag = null } };
        cItems.AddRange(S.CycleValues.Select(v => new ComboBoxItem { Content = "Cycle " + v, Tag = v }));
        CycleCombo.ItemsSource = cItems;
        CycleCombo.SelectedItem = cItems.FirstOrDefault(i => (int?)i.Tag == S.CycleSelection) ?? cItems[0];
        var aItems = new List<ComboBoxItem> { new() { Content = "All Actions", Tag = null } };
        aItems.AddRange(S.ActionValues.Select(v => new ComboBoxItem { Content = S.ActionLabel(v), Tag = v }));
        ActionCombo.ItemsSource = aItems;
        ActionCombo.SelectedItem = aItems.FirstOrDefault(i => (int?)i.Tag == S.ActionSelection) ?? aItems[0];
        CycleCombo.IsEnabled = hasC; ActionCombo.IsEnabled = hasA;
        OverlayBox.IsEnabled = hasC; OverlayModeCombo.IsEnabled = hasC;
        OverlayBox.IsChecked = S.OverlayChecked;
        _fillingCycle = false;
    }

    void CycleFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingCycle || S.Data == null) return;
        S.SetCycleSelection((CycleCombo.SelectedItem as ComboBoxItem)?.Tag as int?,
            (ActionCombo.SelectedItem as ComboBoxItem)?.Tag as int?, OverlayBox.IsChecked == true);
    }

    void OverlayMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (S == null || OverlayModeCombo.SelectedItem is not ComboBoxItem it) return;
        S.SetOverlayMode((string)it.Tag);
    }

    void CycleAnalysis_Click(object sender, RoutedEventArgs e)
    {
        var d = S.Data;
        if (d == null) { MessageBox.Show(this, "CSV를 먼저 여세요", "Cycle·Action 분석"); return; }
        if (_cycleConfig == null || _cycleConfigFor != d)
        {
            _cycleConfig = new DataAnalyzer.Core.Analysis.CycleConfig();
            DataAnalyzer.Core.Analysis.CycleAnalysis.GuessAxes(_cycleConfig, d.Names);
            _cycleConfigFor = d;
        }
        new CycleDialog(S, _cycleConfig, () =>
        {
            if (!_charts.Any(c => c.Signals.Contains("Cycle"))) AddChart(new[] { "Cycle", "Action" });
        }) { Owner = this }.Show();
    }

    void OnViewChanged()
    {
        foreach (var c in AllCharts()) c.Redraw();
        Readout.Text = S.CursorReadout();
        CursorBButton.IsChecked = S.CursorB != null;
        if (S.Data == null) return;
        if (!XMinBox.IsKeyboardFocused) XMinBox.Text = S.TimeText(S.ViewMin);
        if (!XMaxBox.IsKeyboardFocused) XMaxBox.Text = S.TimeText(S.ViewMax);
    }

    IEnumerable<ChartBase> AllCharts() => _charts.Cast<ChartBase>().Concat(_xy);

    // ---------- 차트 추가/삭제 ----------

    void AddChart_Click(object sender, RoutedEventArgs e) { if (S.Data != null) AddChart(); }
    void AddXY_Click(object sender, RoutedEventArgs e) { if (S.Data != null) AddXY(S.Data.Names.FirstOrDefault(), Array.Empty<string>()); }

    TimeChart AddChart(IEnumerable<string>? sigs = null)
    {
        var c = new TimeChart(S, sigs);
        Hook(c);
        _charts.Add(c);
        ChartPanel.Children.Add(c);
        SetActive(c);
        S.Notify();
        return c;
    }

    XYChart AddXY(string? x, IEnumerable<string> ys)
    {
        var c = new XYChart(S, x, ys);
        c.SetHeightFactor(XYHeight());
        Hook(c);
        _xy.Add(c);
        XYPanel.Children.Add(c);
        S.Notify();
        return c;
    }

    void Hook(ChartBase c)
    {
        c.Activated += SetActive;
        c.RemoveRequested += ch =>
        {
            if (ch is TimeChart t) { _charts.Remove(t); ChartPanel.Children.Remove(t); }
            if (ch is XYChart x) { _xy.Remove(x); XYPanel.Children.Remove(x); }
            if (_active == ch) SetActive(_charts.LastOrDefault());
        };
    }

    void SetActive(ChartBase? c)
    {
        if (c is not TimeChart && c != null) return; // 신호 목록 클릭은 시계열 차트에 추가
        _active = c;
        foreach (var t in _charts) t.SetActive(t == c);
    }

    void RemoveAllCharts()
    {
        _charts.Clear(); _xy.Clear();
        ChartPanel.Children.Clear(); XYPanel.Children.Clear();
        _active = null;
    }

    double XYHeight() =>
        XYHeightCombo?.SelectedItem is ComboBoxItem it && double.TryParse((string)it.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var k) ? k : 2;

    void XYHeight_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_xy == null) return;
        foreach (var x in _xy) x.SetHeightFactor(XYHeight());
    }

    // ---------- 신호 목록 ----------

    void BuildSignalList()
    {
        SignalList.Items.Clear();
        var d = S.Data;
        if (d == null) return;
        var q = FilterBox.Text.Trim().ToLowerInvariant();
        var names = d.Names.Where(n => n.ToLowerInvariant().Contains(q)).ToList();
        if (_sortAZ) names.Sort(NaturalCompare);
        foreach (var n in names)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(S.ColorOf(n)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            var u = S.UnitOf(n);
            sp.Children.Add(new TextBlock { Text = u.Length > 0 && !n.Contains('[') ? $"{n} [{u}]" : n, TextWrapping = TextWrapping.Wrap });
            SignalList.Items.Add(new ListBoxItem { Content = sp, Tag = n, Cursor = Cursors.Hand, Padding = new Thickness(4, 3, 4, 3) });
        }
    }

    static int NaturalCompare(string a, string b)
    {
        // 숫자 부분은 숫자로 비교 (Sig2 < Sig10), 대소문자 무시
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = decimal.Parse(a[si..i]); var nb = decimal.Parse(b[sj..j]);
                if (na != nb) return na.CompareTo(nb);
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => BuildSignalList();

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        _sortAZ = !_sortAZ;
        SortButton.Content = _sortAZ ? "원래 순서" : "A→Z";
        BuildSignalList();
    }

    Point? _listDown;
    string? _listSignal;

    void SignalList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _listDown = e.GetPosition(SignalList);
        _listSignal = (ItemsControl.ContainerFromElement(SignalList, (DependencyObject)e.OriginalSource) as ListBoxItem)?.Tag as string;
    }

    void SignalList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_listDown == null || _listSignal == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(SignalList);
        if (Math.Abs(p.X - _listDown.Value.X) < 5 && Math.Abs(p.Y - _listDown.Value.Y) < 5) return;
        var s = _listSignal;
        _listDown = null; _listSignal = null;
        DragDrop.DoDragDrop(SignalList, new DataObject(DataFormats.StringFormat, s), DragDropEffects.Copy);
    }

    void SignalList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_listDown != null && _listSignal != null && _active != null) _active.AddSignal(_listSignal);
        _listDown = null; _listSignal = null;
    }

    // ---------- 분석 ----------

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (S.Data == null) { MessageBox.Show(this, "CSV를 먼저 여세요", "Filter"); return; }
        var pre = (_active as TimeChart)?.Signals.FirstOrDefault();
        new FilterDialog(S, pre, (src, result) =>
        {
            // 결과가 이미 어느 차트에 있으면 새 차트를 만들지 않는다 (같은 설정으로 다시 누른 경우)
            if (!_charts.Any(c => c.Signals.Contains(result))) AddChart(new[] { src, result });
        }) { Owner = this }.ShowDialog();
    }

    // ---------- 굴착기 · 휠로더 분석 창 ----------

    ExcavatorWindow? _exWin;

    void Excavator_Click(object sender, RoutedEventArgs e)
    {
        if (_exWin == null || !_exWin.IsLoaded)
        {
            _exWin = new ExcavatorWindow(S, ShowResult) { Owner = this };
            _exWin.Show();
        }
        else _exWin.Activate();
    }

    /// <summary>계산 결과의 신호를 추가하고, 결과가 제안한 차트 중 아직 없는 것을 만든다.</summary>
    void Db_Click(object sender, RoutedEventArgs e) => new Dialogs.DbDialog { Owner = this }.ShowDialog();

    WheelLoaderWindow? _wlWin;

    void WheelLoader_Click(object sender, RoutedEventArgs e)
    {
        if (_wlWin == null || !_wlWin.IsLoaded)
        {
            _wlWin = new WheelLoaderWindow(S, ShowResult) { Owner = this };
            _wlWin.Show();
        }
        else _wlWin.Activate();
    }

    public void ShowResult(DataAnalyzer.Core.Excavator.CalcResult r)
    {
        if (S.Data == null || r.Outputs.Count == 0) return;
        S.PutSignals(r.Outputs.Select(o => (o.Name, o.Values, o.Unit)));
        foreach (var sigs in r.Charts)
        {
            var have = sigs.Where(x => S.Data.Signals.ContainsKey(x)).ToArray();
            if (have.Length == 0) continue;
            var c = _charts.FirstOrDefault(c => have.All(c.Signals.Contains));
            if (c == null) AddChart(have);
        }
        foreach (var g in r.XyCharts.GroupBy(x => x.X))
        {
            var ys = g.Select(x => x.Y).Where(y => S.Data.Signals.ContainsKey(y)).ToArray();
            if (!S.Data.Signals.ContainsKey(g.Key) || ys.Length == 0) continue;
            if (!_xy.Any(x => x.XSignal == g.Key && ys.All(x.Signals.Contains))) AddXY(g.Key, ys);
        }
    }

    // ---------- 표시 구간·커서 ----------

    void ResetView_Click(object sender, RoutedEventArgs e) => S.ResetView();
    void CursorB_Click(object sender, RoutedEventArgs e) => S.ToggleB();
    void XRange_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyXRange(); }
    void XRange_LostFocus(object sender, RoutedEventArgs e) => ApplyXRange();

    void ApplyXRange()
    {
        var d = S.Data;
        if (d == null) return;
        S.SetView(Format.ParseTime(XMinBox.Text, d.IsDate), Format.ParseTime(XMaxBox.Text, d.IsDate));
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (S.Data == null || e.Key is not (Key.Left or Key.Right)) return;
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem) return;
        e.Handled = true;
        S.StepCursor(e.Key == Key.Right ? 1 : -1, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    // ---------- 구성 저장/불러오기 ----------

    ChartConfig? GetConfig() => S.Data == null ? null : new ChartConfig
    {
        Charts = _charts.Select(c => c.ToConfig()).ToList(),
        XY = _xy.Select(c => c.ToConfig()).ToList(),
    };

    string ApplyConfig(ChartConfig cfg)
    {
        var d = S.Data;
        if (d == null) return "먼저 데이터를 여세요";
        var m = cfg.MapTo(d.Names, out var missing);
        RemoveAllCharts();
        foreach (var c in m.Charts!) AddChart().ApplyConfig(c);
        foreach (var x in m.XY!) AddXY(x.X, Array.Empty<string>()).ApplyConfig(x);
        S.Notify();
        return missing > 0 ? $"적용 완료 (데이터에 없는 신호 {missing}개 제외)" : "적용 완료";
    }

    void Config_Click(object sender, RoutedEventArgs e) =>
        new ConfigDialog(GetConfig, ApplyConfig) { Owner = this }.ShowDialog();

    // ---------- PNG ----------

    void SavePng_Click(object sender, RoutedEventArgs e)
    {
        if (S.Data == null || _charts.Count == 0) return;
        var dlg = new SaveFileDialog { FileName = "charts.png", Filter = "PNG (*.png)|*.png" };
        if (dlg.ShowDialog(this) != true) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var parts = new List<FrameworkElement> { ChartPanel };
        if (_xy.Count > 0) parts.Add(XYPanel);
        var header = new FormattedText(S.FileName + "   " + S.CursorReadout(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, Brushes.Black, dpi.PixelsPerDip);
        double w = parts.Max(p => p.ActualWidth), h = 24 + parts.Sum(p => p.ActualHeight);
        var dv = new DrawingVisual();
        using (var g = dv.RenderOpen())
        {
            g.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            g.DrawText(header, new Point(10, 4));
            double y = 24;
            foreach (var p in parts)
            {
                g.DrawRectangle(new VisualBrush(p) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, y, p.ActualWidth, p.ActualHeight));
                y += p.ActualHeight;
            }
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(dlg.FileName);
        enc.Save(fs);
    }
}
