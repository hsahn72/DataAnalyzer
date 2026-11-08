using DataAnalyzer.Core.Storage;

namespace DataAnalyzer.App;

/// <summary>
/// 파라미터·차트 구성 프리셋 저장소. db.json에 연결 문자열이 있으면 SQL Server(Dapper), 없으면 이 PC(%AppData%\DataAnalyzer).
/// DB에 연결하지 못하면 이번 실행 동안은 이 PC 저장소로 바꾸고 LastError에 이유를 남긴다.
/// </summary>
public static class AppStore
{
    static INamedStore? _s;
    static readonly FileNamedStore Local = FileNamedStore.AppData();

    public static INamedStore Store => _s ??= DbSettings.Load().CreateStore(Local);
    public static bool IsDb => Store is SqlNamedStore;
    public static string Where => Store.Where;
    /// <summary>마지막 DB 오류 (없으면 "")</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>설정을 바꾼 뒤 다시 읽는다.</summary>
    public static void Reset() { _s = null; LastError = ""; }

    static T Run<T>(Func<INamedStore, T> f)
    {
        try { return f(Store); }
        catch (Exception e) when (Store is SqlNamedStore)
        {
            LastError = "DB에 연결하지 못해 이번 실행 동안은 이 PC에 저장합니다: " + e.Message;
            _s = Local;
            return f(Local);
        }
    }

    public static SortedDictionary<string, string> List(string kind) => Run(s => s.List(kind));
    public static void Put(string kind, string name, string json) => Run(s => { s.Put(kind, name, json); return 0; });
    public static void Remove(string kind, string name) => Run(s => { s.Remove(kind, name); return 0; });

    /// <summary>저장 버튼 옆에 보여 줄 위치 설명</summary>
    public static string SaveTip => Where == "이 PC" ? "이 PC에 저장 (%AppData%\\DataAnalyzer)" : Where + " 에 저장 (팀이 함께 씀)";

    /// <summary>위 동작(DB 실패 시 이 PC로 전환)을 INamedStore로 감싼 것</summary>
    public static readonly INamedStore Safe = new SafeStore();

    sealed class SafeStore : INamedStore
    {
        public string Where => AppStore.Where;
        public SortedDictionary<string, string> List(string kind) => AppStore.List(kind);
        public void Put(string kind, string name, string json) => AppStore.Put(kind, name, json);
        public void Remove(string kind, string name) => AppStore.Remove(kind, name);
    }
}
