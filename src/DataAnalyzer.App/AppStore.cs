using DataAnalyzer.Core.Storage;

namespace DataAnalyzer.App;

/// <summary>
/// 파라미터·관성 추정 세트·차트 구성 프리셋 저장소 (%AppData%\DataAnalyzer).
/// 나중에 팀 공유 DB를 붙일 때는 Store만 바꾸면 된다.
/// </summary>
public static class AppStore
{
    public static INamedStore Store { get; } = FileNamedStore.AppData();
    public static string Where => Store.Where;

    public static SortedDictionary<string, string> List(string kind) => Store.List(kind);
    public static void Put(string kind, string name, string json) => Store.Put(kind, name, json);
    public static void Remove(string kind, string name) => Store.Remove(kind, name);

    /// <summary>저장 버튼 툴팁</summary>
    public static string SaveTip => "이 PC에 저장 (%AppData%\\DataAnalyzer)";

    public static INamedStore Safe => Store;
}
