using System.Text.Json;

namespace DataAnalyzer.Core.Config;

/// <summary>이름 붙인 차트 구성을 JSON 파일 하나에 저장한다.</summary>
public sealed class PresetStore
{
    readonly string _path;
    public PresetStore(string path) { _path = path; }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataAnalyzer", "presets.json");

    public SortedDictionary<string, ChartConfig> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            return JsonSerializer.Deserialize<SortedDictionary<string, ChartConfig>>(File.ReadAllText(_path), ChartConfig.Json) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    public void Save(SortedDictionary<string, ChartConfig> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(presets, ChartConfig.Json));
    }

    public void Put(string name, ChartConfig cfg) { var p = Load(); p[name] = cfg; Save(p); }
    public void Remove(string name) { var p = Load(); if (p.Remove(name)) Save(p); }
}
