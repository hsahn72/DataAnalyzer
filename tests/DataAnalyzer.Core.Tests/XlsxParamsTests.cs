using System.IO.Compression;
using System.Text;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.Core.Tests;

/// <summary>Excel F5:G69 파라미터 가져오기. 이름 매핑 기대값은 HTML xlKey를 node로 돌린 결과.</summary>
public class XlsxParamsTests
{
    [Theory]
    [InlineData("Boom Pivot Height", "BPH")]
    [InlineData("붐 링크 길이 (L2)", "L2")]
    [InlineData("L1", "L1")]
    [InlineData("p20x", "P20x")]
    [InlineData("P 21 y", "P21y")]
    [InlineData("Boom Cyl Min Length", "BmLmin")]
    [InlineData("Arm cylinder rod dia", "AmDr")]
    [InlineData("Bucket Cyl Tube", "BkDt")]
    [InlineData("Bkt cyl bore", "BkDt")]
    [InlineData("Boom cyl number", "BmN")]
    [InlineData("Arm Cyl EA", "AmN")]
    [InlineData("Swing Body Mass", "M1")]
    [InlineData("bucketmass", "M4")]
    [InlineData("Machine Name", "name")]
    [InlineData("Description", "desc")]
    [InlineData("I3zz", "I3zz")]
    [InlineData("Unknown thing", null)]
    [InlineData("Arm link length", "L3")]
    [InlineData("Boom rod length max", "BmLmax")]
    [InlineData("LBm cyl max", "BmLmax")]
    [InlineData("C1z", "C1z")]
    [InlineData("SwEff", "SwEff")]
    [InlineData("Bucket Cyl Head Dia", "BkDt")]
    public void Key_matches_html(string name, string? key) => Assert.Equal(key, XlsxParams.Key(name));

    static MemoryStream Xlsx()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string n, string s) { using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false)); w.Write(s); }
            const string M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Put("xl/workbook.xml", $"<workbook xmlns=\"{M}\" xmlns:r=\"{R}\"><sheets><sheet name=\"Cover\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Spec\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
            Put("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>");
            Put("xl/sharedStrings.xml", $"<sst xmlns=\"{M}\"><si><t>Boom Pivot Height</t></si><si><r><t>Boom Cyl </t></r><r><t>Min</t></r></si><si><t>Machine Name</t></si></sst>");
            Put("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{M}\"><sheetData><row r=\"5\"><c r=\"F5\" t=\"inlineStr\"><is><t>L1</t></is></c><c r=\"G5\"><v>1</v></c></row></sheetData></worksheet>");
            Put("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{M}\"><sheetData>" +
                "<row r=\"4\"><c r=\"F4\" t=\"inlineStr\"><is><t>L2</t></is></c><c r=\"G4\"><v>9</v></c></row>" +
                "<row r=\"5\"><c r=\"F5\" t=\"s\"><v>0</v></c><c r=\"G5\"><v>2150.5</v></c></row>" +
                "<row r=\"6\"><c r=\"F6\" t=\"s\"><v>1</v></c><c r=\"G6\" t=\"str\"><v>1,820</v></c></row>" +
                "<row r=\"7\"><c r=\"F7\" t=\"s\"><v>2</v></c><c r=\"G7\" t=\"inlineStr\"><is><t>DX-1</t></is></c></row>" +
                "<row r=\"8\"><c r=\"F8\" t=\"inlineStr\"><is><t>Weird</t></is></c><c r=\"G8\"><v>3</v></c></row>" +
                "<row r=\"9\"><c r=\"F9\" t=\"inlineStr\"><is><t>L3</t></is></c></row>" +
                "<row r=\"70\"><c r=\"F70\" t=\"inlineStr\"><is><t>L4</t></is></c><c r=\"G70\"><v>9</v></c></row>" +
                "</sheetData></worksheet>");
        }
        ms.Position = 0; return ms;
    }

    [Fact]
    public void Reads_best_sheet_and_applies()
    {
        var sheets = XlsxParams.Read(Xlsx());
        Assert.Equal(new[] { "Cover", "Spec" }, sheets.Select(s => s.Name));
        Assert.Equal(65, sheets[1].Rows.Count);
        var r = XlsxParams.Map(sheets)!;
        Assert.Equal("Spec", r.Sheet);
        Assert.Equal(3, r.Count);
        Assert.Equal(2150.5, r.Map["BPH"]);
        Assert.Equal(1820.0, r.Map["BmLmin"]);
        Assert.Equal("DX-1", r.Map["name"]);
        Assert.Equal(new[] { "Weird", "L3 (값 없음)" }, r.Unmapped);

        var k = new KinParams(); k["L1"] = 500;
        var (miss, keep) = XlsxParams.Apply(k, r, "mm", "spec.xlsx");
        Assert.Equal(2150.5, k["BPH"]); Assert.Equal("DX-1", k.Name); Assert.Equal("mm", k.Unit);
        Assert.Equal(new[] { "L1" }, keep);
        Assert.Equal(XlsxParams.Required.Length - 2, miss.Count);
    }

    [Fact]
    public void Not_a_zip_throws() => Assert.Throws<InvalidDataException>(() => XlsxParams.Read(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
}
