namespace DataAnalyzer.Core.Data;

/// <summary>CSV에서 읽은 그대로의 표. 열 순서를 유지하며, 시간 열은 아직 정하지 않은 상태.</summary>
/// <param name="Names">열 이름</param>
/// <param name="Columns">열별 값 (빈 칸·해석 불가 = NaN, 날짜 = Unix ms)</param>
/// <param name="IsDate">열이 날짜 형식인지</param>
/// <param name="Units">열별 단위 (없으면 빈 문자열)</param>
/// <param name="HasUnitRow">2행이 단위 행이었는지</param>
/// <param name="Categories">Action 열의 코드 → 원래 문자열</param>
public sealed record RawTable(
    string[] Names,
    double[][] Columns,
    bool[] IsDate,
    string[] Units,
    bool HasUnitRow,
    IReadOnlyDictionary<string, SortedDictionary<int, string>> Categories)
{
    public int RowCount => Columns.Length == 0 ? 0 : Columns[0].Length;
}
