using DataAnalyzer.Core.Storage;

namespace DataAnalyzer.Core.Tests;

public class StorageTests
{
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "da_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void File_store_reads_old_string_format_and_round_trips()
    {
        var d = Tmp();
        try
        {
            // 예전 형식: 값이 JSON을 담은 문자열
            File.WriteAllText(Path.Combine(d, "excavator_params.json"), "{\"A\":\"{\\\"name\\\":\\\"A\\\",\\\"L1\\\":1.5}\"}");
            var s = FileNamedStore.AppData(d);
            var l = s.List("excavator");
            Assert.Equal("{\"name\":\"A\",\"L1\":1.5}", l["A"]);

            s.Put("excavator", "B", "{\"name\":\"B\"}");
            s.Put("excavator_inertial", "set1", "{\"4\":{\"M\":10}}");
            l = s.List("excavator");
            Assert.Equal(new[] { "A", "B" }, l.Keys);
            Assert.Equal("{\"name\":\"A\",\"L1\":1.5}", l["A"]);
            Assert.True(File.Exists(Path.Combine(d, "excavator_inertial_sets.json")));
            Assert.Single(s.List("excavator_inertial"));

            s.Remove("excavator", "A");
            Assert.Equal(new[] { "B" }, s.List("excavator").Keys);
            Assert.Empty(s.List("wheelloader"));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Db_settings_use_first_file_with_connection_string()
    {
        var d = Tmp();
        try
        {
            string a = Path.Combine(d, "a.json"), b = Path.Combine(d, "b.json");
            File.WriteAllText(a, "{\"ConnectionString\":\"\"}");
            new DbSettings { ConnectionString = "Server=S;Database=D;Integrated Security=true" }.Save(b);
            var s = DbSettings.Load(a, b);
            Assert.Equal(b, s.Source);
            var store = s.CreateStore(FileNamedStore.AppData(d));
            var sql = Assert.IsType<SqlNamedStore>(store);
            Assert.Equal("DB S/D", sql.Where);

            var none = DbSettings.Load(a, Path.Combine(d, "missing.json"));
            Assert.Null(none.Source);
            Assert.IsType<FileNamedStore>(none.CreateStore(FileNamedStore.AppData(d)));
        }
        finally { Directory.Delete(d, true); }
    }
}
