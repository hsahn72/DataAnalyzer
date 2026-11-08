using System.Windows;
using System.Windows.Controls;
using DataAnalyzer.App.Analysis;
using DataAnalyzer.Core.Storage;
using Microsoft.Data.SqlClient;

namespace DataAnalyzer.App.Dialogs;

/// <summary>
/// DB 연결 설정. 연결하면 굴착기·휠로더 파라미터, 관성 추정 세트, 차트 구성 프리셋을
/// SQL Server 테이블 dbo.DA_Preset에 저장해 팀이 함께 쓴다. 설정은 %AppData%\DataAnalyzer\db.json.
/// </summary>
public sealed class DbDialog : Window
{
    static readonly string[] Kinds = { "excavator", "wheelloader", "excavator_inertial", "chart" };

    readonly TextBox _server = new() { Width = 220 }, _db = new() { Width = 160 }, _user = new() { Width = 140 };
    readonly PasswordBox _pw = new() { Width = 140 };
    readonly CheckBox _win = new() { Content = "Windows 인증 (회사 로그인 계정)", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _trust = new() { Content = "서버 인증서 신뢰", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "사내 서버처럼 인증서가 따로 없는 경우 켜 둡니다 (TrustServerCertificate)" };
    readonly TextBox _raw = new() { MinWidth = 520, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
    readonly TextBlock _msg = Ui.Msg(), _now = Ui.Msg();

    public DbDialog()
    {
        Title = "DB 연결 (팀 공유 저장소)"; Width = 620; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(Ui.Hint("SQL Server에 연결하면 파라미터·관성 추정 세트·차트 구성의 [저장]이 DB(dbo.DA_Preset 테이블)로 갑니다. " +
                               "연결하지 않으면 지금처럼 이 PC에 저장합니다. 테이블은 처음 연결할 때 자동으로 만들어집니다."));
        p.Children.Add(_now);
        p.Children.Add(Ui.Row(Ui.Labeled("서버 (예: SERVER\\SQLEXPRESS 또는 10.0.0.5,1433)", _server), Ui.Labeled("데이터베이스", _db)));
        p.Children.Add(Ui.Row(_win, Ui.Labeled("사용자", _user), Ui.Labeled("암호", _pw), _trust));
        _win.Checked += (_, _) => Auth(); _win.Unchecked += (_, _) => Auth();
        p.Children.Add(Ui.Section("고급: 연결 문자열 직접 입력", new StackPanel { Children = { Ui.Hint("값이 있으면 위 칸 대신 이것을 씁니다."), _raw } }));
        p.Children.Add(Ui.Hint("SQL 인증 암호는 이 PC의 %AppData%\\DataAnalyzer\\db.json에 그대로 저장됩니다. 가능하면 Windows 인증을 쓰세요."));
        p.Children.Add(_msg);
        p.Children.Add(Ui.Row(
            Ui.Btn("연결 확인", Test),
            Ui.Btn("저장하고 사용", SaveUse, true),
            Ui.Btn("이 PC 프리셋을 DB로 복사", Copy, tip: "이 PC에 저장해 둔 파라미터·세트·차트 구성을 DB에 올립니다 (같은 이름은 덮어씀)"),
            Ui.Btn("DB 끄기", Off, tip: "이 PC에 저장하는 방식으로 돌아갑니다"),
            Ui.Btn("닫기", Close)));
        Content = p;
        Load();
    }

    void Auth() { bool w = _win.IsChecked == true; _user.IsEnabled = _pw.IsEnabled = !w; }

    void Load()
    {
        var s = DbSettings.Load();
        _now.Text = "현재 저장 위치: " + AppStore.Where + (s.Source != null ? "  ·  설정 파일: " + s.Source : "") + (AppStore.LastError != "" ? "\n" + AppStore.LastError : "");
        if (s.ConnectionString != "")
        {
            try
            {
                var b = new SqlConnectionStringBuilder(s.ConnectionString);
                _server.Text = b.DataSource; _db.Text = b.InitialCatalog; _win.IsChecked = b.IntegratedSecurity; _user.Text = b.UserID; _pw.Password = b.Password;
                _trust.IsChecked = b.TrustServerCertificate;
            }
            catch (ArgumentException) { _raw.Text = s.ConnectionString; }
        }
        Auth();
    }

    string? Cs()
    {
        if (_raw.Text.Trim() != "") return _raw.Text.Trim();
        if (_server.Text.Trim() == "" || _db.Text.Trim() == "") { Ui.SetMsg(_msg, "서버와 데이터베이스를 입력하세요", true); return null; }
        var b = new SqlConnectionStringBuilder { DataSource = _server.Text.Trim(), InitialCatalog = _db.Text.Trim(), TrustServerCertificate = _trust.IsChecked == true, ApplicationName = "DataAnalyzer" };
        if (_win.IsChecked == true) b.IntegratedSecurity = true; else { b.UserID = _user.Text.Trim(); b.Password = _pw.Password; }
        return b.ConnectionString;
    }

    string? Check(string cs)
    {
        Ui.SetMsg(_msg, "연결하는 중…"); Mouse(true);
        try { return new SqlNamedStore(cs).Test(); }
        finally { Mouse(false); }
    }

    void Mouse(bool wait) => System.Windows.Input.Mouse.OverrideCursor = wait ? System.Windows.Input.Cursors.Wait : null;

    void Test()
    {
        var cs = Cs(); if (cs == null) return;
        var e = Check(cs);
        Ui.SetMsg(_msg, e == null ? "✓ 연결되었습니다. [저장하고 사용]을 누르면 이제부터 DB에 저장합니다." : "연결 실패: " + e, e != null);
    }

    void SaveUse()
    {
        var cs = Cs(); if (cs == null) return;
        var e = Check(cs);
        if (e != null) { Ui.SetMsg(_msg, "연결 실패: " + e, true); return; }
        new DbSettings { ConnectionString = cs }.Save(DbSettings.UserPath);
        AppStore.Reset(); Load();
        Ui.SetMsg(_msg, "저장했습니다. 이제부터 " + AppStore.Where + " 에 저장합니다. 열려 있는 분석 창은 다시 열면 DB 목록이 보입니다.");
    }

    void Copy()
    {
        if (!AppStore.IsDb) { Ui.SetMsg(_msg, "먼저 DB에 연결하세요 ([저장하고 사용])", true); return; }
        var local = FileNamedStore.AppData(); int n = 0;
        try
        {
            foreach (var k in Kinds) foreach (var (name, js) in local.List(k)) { AppStore.Store.Put(k, name, js); n++; }
            Ui.SetMsg(_msg, $"이 PC의 프리셋 {n}개를 DB에 복사했습니다.");
        }
        catch (Exception ex) { Ui.SetMsg(_msg, $"{n}개 복사 후 실패: " + ex.Message, true); }
    }

    void Off()
    {
        try { if (File.Exists(DbSettings.UserPath)) File.Delete(DbSettings.UserPath); }
        catch (IOException ex) { Ui.SetMsg(_msg, "설정 파일을 지우지 못했습니다: " + ex.Message, true); return; }
        AppStore.Reset(); Load();
        Ui.SetMsg(_msg, AppStore.IsDb ? "개인 설정은 지웠지만, 프로그램 폴더의 공유 설정(db.json) 때문에 계속 DB를 씁니다." : "이제 이 PC에 저장합니다.");
    }
}
