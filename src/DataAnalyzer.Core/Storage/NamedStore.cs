using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;

namespace DataAnalyzer.Core.Storage;

/// <summary>
/// 이름 붙인 JSON 묶음 저장소. kind = "chart", "excavator", "wheelloader", "excavator_inertial" 처럼 종류,
/// name = 사용자가 붙인 이름, 값 = JSON 문자열.
/// </summary>
public interface INamedStore
{
    /// <summary>사용자에게 보여 줄 저장 위치 ("이 PC" 또는 "DB …")</summary>
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

/// <summary>SQL Server 테이블 dbo.DA_Preset (Dapper). 테이블이 없으면 처음 쓸 때 만든다.</summary>
public sealed class SqlNamedStore : INamedStore
{
    readonly string _cs;
    bool _ready;
    public SqlNamedStore(string connectionString)
    {
        _cs = connectionString;
        // 서버가 꺼져 있을 때 화면이 오래 멈추지 않도록 연결 대기 시간을 따로 적지 않았으면 5초로
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            if (!connectionString.Contains("timeout", StringComparison.OrdinalIgnoreCase)) { b.ConnectTimeout = 5; _cs = b.ConnectionString; }
        }
        catch (ArgumentException) { }
    }

    public string Where
    {
        get
        {
            try { var b = new SqlConnectionStringBuilder(_cs); return $"DB {b.DataSource}/{b.InitialCatalog}"; }
            catch (ArgumentException) { return "DB"; }
        }
    }

    public const string CreateSql = """
        IF OBJECT_ID(N'dbo.DA_Preset', N'U') IS NULL
        CREATE TABLE dbo.DA_Preset (
            Kind      NVARCHAR(40)  NOT NULL,
            Name      NVARCHAR(200) NOT NULL,
            Json      NVARCHAR(MAX) NOT NULL,
            UpdatedBy NVARCHAR(100) NULL,
            UpdatedAt DATETIME2(0)  NOT NULL CONSTRAINT DF_DA_Preset_UpdatedAt DEFAULT SYSDATETIME(),
            CONSTRAINT PK_DA_Preset PRIMARY KEY (Kind, Name)
        );
        """;

    SqlConnection Open()
    {
        var c = new SqlConnection(_cs);
        c.Open();
        if (!_ready) { c.Execute(CreateSql); _ready = true; }
        return c;
    }

    /// <summary>연결 확인. 성공하면 null, 실패하면 오류 메시지.</summary>
    public string? Test()
    {
        try { using var c = Open(); c.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.DA_Preset"); return null; }
        catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException) { return e.Message; }
    }

    public SortedDictionary<string, string> List(string kind)
    {
        using var c = Open();
        var rows = c.Query<(string Name, string Json)>("SELECT Name, Json FROM dbo.DA_Preset WHERE Kind = @kind", new { kind });
        var r = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (n, j) in rows) r[n] = j;
        return r;
    }

    public void Put(string kind, string name, string json)
    {
        using var c = Open();
        c.Execute("""
            UPDATE dbo.DA_Preset SET Json = @json, UpdatedBy = @by, UpdatedAt = SYSDATETIME() WHERE Kind = @kind AND Name = @name;
            IF @@ROWCOUNT = 0 INSERT dbo.DA_Preset (Kind, Name, Json, UpdatedBy) VALUES (@kind, @name, @json, @by);
            """, new { kind, name, json, by = Environment.UserName });
    }

    public void Remove(string kind, string name)
    {
        using var c = Open();
        c.Execute("DELETE dbo.DA_Preset WHERE Kind = @kind AND Name = @name", new { kind, name });
    }
}

/// <summary>DB 연결 설정 (db.json). %AppData%\DataAnalyzer\db.json을 먼저 보고, 없으면 실행 파일 옆의 db.json(공유 배포용).</summary>
public sealed class DbSettings
{
    public string ConnectionString { get; set; } = "";

    public static string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataAnalyzer", "db.json");
    public static string SharedPath => Path.Combine(AppContext.BaseDirectory, "db.json");

    /// <summary>실제로 읽은 파일 (없으면 null)</summary>
    public string? Source { get; private set; }

    public static DbSettings Load(params string[] paths)
    {
        if (paths.Length == 0) paths = new[] { UserPath, SharedPath };
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var s = JsonSerializer.Deserialize<DbSettings>(File.ReadAllText(p)) ?? new();
                if (string.IsNullOrWhiteSpace(s.ConnectionString)) continue;
                s.Source = p; return s;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return new DbSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { ConnectionString }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public INamedStore CreateStore(INamedStore fallback) =>
        string.IsNullOrWhiteSpace(ConnectionString) ? fallback : new SqlNamedStore(ConnectionString);
}
