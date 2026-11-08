using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DataAnalyzer.App.Analysis;

/// <summary>코드로 만드는 분석 창용 작은 UI 도우미</summary>
static class Ui
{
    public static readonly Brush Mute = new SolidColorBrush(Color.FromRgb(0x6B, 0x75, 0x86));
    public static readonly Brush Err = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));

    public static TextBlock Hint(string t) => new() { Text = t, Foreground = Mute, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 3) };
    public static TextBlock Bold(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 6, 2) };
    public static TextBlock Text(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
    public static TextBlock Msg() => new() { Foreground = Mute, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 4) };

    public static void SetMsg(TextBlock b, string t, bool err = false) { b.Text = t; b.Foreground = err ? Err : Mute; }

    /// <summary>가로로 늘어놓고 넘치면 줄바꿈</summary>
    public static WrapPanel Row(params UIElement[] c)
    {
        var p = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var e in c) { if (e is FrameworkElement f && f.Margin == default) f.Margin = new Thickness(0, 1, 6, 1); p.Children.Add(e); }
        return p;
    }

    public static StackPanel Labeled(string label, UIElement e)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 10, 1) };
        p.Children.Add(Text(label)); p.Children.Add(e);
        return p;
    }

    public static Button Btn(string t, Action click, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = t, ToolTip = tip };
        if (primary) b.Style = (Style)Application.Current.Resources["PrimaryButton"];
        b.Click += (_, _) => click();
        return b;
    }

    public static string F(double v) => double.IsFinite(v) ? v.ToString("G6", CultureInfo.InvariantCulture) : "";
    public static double P(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>숫자 입력 칸 (빈칸 = NaN). 포커스를 잃거나 Enter면 set 호출.</summary>
    public static TextBox Num(double v, Action<double> set, double width = 70)
    {
        var t = new TextBox { Width = width, Text = F(v) };
        void Apply() => set(P(t.Text));
        t.LostFocus += (_, _) => Apply();
        t.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(); };
        return t;
    }

    public static CheckBox Check(string t, bool v, Action<bool> set, string? tip = null)
    {
        var c = new CheckBox { Content = t, IsChecked = v, VerticalAlignment = VerticalAlignment.Center, ToolTip = tip, Margin = new Thickness(0, 1, 10, 1) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    /// <summary>(값, 표시) 목록 콤보</summary>
    public static ComboBox Combo(IEnumerable<(string V, string T)> items, string value, Action<string> set, double minWidth = 0)
    {
        var c = new ComboBox { MinWidth = minWidth, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (v, t) in items) c.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value) ?? (c.Items.Count > 0 ? c.Items[0] : null);
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem i) set((string)i.Tag); };
        return c;
    }

    /// <summary>열 선택 콤보: (없음), 후보(★), 나머지 열 [단위]</summary>
    public static ComboBox Columns(DataAnalyzer.Core.Data.DataSet d, string value, Action<string> set, IEnumerable<string>? cand = null, double minWidth = 150)
    {
        var c = (cand ?? Array.Empty<string>()).ToList();
        string Lb(string n) => n + (d.Units.TryGetValue(n, out var u) && u != "" ? "  [" + u + "]" : "");
        var items = new List<(string, string)> { ("", "(없음)") };
        items.AddRange(c.Select(n => (n, "★ " + Lb(n))));
        items.AddRange(d.Names.Where(n => !c.Contains(n)).Select(n => (n, Lb(n))));
        if (value != "" && !items.Any(i => i.Item1 == value)) items.Add((value, value + " (없음)"));
        return Combo(items, value, set, minWidth);
    }

    public static Expander Section(string header, UIElement content, bool open = false) =>
        new() { Header = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold }, Content = new Border { Padding = new Thickness(8, 4, 4, 8), Child = content }, IsExpanded = open, Margin = new Thickness(0, 2, 0, 2) };

    /// <summary>헤더 + 행으로 된 간단한 표</summary>
    public static Grid Table(string[] header, IEnumerable<UIElement[]> rows)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        for (int c = 0; c < header.Length; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < header.Length; c++)
        {
            var h = new TextBlock { Text = header[c], FontWeight = FontWeights.SemiBold, Foreground = Mute, FontSize = 11.5, Margin = new Thickness(0, 0, 8, 2) };
            Grid.SetColumn(h, c); g.Children.Add(h);
        }
        int r = 1;
        foreach (var row in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < row.Length && c < header.Length; c++)
            {
                var e = row[c];
                if (e is FrameworkElement f) f.Margin = new Thickness(0, 1, 8, 1);
                Grid.SetRow(e, r); Grid.SetColumn(e, c); g.Children.Add(e);
            }
            r++;
        }
        return g;
    }

    public static string AppDataFile(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataAnalyzer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }
}
