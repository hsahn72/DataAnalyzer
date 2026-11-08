using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DataAnalyzer.Core.Config;
using Microsoft.Win32;

namespace DataAnalyzer.App.Dialogs;

/// <summary>
/// 차트 구성·스타일 저장/불러오기. 이름 붙인 설정은 %AppData%\DataAnalyzer\presets.json에 저장되고,
/// 파일 내보내기/불러오기는 HTML 뷰어의 JSON과 호환된다.
/// </summary>
public sealed class ConfigDialog : Window
{
    readonly PresetStore _store = new(AppStore.Safe);
    readonly Func<ChartConfig?> _get;
    readonly Func<ChartConfig, string> _apply;
    readonly TextBox _name = new() { Width = 200 };
    readonly StackPanel _list = new();
    readonly TextBlock _msg = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };

    /// <param name="get">현재 구성 (데이터가 없으면 null)</param>
    /// <param name="apply">구성 적용 → 결과 메시지</param>
    public ConfigDialog(Func<ChartConfig?> get, Func<ChartConfig, string> apply)
    {
        _get = get; _apply = apply;
        Title = "차트 구성 · 스타일 설정";
        Width = 420; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(14) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var save = new Button { Content = "저장" };
        save.Click += (_, _) => Save();
        _name.ToolTip = "설정 이름";
        row.Children.Add(_name); row.Children.Add(save);
        root.Children.Add(row);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 260, Margin = new Thickness(0, 8, 0, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var io = new StackPanel { Orientation = Orientation.Horizontal };
        var exp = new Button { Content = "파일로 내보내기" };
        var imp = new Button { Content = "파일 불러오기" };
        exp.Click += (_, _) => Export();
        imp.Click += (_, _) => Import();
        io.Children.Add(exp); io.Children.Add(imp);
        root.Children.Add(io);
        root.Children.Add(_msg);
        var close = new Button { Content = "닫기", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
        RenderList();
    }

    void RenderList()
    {
        _list.Children.Clear();
        var p = _store.Load();
        _list.Children.Add(new TextBlock { Text = "저장 위치: " + _store.Where, Foreground = Brushes.Gray, FontSize = 11 });
        if (p.Count == 0) { _list.Children.Add(new TextBlock { Text = "저장된 설정이 없습니다.", Foreground = Brushes.Gray }); return; }
        foreach (var (name, cfg) in p)
        {
            var r = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var del = new Button { Content = "삭제" };
            var ap = new Button { Content = "적용" };
            DockPanel.SetDock(del, Dock.Right); DockPanel.SetDock(ap, Dock.Right);
            r.Children.Add(del); r.Children.Add(ap);
            r.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            ap.Click += (_, _) => _msg.Text = _get() == null ? "먼저 데이터를 여세요" : _apply(cfg);
            del.Click += (_, _) => { _store.Remove(name); RenderList(); };
            _list.Children.Add(r);
        }
    }

    void Save()
    {
        var cfg = _get();
        if (cfg == null) { _msg.Text = "먼저 데이터를 여세요"; return; }
        var n = _name.Text.Trim();
        if (n.Length == 0) n = "설정 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _store.Put(n, cfg);
        _name.Text = "";
        RenderList();
        _msg.Text = "저장됨: " + n + " (" + _store.Where + ")";
    }

    void Export()
    {
        var cfg = _get();
        if (cfg == null) { _msg.Text = "먼저 데이터를 여세요"; return; }
        var dlg = new SaveFileDialog { FileName = "chart-config.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, cfg.ToJson());
        _msg.Text = "내보냄: " + dlg.FileName;
    }

    void Import()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        if (_get() == null) { _msg.Text = "먼저 데이터를 여세요"; return; }
        try { _msg.Text = _apply(ChartConfig.FromJson(File.ReadAllText(dlg.FileName))); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException) { _msg.Text = "파일을 읽을 수 없습니다: " + e.Message; }
    }
}
