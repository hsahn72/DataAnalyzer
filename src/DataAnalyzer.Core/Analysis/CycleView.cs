using System.Text.RegularExpressions;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.Core.Analysis;

/// <summary>Cycle 하나의 샘플들 (겹쳐보기용). Rel = Cycle(또는 선택 Action) 시작 기준 시간 [s 또는 시간 단위].</summary>
public sealed class CycleGroup
{
    public required int Cycle { get; init; }
    public required int[] Index { get; init; }
    public required double[] Rel { get; init; }
}

/// <summary>여러 Cycle을 같은 상대 시간 격자(M점)에 보간해 낸 평균·표준편차·최소·최대.</summary>
public sealed class CycleBand
{
    public required int M { get; init; }
    public required double MaxRel { get; init; }
    /// <summary>격자점별 표본 수 (최소 표본 수 미만이면 0)</summary>
    public required double[] Count { get; init; }
    public required double[] Mean { get; init; }
    public required double[] Sd { get; init; }
    public required double[] Min { get; init; }
    public required double[] Max { get; init; }
    public double X(int k) => k / (double)(M - 1) * MaxRel;
}

/// <summary>
/// Cycle·Action 신호로 데이터 거르기와 Cycle 겹쳐보기 (HTML의 CY.mask / CY.groups / CY.band).
/// </summary>
public static class CycleView
{
    static readonly Regex CycleName = new(@"^cycle([_\s]?(no|num|number|id|idx|index))?$", RegexOptions.IgnoreCase);
    static readonly Regex ActionNameRx = new(@"^action([_\s]?(id|no|code|name))?$", RegexOptions.IgnoreCase);

    /// <summary>Cycle 번호 열 이름 (Cycle, Cycle_No …), 없으면 null</summary>
    public static string? FindCycleColumn(IEnumerable<string> names) => names.FirstOrDefault(CycleName.IsMatch);
    /// <summary>Action 열 이름 (Action, Action_Code …), 없으면 null</summary>
    public static string? FindActionColumn(IEnumerable<string> names) => names.FirstOrDefault(ActionNameRx.IsMatch);

    /// <summary>Cycle 콤보용: 0보다 큰 값들 (오름차순)</summary>
    public static List<int> CycleValues(double[]? a) =>
        a == null ? new() : a.Where(v => v > 0 && v == Math.Floor(v) && double.IsFinite(v)).Select(v => (int)v).Distinct().OrderBy(v => v).ToList();

    /// <summary>Action 콤보용: 0 이상의 정수 값들 (오름차순)</summary>
    public static List<int> ActionValues(double[]? a) =>
        a == null ? new() : a.Where(v => v >= 0 && v == Math.Floor(v) && double.IsFinite(v)).Select(v => (int)v).Distinct().OrderBy(v => v).ToList();

    /// <summary>선택한 Cycle·Action에 해당하는 샘플만 1. cycSel/actSel이 null이면 전체.</summary>
    public static byte[] Mask(int n, double[]? cyc, double[]? act, int? cycSel, int? actSel)
    {
        var m = new byte[n];
        for (int i = 0; i < n; i++)
        {
            bool okC = cycSel == null || cyc == null || cyc[i] == cycSel;
            bool okA = actSel == null || act == null || act[i] == actSel;
            m[i] = okC && okA ? (byte)1 : (byte)0;
        }
        return m;
    }

    /// <summary>마스크 밖을 NaN으로</summary>
    public static double[] ApplyMask(double[] a, byte[] m)
    {
        var o = new double[a.Length];
        for (int i = 0; i < a.Length; i++) o[i] = m[i] != 0 ? a[i] : double.NaN;
        return o;
    }

    /// <summary>
    /// 겹쳐보기 그룹. 각 Cycle(>0)의 샘플 중 선택 조건에 맞는 것을 모으고, 첫 샘플 시각을 0으로 둔다.
    /// Action 0(Idling)은 Action 0을 고른 경우가 아니면 뺀다. timeScale: 날짜 시간축이면 1e-3 (ms → s).
    /// </summary>
    public static (List<CycleGroup> List, double MaxRel) Groups(double[] t, double[] cyc, double[]? act, int? cycSel, int? actSel, double timeScale)
    {
        var map = new Dictionary<int, (List<int> Idx, double T0)>();
        var order = new List<int>();
        for (int i = 0; i < t.Length; i++)
        {
            double cv = cyc[i];
            if (!(cv > 0)) continue;
            int c = (int)cv;
            if (cv != c) continue; // HTML은 Map 키로 실수를 쓰지만, Cycle 번호는 정수
            if (cycSel != null && c != cycSel) continue;
            if (actSel != null && act != null && act[i] != actSel) continue;
            if (actSel != 0 && act != null && act[i] == 0) continue;
            if (!map.TryGetValue(c, out var g)) { g = (new List<int>(), t[i]); map[c] = g; order.Add(c); }
            g.Idx.Add(i);
        }
        double mx = 0;
        var list = new List<CycleGroup>();
        foreach (var c in order.OrderBy(x => x))
        {
            var (idx, t0) = map[c];
            var rel = idx.Select(i => (t[i] - t0) * timeScale).ToArray();
            if (rel.Length > 0 && rel[^1] > mx) mx = rel[^1];
            list.Add(new CycleGroup { Cycle = c, Index = idx.ToArray(), Rel = rel });
        }
        return (list, mx);
    }

    /// <summary>평균 ± 산포 띠. 그룹이 5개보다 많으면 격자점마다 전체의 20% 이상 표본이 있는 곳만 남긴다.</summary>
    public static CycleBand Band(double[] a, List<CycleGroup> groups, double maxRel, int M = 400)
    {
        double mx = maxRel > 0 ? maxRel : 1;
        var sum = new double[M]; var sq = new double[M]; var cnt = new double[M];
        var mn = Enumerable.Repeat(double.PositiveInfinity, M).ToArray();
        var mxv = Enumerable.Repeat(double.NegativeInfinity, M).ToArray();
        foreach (var g in groups)
        {
            var r = g.Rel; var ix = g.Index; int m = r.Length;
            if (m < 2) continue;
            int j = 0;
            for (int k = 0; k < M; k++)
            {
                double x = k / (double)(M - 1) * mx;
                if (x > r[m - 1] + 1e-12) break;
                while (j < m - 2 && r[j + 1] < x) j++;
                double v0 = a[ix[j]], v1 = a[ix[j + 1]];
                if (double.IsNaN(v0) || double.IsNaN(v1)) continue;
                double f = r[j + 1] > r[j] ? Math.Min(1, Math.Max(0, (x - r[j]) / (r[j + 1] - r[j]))) : 0, v = v0 + (v1 - v0) * f;
                sum[k] += v; sq[k] += v * v; cnt[k]++;
                if (v < mn[k]) mn[k] = v;
                if (v > mxv[k]) mxv[k] = v;
            }
        }
        double minN = groups.Count > 5 ? Math.Ceiling(.2 * groups.Count) : 1;
        for (int k = 0; k < M; k++) if (cnt[k] < minN) cnt[k] = 0;
        var mean = new double[M]; var sd = new double[M];
        for (int k = 0; k < M; k++)
            if (cnt[k] > 0) { mean[k] = sum[k] / cnt[k]; sd[k] = Math.Sqrt(Math.Max(0, sq[k] / cnt[k] - mean[k] * mean[k])); }
        return new CycleBand { M = M, MaxRel = mx, Count = cnt, Mean = mean, Sd = sd, Min = mn, Max = mxv };
    }
}
