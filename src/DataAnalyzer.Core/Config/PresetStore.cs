using System.Text.Json;
using DataAnalyzer.Core.Storage;

namespace DataAnalyzer.Core.Config;

/// <summary>이름 붙인 차트 구성 저장 (종류 "chart"). 파일 또는 DB 저장소를 쓴다.</summary>
public sealed class PresetStore
{
    public const string Kind = "chart";
    readonly INamedStore _store;

    /// <summary>JSON 파일 하나에 저장</summary>
    public PresetStore(string path) : this(new FileNamedStore(_ => path)) { }
    public PresetStore(INamedStore store) { _store = store; }

    public string Where => _store.Where;

    public SortedDictionary<string, ChartConfig> Load()
    {
        var r = new SortedDictionary<string, ChartConfig>();
        foreach (var (n, js) in _store.List(Kind))
        {
            try { if (JsonSerializer.Deserialize<ChartConfig>(js, ChartConfig.Json) is { } c) r[n] = c; }
            catch (JsonException) { }
        }
        return r;
    }

    public void Put(string name, ChartConfig cfg) => _store.Put(Kind, name, JsonSerializer.Serialize(cfg, ChartConfig.Json));
    public void Remove(string name) => _store.Remove(Kind, name);
}
