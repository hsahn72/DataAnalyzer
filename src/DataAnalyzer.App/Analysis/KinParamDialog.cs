using System.Windows;
using System.Windows.Controls;
using DataAnalyzer.Core.Excavator;
using Microsoft.Win32;

namespace DataAnalyzer.App.Analysis;

/// <summary>굴착기 Kinematic 파라미터 편집 (HTML kdlg). [적용]을 누르면 검사 후 onApply로 넘긴다.</summary>
public sealed class KinParamDialog : Window
{
    readonly Func<KinParams, string?> _onApply;
    readonly Dictionary<string, TextBox> _num = new();
    readonly Dictionary<string, ComboBox> _txt = new();
    readonly Dictionary<string, ComboBox> _role = new();
    readonly Dictionary<string, TextBlock> _roleText = new();
    readonly TextBox _name = new() { MinWidth = 260 }, _desc = new() { MinWidth = 360 };
    readonly TextBlock _msg = Ui.Msg();
    KinParams _base;

    static readonly (string Key, string Def)[] Signs = { ("sB", "1"), ("sA", "1"), ("sJ", "1"), ("sP", "1") };

    /// <param name="onApply">적용. 오류 메시지를 돌려주면 창을 닫지 않는다.</param>
    public KinParamDialog(string title, KinParams k, Func<KinParams, string?> onApply)
    {
        _onApply = onApply; _base = k.Clone();
        Title = title; Width = 760; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Margin = new Thickness(12) };

        p.Children.Add(Ui.Bold("기본"));
        p.Children.Add(Ui.Row(Ui.Labeled("Machine Name", _name), Ui.Labeled("길이·좌표 단위", Sel("unit", "m", ("m", "m"), ("mm", "mm")))));
        p.Children.Add(Ui.Row(Ui.Labeled("Description", _desc)));

        p.Children.Add(Ui.Bold("링크 길이 · 높이"));
        p.Children.Add(Grid2(new[] { ("BPH", "BoomPivotHeight"), ("L1", "Swing Link Length (L1)"), ("L2", "Boom Link Length (L2)"), ("L3", "Arm Link Length (L3)"),
            ("L4", "Bucket Link Length (L4)"), ("Lgl", "Guide Link Length (Lgl)"), ("Lpl", "Push Link Length (Lpl)") }));

        p.Children.Add(Ui.Bold("핀 좌표 (x, y)"));
        p.Children.Add(Ui.Table(new[] { "점", "x", "y", "현재 역할" }, Kinematics.Points.Select(pt =>
        {
            var rt = new TextBlock { Foreground = Ui.Mute, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }; _roleText[pt] = rt;
            return new UIElement[] { Ui.Text(pt), N(pt + "x"), N(pt + "y"), rt };
        })));
        p.Children.Add(Ui.Hint("붐 실린더 P20(선회체)–P21(붐), 암 실린더 P30(붐)–P31(암), 버켓 실린더 P40(암 쪽 베이스), 푸시 링크 버켓측 연결점 P41, 가이드 링크 암측 고정점 PGL로 가정했습니다. 각 점은 해당 링크 좌표계(핀 기준, x축=링크 방향) 값입니다. 다르면 아래 \"고급\"에서 역할을 바꾸세요."));

        p.Children.Add(Ui.Bold("질량 [kg]"));
        p.Children.Add(Grid2(new[] { ("M1", "Swing Body Mass (M1)"), ("M2", "Boom Link Mass (M2)"), ("M3", "Arm Link Mass (M3)"), ("M4", "Bucket Link Mass (M4)") }));

        p.Children.Add(Ui.Bold("관성모멘트 [kg·m²]"));
        p.Children.Add(Ui.Table(new[] { "", "Ixx", "Iyy", "Izz" }, new[] { ("1", "Swing Body"), ("2", "Boom Link"), ("3", "Arm Link"), ("4", "Bucket Link") }
            .Select(x => new UIElement[] { Ui.Text(x.Item2), N("I" + x.Item1 + "xx"), N("I" + x.Item1 + "yy"), N("I" + x.Item1 + "zz") })));
        p.Children.Add(Ui.Hint("질량·관성모멘트는 링크 각도 계산에는 쓰지 않고 동역학(Tip 외력)에만 씁니다. 링크 평면 회전축은 z축(Izz)으로 봅니다."));

        p.Children.Add(Ui.Bold("링크 무게중심 (링크 좌표계: 원점 = 링크 시작 핀, x = 링크 방향. 비우면 링크 중간)"));
        p.Children.Add(Ui.Table(new[] { "", "x", "y", "z" }, new[]
        {
            new UIElement[] { Ui.Text("선회체 (X 전방, Z 상방, 원점=선회축 중심)"), N("C1x"), N("C1y"), N("C1z") },
            new UIElement[] { Ui.Text("붐"), N("C2x"), N("C2y") },
            new UIElement[] { Ui.Text("암"), N("C3x"), N("C3y") },
            new UIElement[] { Ui.Text("버켓"), N("C4x"), N("C4y") },
        }));

        p.Children.Add(Ui.Bold("실린더 (길이·직경 단위 = 위의 단위)"));
        p.Children.Add(Ui.Table(new[] { "", "최소 길이 Lmin", "최대 길이 Lmax", "Tube(보어) 직경", "Rod 직경", "실린더 개수" },
            new[] { ("Bm", "붐 실린더"), ("Am", "암 실린더"), ("Bk", "버켓 실린더") }
            .Select(x => new UIElement[] { Ui.Text(x.Item2), N(x.Item1 + "Lmin"), N(x.Item1 + "Lmax"), N(x.Item1 + "Dt"), N(x.Item1 + "Dr"), N(x.Item1 + "N") })));
        p.Children.Add(Ui.Hint("Lmin/Lmax: 행정 범위 밖 데이터 경고용. 직경: 압력 → 추력 (헤드 면적 = π/4·Tube², 로드 쪽 = π/4·(Tube²−Rod²)). 실린더 개수: 붐 2개, 암·버켓 1개가 기본이며, 핀 좌표는 실린더 1개 기준이고 추력·토크는 개수만큼 곱합니다."));

        p.Children.Add(Ui.Bold("동역학 설정"));
        p.Children.Add(Ui.Row(Ui.Labeled("관성모멘트 기준점", Sel("IRef", "com", ("com", "무게중심"), ("pivot", "링크 시작 핀"))),
            Ui.Labeled("선회체 수직(Z)축 관성모멘트", Sel("SwAxis", "zz", ("xx", "Ixx"), ("yy", "Iyy"), ("zz", "Izz"))),
            Ui.Labeled("압력 단위", Sel("pUnit", "bar", ("bar", "bar"), ("MPa", "MPa")))));
        p.Children.Add(Ui.Row(Ui.Labeled("선회 모터 배기량 [cc/rev]", N("SwDisp")), Ui.Labeled("선회 감속비", N("SwRatio")), Ui.Labeled("선회 효율 (0~1)", N("SwEff"))));

        var adv = new StackPanel();
        adv.Children.Add(Ui.Hint("점 이름이 가리키는 위치가 다르면 역할을 바꿔 지정하세요. 자세가 뒤집혀 보이면 해(±)를 바꿔 보세요."));
        adv.Children.Add(Ui.Table(new[] { "역할", "점" }, Kinematics.RoleList.Select(r =>
        {
            var c = new ComboBox { MinWidth = 80 };
            foreach (var pt in Kinematics.Points) c.Items.Add(pt);
            c.SelectionChanged += (_, _) => RoleText();
            _role[r.Key] = c;
            return new UIElement[] { Ui.Text(r.Label), c };
        })));
        adv.Children.Add(Ui.Row(Signs.Zip(new[] { "붐 실린더 해", "암 실린더 해", "버켓: 실린더·가이드 교점 해", "버켓: 푸시 링크 해" })
            .Select(x => (UIElement)Ui.Labeled(x.Second, Sel(x.First.Key, x.First.Def, ("1", "+1"), ("-1", "-1")))).ToArray()));
        p.Children.Add(Ui.Section("고급: 점 역할 지정 · 해 선택", adv));

        var bottom = new StackPanel { Margin = new Thickness(12, 4, 12, 10) };
        bottom.Children.Add(_msg);
        bottom.Children.Add(Ui.Row(
            Ui.Btn("적용", Apply, true),
            Ui.Btn("JSON 내보내기", Export),
            Ui.Btn("JSON 가져오기", Import),
            Ui.Btn("예시 값 채우기", () => { Fill(KinParams.Demo()); Ui.SetMsg(_msg, "예시 값을 채웠습니다. [적용]을 누르세요."); }),
            Ui.Btn("닫기", Close)));
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        Fill(k);
    }

    TextBox N(string key) { var t = new TextBox { Width = 90 }; _num[key] = t; return t; }

    ComboBox Sel(string key, string def, params (string V, string T)[] items)
    {
        var c = new ComboBox { MinWidth = 70, Tag = def };
        foreach (var (v, t) in items) c.Items.Add(new ComboBoxItem { Content = t, Tag = v });
        _txt[key] = c;
        return c;
    }

    WrapPanel Grid2((string Key, string Label)[] f) => Ui.Row(f.Select(x => (UIElement)Ui.Labeled(x.Label, N(x.Key))).ToArray());

    void Fill(KinParams k)
    {
        _name.Text = k.Str("name"); _desc.Text = k.Str("desc");
        foreach (var (key, t) in _num) t.Text = Ui.F(k[key]);
        foreach (var (key, c) in _txt)
        {
            string v = Signs.Any(s => s.Key == key) ? (double.IsFinite(k[key]) ? ((int)k[key]).ToString() : (string)c.Tag) : k.Str(key, (string)c.Tag);
            c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == v) ?? c.Items[0];
        }
        foreach (var r in Kinematics.RoleList) _role[r.Key].SelectedItem = k.Roles.GetValueOrDefault(r.Key, r.DefaultPoint);
        RoleText();
    }

    void RoleText()
    {
        var ro = new Dictionary<string, string>();
        foreach (var r in Kinematics.RoleList)
        {
            if (_role.GetValueOrDefault(r.Key)?.SelectedItem is not string pt) continue;
            var n = r.Label.Split(" (")[0];
            ro[pt] = ro.TryGetValue(pt, out var s) ? s + ", " + n : n;
        }
        foreach (var (pt, t) in _roleText) t.Text = ro.GetValueOrDefault(pt, "— 미사용");
    }

    KinParams Read()
    {
        var k = _base.Clone();
        k.Text["name"] = _name.Text.Trim(); k.Text["desc"] = _desc.Text.Trim();
        foreach (var (key, t) in _num) k[key] = Ui.P(t.Text);
        foreach (var (key, c) in _txt)
        {
            var v = (string)((ComboBoxItem)c.SelectedItem).Tag;
            if (Signs.Any(s => s.Key == key)) k[key] = Ui.P(v); else k.Text[key] = v;
        }
        k.Roles.Clear();
        foreach (var (r, c) in _role) if (c.SelectedItem is string pt) k.Roles[r] = pt;
        return k;
    }

    /// <summary>실린더 값 검사 (HTML kCheck)</summary>
    public static string Check(KinParams k)
    {
        var e = new List<string>(); string[] nm = { "붐", "암", "버켓" };
        for (int i = 0; i < 3; i++)
        {
            string p = ExcavatorSignals.CylPrefix[i];
            double a = k[p + "Lmin"], b = k[p + "Lmax"], t = k[p + "Dt"], r = k[p + "Dr"];
            if (double.IsFinite(a) && double.IsFinite(b) && !(a < b)) e.Add(nm[i] + " Lmin < Lmax 여야 합니다");
            if (double.IsFinite(t) && double.IsFinite(r) && !(r < t && r >= 0)) e.Add(nm[i] + " Rod 직경 < Tube 직경 이어야 합니다");
        }
        return string.Join(" / ", e);
    }

    void Apply()
    {
        var k = Read();
        if (Kinematics.Build(k, out var err) == null) { Ui.SetMsg(_msg, err ?? "파라미터 오류", true); return; }
        var c = Check(k);
        if (c != "") { Ui.SetMsg(_msg, c, true); return; }
        var e2 = _onApply(k);
        if (e2 != null) { Ui.SetMsg(_msg, e2, true); return; }
        Close();
    }

    void Export()
    {
        var k = Read();
        var name = string.Concat((k.Name == "" ? "kinematics" : k.Name).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dlg = new SaveFileDialog { FileName = name + ".json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, k.ToJson());
        Ui.SetMsg(_msg, "저장했습니다: " + dlg.FileName);
    }

    void Import()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { var k = KinParams.FromJson(File.ReadAllText(dlg.FileName)); _base = k.Clone(); Fill(k); Ui.SetMsg(_msg, "불러왔습니다. 확인 후 [적용]을 누르세요."); }
        catch (Exception ex) { Ui.SetMsg(_msg, "파일을 읽을 수 없습니다: " + ex.Message, true); }
    }
}
