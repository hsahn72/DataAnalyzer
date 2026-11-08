using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataAnalyzer.Core.Storage;

/// <summary>
/// 이름 붙인 JSON 묶음 저장소. kind = "chart", "excavator", "wheelloader", "excavator_inertial" 처럼 종류,
/// name = 사용자가 붙인 이름, 값 = JSON 문자열.
/// </summary>
public interface INamedStore
{
    /// <summary>사용자에게 보여 줄 저장 위치 ("이 PC")</summary>
    string Where { get; }
    SortedDictionary<string, string> List(string kind);
    void Put(string kind, string name, string json);
    void Remove(string kind, string name);
}

/// <summary>종류마다 JSON 파일 하나 ({이름: 값}). 값은 JSON 그대로 저장하고, 예전 형식(문자열에 담긴 JSON)도 읽는다.</summary>
public sealed class FileNamedStore : INamedStore
{
    readonly Func<string, string> _path;
    public FileNamedStore(Func<string, string> pathOfKind) { _path = pathOfKind; }

    /// <summary>%AppData%\DataAnalyzer 아래, 예전 파일 이름을 그대로 쓴다.</summary>
    public static FileNamedStore AppData(string? dir = null)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataAnalyzer");
        return new FileNamedStore(k => Path.Combine(dir, k switch
        {
            "chart" => "presets.json",
            "excavator" => "excavator_params.json",
            "wheelloader" => "wheelloader_params.json",
            _ => k + "_sets.json",
        }));
    }

    public string Where => "이 PC";

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public SortedDictionary<string, string> List(string kind)
    {
        var r = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var p = _path(kind);
            if (!File.Exists(p)) return r;
            if (JsonNode.Parse(File.ReadAllText(p)) is not JsonObject o) return r;
            foreach (var (k, v) in o)
                if (v != null) r[k] = v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return r;
    }

    void Write(string kind, SortedDictionary<string, string> all)
    {
        var o = new JsonObject();
        foreach (var (k, v) in all)
        {
            JsonNode? n;
            try { n = JsonNode.Parse(v); } catch (JsonException) { n = JsonValue.Create(v); }
            o[k] = n;
        }
        var p = _path(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!);
        File.WriteAllText(p, o.ToJsonString(Opt));
    }

    public void Put(string kind, string name, string json) { var a = List(kind); a[name] = json; Write(kind, a); }
    public void Remove(string kind, string name) { var a = List(kind); if (a.Remove(name)) Write(kind, a); }
}
