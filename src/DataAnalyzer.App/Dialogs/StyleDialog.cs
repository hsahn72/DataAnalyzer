using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataAnalyzer.App.Controls;
using DataAnalyzer.Core.Config;

namespace DataAnalyzer.App.Dialogs;

/// <summary>선 스타일 편집 (값을 바꾸면 바로 적용). '기본값'은 사용자 지정을 지운다.</summary>
public sealed class StyleDialog : Window
{
    /// <summary>새 스타일 (null = 기본값으로 되돌림)</summary>
    public event Action<LineStyle?>? StyleChanged;

    readonly LineStyle _st;
    readonly TextBox _hex = new() { Width = 90 };
    readonly Border _swatch = new() { Width = 22, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 0, 0) };
    readonly TextBox _width = new() { Width = 60 };
    readonly ComboBox _dash = Combo(("solid", "실선"), ("dash", "파선"), ("dot", "점선"), ("dashdot", "일점쇄선"));
    readonly ComboBox _mode = Combo(("line", "선"), ("both", "선 + 마커"), ("marker", "마커만"));
    readonly ComboBox _shape = Combo(("circle", "●"), ("square", "■"), ("triangle", "▲"), ("diamond", "◆"), ("x", "✕"));
    readonly TextBox _msz = new() { Width = 60 };
    bool _loading;

    static ComboBox Combo(params (string Key, string Text)[] items)
    {
        var c = new ComboBox { MinWidth = 110 };
        foreach (var (k, t) in items) c.Items.Add(new ComboBoxItem { Tag = k, Content = t });
        return c;
    }
    static void Select(ComboBox c, string key) =>
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == key) ?? c.Items[0];
    static string Key(ComboBox c) => (string)((ComboBoxItem)c.SelectedItem).Tag;

    public StyleDialog(string signal, LineStyle current, bool isXY)
    {
        _st = current.Clone();
        Title = "선 스타일 · " + signal;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var g = new Grid { Margin = new Thickness(14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int row = 0;
        void Row(string label, UIElement el)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            Grid.SetRow(l, row); g.Children.Add(l);
            if (el is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            Grid.SetRow(el, row); Grid.SetColumn(el, 1); g.Children.Add(el);
            row++;
        }
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal };
        colorRow.Children.Add(_hex); colorRow.Children.Add(_swatch);
        var pal = new WrapPanel { Width = 230 };
        foreach (var c in AppState.Palette.Concat(new[] { Colors.Black, Colors.Gray }))
        {
            var b = new Button { Width = 20, Height = 20, Margin = new Thickness(1), Background = new SolidColorBrush(c), Padding = new Thickness(0) };
            b.Click += (_, _) => { _hex.Text = ChartBase.ToHex(c); };
            pal.Children.Add(b);
        }
        Row("색상", colorRow);
        Row("", pal);
        Row("두께", _width);
        Row("스타일", _dash);
        if (isXY) { Row("표시", _mode); Row("마커", _shape); Row("마커 크기", _msz); }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var reset = new Button { Content = "기본값" };
        var close = new Button { Content = "닫기", IsCancel = true, IsDefault = true };
        reset.Click += (_, _) => { StyleChanged?.Invoke(null); Close(); };
        close.Click += (_, _) => Close();
        btns.Children.Add(reset); btns.Children.Add(close);
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(btns, row); Grid.SetColumnSpan(btns, 2); g.Children.Add(btns);
        Content = g;

        _loading = true;
        _hex.Text = _st.Color ?? "#000000";
        _width.Text = _st.Width.ToString(CultureInfo.InvariantCulture);
        Select(_dash, _st.Dash); Select(_mode, _st.Mode); Select(_shape, _st.Shape);
        _msz.Text = _st.MarkerSize.ToString(CultureInfo.InvariantCulture);
        UpdateSwatch();
        _loading = false;

        _hex.TextChanged += (_, _) => Apply();
        _width.TextChanged += (_, _) => Apply();
        _msz.TextChanged += (_, _) => Apply();
        foreach (var c in new[] { _dash, _mode, _shape }) c.SelectionChanged += (_, _) => Apply();
    }

    void UpdateSwatch()
    {
        try { _swatch.Background = new SolidColorBrush(ChartBase.ToWpf(_hex.Text.Trim())); } catch (FormatException) { }
    }

    void Apply()
    {
        if (_loading) return;
        var hex = _hex.Text.Trim();
        try { ChartBase.ToWpf(hex); } catch (FormatException) { return; }
        UpdateSwatch();
        _st.Color = hex;
        if (double.TryParse(_width.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) _st.Width = Math.Clamp(w, .5, 10);
        if (double.TryParse(_msz.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) _st.MarkerSize = Math.Clamp(m, 1, 20);
        _st.Dash = Key(_dash); _st.Mode = Key(_mode); _st.Shape = Key(_shape);
        StyleChanged?.Invoke(_st.Clone());
    }
}
