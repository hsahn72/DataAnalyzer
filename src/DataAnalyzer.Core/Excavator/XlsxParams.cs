using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Excavator;

/// <summary>엑셀 한 시트의 F5:G69 (행 번호, F값, G값). 값은 string, double, bool 또는 null.</summary>
public sealed record XlSheet(string Name, IReadOnlyList<(int Row, object? F, object? G)> Rows);

/// <summary>가장 많이 인식된 시트의 매핑 결과</summary>
public sealed record XlMapResult(string Sheet, Dictionary<string, object> Map, List<string> Unmapped, int Count);

/// <summary>Excel(.xlsx) 양식 F5:G69 (F=이름, G=값) → Kinematic 파라미터 (HTML xlOwn / xlMap / xlKey)</summary>
public static class XlsxParams
{
    public const int FirstRow = 5, LastRow = 69;

    static readonly string[] Pts = { "P20", "P21", "P30", "P31", "P40", "P41", "PGL" };
    static readonly string[] Len = { "BPH", "L1", "L2", "L3", "L4", "Lgl", "Lpl" };

    /// <summary>필수 항목 (길이 + 점 좌표)</summary>
    public static readonly string[] Required = Len.Concat(Pts.SelectMany(p => new[] { p + "x", p + "y" })).ToArray();

    static readonly string[] All = Required
        .Concat(new[] { "M1", "M2", "M3", "M4" })
        .Concat(new[] { 1, 2, 3, 4 }.SelectMany(i => new[] { "xx", "yy", "zz" }.Select(a => "I" + i + a)))
        .Concat(new[] { "BmN", "AmN", "BkN", "BmLmin", "BmLmax", "BmDt", "BmDr", "AmLmin", "AmLmax", "AmDt", "AmDr", "BkLmin", "BkLmax", "BkDt", "BkDr",
                        "C1x", "C1y", "C1z", "C2x", "C2y", "C3x", "C3y", "C4x", "C4y", "SwDisp", "SwRatio", "SwEff", "name", "desc" })
        .ToArray();

    static readonly Dictionary<string, string> Ci = All.ToDictionary(k => k.ToLowerInvariant(), k => k);

    static readonly Dictionary<string, string> Alias = new()
    {
        ["boompivotheight"] = "BPH", ["boompivot"] = "BPH", ["swinglinklength"] = "L1", ["boomlinklength"] = "L2", ["armlinklength"] = "L3",
        ["bucketlinklength"] = "L4", ["guidelinklength"] = "Lgl", ["pushlinklength"] = "Lpl", ["swingbodymass"] = "M1", ["boomlinkmass"] = "M2",
        ["boommass"] = "M2", ["armlinkmass"] = "M3", ["armmass"] = "M3", ["bucketlinkmass"] = "M4", ["bucketmass"] = "M4",
        ["machinename"] = "name", ["description"] = "desc",
    };

    static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>.xlsx 파일에서 앞쪽 12개 시트의 F5:G69를 읽는다.</summary>
    public static List<XlSheet> Read(Stream s)
    {
        ZipArchive z;
        try { z = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new InvalidDataException("xlsx(zip) 형식이 아닙니다. .xlsx로 저장한 파일인지 확인하세요."); }
        using (z)
        {
            XDocument? Doc(string n) { var e = z.GetEntry(n); if (e == null) return null; using var st = e.Open(); return XDocument.Load(st); }
            var wb = Doc("xl/workbook.xml") ?? throw new InvalidDataException("workbook.xml이 없습니다");
            var rm = (Doc("xl/_rels/workbook.xml.rels")?.Root?.Elements() ?? Enumerable.Empty<XElement>())
                .Where(r => r.Name.LocalName == "Relationship")
                .ToDictionary(r => (string?)r.Attribute("Id") ?? "", r => (string?)r.Attribute("Target") ?? "");
            var ss = new List<string>();
            if (Doc("xl/sharedStrings.xml") is { } sst)
                foreach (var si in sst.Descendants().Where(e => e.Name.LocalName == "si"))
                    ss.Add(string.Concat(si.Descendants().Where(e => e.Name.LocalName == "t").Select(t => t.Value)));

            var res = new List<XlSheet>();
            foreach (var sh in wb.Descendants().Where(e => e.Name.LocalName == "sheet").Take(12))
            {
                var rid = (string?)sh.Attribute(RelNs + "id") ?? "";
                var tg = rm.GetValueOrDefault(rid, "");
                tg = tg.StartsWith('/') ? tg[1..] : "xl/" + tg;
                var x = Doc(tg); if (x == null) continue;
                var cell = new Dictionary<int, (object? F, object? G)>();
                foreach (var c in x.Descendants().Where(e => e.Name.LocalName == "c"))
                {
                    var m = Regex.Match((string?)c.Attribute("r") ?? "", "^([FG])(\\d+)$"); if (!m.Success) continue;
                    int r = int.Parse(m.Groups[2].Value); if (r < FirstRow || r > LastRow) continue;
                    var t = (string?)c.Attribute("t");
                    var v = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value;
                    var isEl = c.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                    object? val = t switch
                    {
                        "s" => v != null && int.TryParse(v, out var i) && i >= 0 && i < ss.Count ? ss[i] : null,
                        "inlineStr" => isEl == null ? null : string.Concat(isEl.Descendants().Where(e => e.Name.LocalName == "t").Select(q => q.Value)),
                        "str" or "e" => v,
                        "b" => v == null ? null : v == "1",
                        _ => string.IsNullOrEmpty(v) ? null : double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
                    };
                    var cur = cell.GetValueOrDefault(r);
                    cell[r] = m.Groups[1].Value == "F" ? (val, cur.G) : (cur.F, val);
                }
                var rows = new List<(int, object?, object?)>();
                for (int r = FirstRow; r <= LastRow; r++) { var cv = cell.GetValueOrDefault(r); rows.Add((r, cv.F, cv.G)); }
                res.Add(new XlSheet((string?)sh.Attribute("name") ?? "", rows));
            }
            if (res.Count == 0) throw new InvalidDataException("시트를 찾을 수 없습니다");
            return res;
        }
    }

    /// <summary>엑셀의 이름 → 파라미터 키 (모르면 null)</summary>
    public static string? Key(object? raw)
    {
        var s = Str(raw).Trim(); if (s == "") return null;
        var pm = Regex.Match(s, "\\(([^)]+)\\)");
        if (pm.Success && Ci.TryGetValue(Regex.Replace(pm.Groups[1].Value, "\\s+", "").ToLowerInvariant(), out var k0)) return k0;
        var compact = Regex.Replace(s, "\\s+", "").ToLowerInvariant();
        if (Ci.TryGetValue(compact, out var k1)) return k1;
        var nrm = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9가-힣]", "");
        if (Alias.TryGetValue(nrm, out var k2)) return k2;
        var tk = Units.Tokens(s);
        string? link = tk.Select(t =>
        {
            var m = Regex.Match(t, "^l?(bm|boom|am|arm|bk|bkt|bucket)$");
            return m.Success ? m.Groups[1].Value switch { "bm" or "boom" => "Bm", "am" or "arm" => "Am", _ => "Bk" } : null;
        }).FirstOrDefault(x => x != null);
        if (link != null)
        {
            bool cyl = tk.Contains("cyl") || nrm.Contains("cylinder") || tk.Contains("rod") || tk.Contains("tube") || tk.Contains("bore");
            if (cyl)
            {
                if (tk.Any(t => t is "num" or "number" or "count" or "qty" or "ea" or "no" or "n")) return link + "N";
                if (tk.Contains("min")) return link + "Lmin";
                if (tk.Contains("max")) return link + "Lmax";
                if (tk.Contains("rod")) return link + "Dr";
                if (tk.Contains("tube") || tk.Contains("bore") || tk.Contains("head")) return link + "Dt";
            }
        }
        return null;
    }

    static string Str(object? v) => v switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => v.ToString() ?? "",
    };

    /// <summary>시트마다 이름을 매핑해 가장 많이 인식된 시트를 고른다.</summary>
    public static XlMapResult? Map(IEnumerable<XlSheet> sheets)
    {
        XlMapResult? best = null;
        foreach (var sh in sheets)
        {
            var map = new Dictionary<string, object>(); var un = new List<string>();
            foreach (var (_, f, g) in sh.Rows)
            {
                if (f == null || Str(f).Trim() == "") continue;
                var k = Key(f);
                if (k == null) { un.Add(Str(f).Trim()); continue; }
                if (k is "name" or "desc") { map[k] = Str(g); continue; }
                double v = g switch
                {
                    double d => d,
                    bool b => b ? 1 : 0,
                    string t when t.Trim() != "" => double.TryParse(t.Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
                    _ => double.NaN,
                };
                if (!double.IsFinite(v)) { un.Add(Str(f).Trim() + " (값 없음)"); continue; }
                map[k] = v;
            }
            if (best == null || map.Count > best.Count) best = new XlMapResult(sh.Name, map, un, map.Count);
        }
        return best;
    }

    /// <summary>매핑 결과를 파라미터에 덮어쓴다. 반환: (비어 있는 필수, 엑셀에 없어 기존 값이 남은 필수)</summary>
    public static (List<string> Missing, List<string> Kept) Apply(KinParams k, XlMapResult r, string unit, string fileName)
    {
        foreach (var (key, v) in r.Map)
            if (v is double d) k[key] = d; else k.Text[key] = (string)v;
        k.Unit = unit;
        if (!r.Map.ContainsKey("name")) k.Name = Path.GetFileNameWithoutExtension(fileName);
        var miss = Required.Where(x => !double.IsFinite(k[x])).ToList();
        var keep = Required.Where(x => !r.Map.ContainsKey(x) && !miss.Contains(x)).ToList();
        return (miss, keep);
    }

    /// <summary>사용자에게 보여 줄 요약</summary>
    public static string Report(string fileName, XlMapResult r, string unit, List<string> miss, List<string> keep)
    {
        var s = $"✓ {fileName} [시트 {r.Sheet}] — {r.Count}개 항목 가져옴 (길이 단위 {unit} 로 지정)";
        if (r.Unmapped.Count > 0) s += $"\n엑셀에는 있으나 매핑되지 않은 이름({r.Unmapped.Count}): " + string.Join(", ", r.Unmapped.Take(12)) + (r.Unmapped.Count > 12 ? " …" : "");
        if (miss.Count > 0) s += "\n⚠ 아직 비어 있는 필수 값: " + string.Join(", ", miss);
        if (keep.Count > 0) s += $"\n⚠ 엑셀에 없어 기존 값이 그대로 남은 필수 항목({keep.Count}): " + string.Join(", ", keep.Take(14)) + (keep.Count > 14 ? " …" : "");
        return s + "\n확인 후 [적용]을 누르세요.";
    }
}
