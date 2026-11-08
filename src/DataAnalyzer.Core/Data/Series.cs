namespace DataAnalyzer.Core.Data;

/// <summary>정렬된 시간축 위의 보간·탐색 함수 (HTML의 bs / valAt / valNear / snapT / meanBetween).</summary>
public static class Series
{
    /// <summary>a[i] &gt;= x 인 첫 번째 i (없으면 a.Length).</summary>
    public static int LowerBound(double[] a, double x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] < x) lo = m + 1; else hi = m;
        }
        return lo;
    }

    /// <summary>시각 tm의 값. 두 샘플 사이는 선형 보간, 한쪽이 NaN이면 가까운 샘플 값.</summary>
    public static double ValAt(double[] t, double[] a, double tm)
    {
        if (t.Length == 0) return double.NaN;
        int i = LowerBound(t, tm);
        if (i <= 0) return a[0];
        if (i >= t.Length) return a[^1];
        if (t[i] == tm) return a[i];
        double p = a[i - 1], q = a[i];
        if (double.IsNaN(p) || double.IsNaN(q)) return (tm - t[i - 1] < t[i] - tm) ? p : q;
        return p + (q - p) * (tm - t[i - 1]) / (t[i] - t[i - 1]);
    }

    /// <summary>가장 가까운 샘플 값. 시간 범위 밖이면 NaN (기준 Data 비교용).</summary>
    public static double ValNear(double[] t, double[] a, double tm)
    {
        int n = t.Length;
        if (n == 0 || tm < t[0] || tm > t[^1]) return double.NaN;
        int i = LowerBound(t, tm);
        if (i <= 0) return a[0];
        return tm - t[i - 1] <= t[i] - tm ? a[i - 1] : a[i];
    }

    /// <summary>가장 가까운 샘플 시각으로 맞춘다.</summary>
    public static double Snap(double[] t, double tm)
    {
        int i = LowerBound(t, tm);
        if (i <= 0) return t[0];
        if (i >= t.Length) return t[^1];
        return tm - t[i - 1] <= t[i] - tm ? t[i - 1] : t[i];
    }

    /// <summary>[lo, hi] 구간 샘플의 평균 (NaN 제외).</summary>
    public static double MeanBetween(double[] t, double[] a, double lo, double hi)
    {
        double s = 0; int k = 0;
        for (int i = LowerBound(t, lo); i < t.Length && t[i] <= hi; i++)
        {
            var v = a[i];
            if (!double.IsNaN(v)) { s += v; k++; }
        }
        return k > 0 ? s / k : double.NaN;
    }

    /// <summary>화면 구간 [x0, x1]을 덮는 인덱스 범위 (양쪽 한 샘플씩 여유).</summary>
    public static (int I0, int I1) VisibleRange(double[] t, double x0, double x1)
    {
        int n = t.Length;
        return (Math.Max(0, LowerBound(t, x0) - 1), Math.Min(n - 1, LowerBound(t, x1)));
    }

    /// <summary>인덱스 범위의 최소·최대 (NaN 제외). 값이 없으면 (+∞, −∞).</summary>
    public static (double Min, double Max) MinMax(double[] a, int i0, int i1)
    {
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        for (int i = i0; i <= i1; i++)
        {
            var v = a[i];
            if (v < mn) mn = v;
            if (v > mx) mx = v;
        }
        return (mn, mx);
    }

    /// <summary>표시 범위에 6% 여유를 준다. 값이 없으면 [0,1], 상수면 ±1.</summary>
    public static (double Lo, double Hi) Pad(double a, double b)
    {
        if (!double.IsFinite(a)) return (0, 1);
        if (a == b) { a -= 1; b += 1; }
        var d = (b - a) * .06;
        return (a - d, b + d);
    }

    /// <summary>
    /// 커서 키 이동: 현재 커서에서 dir(±1) 방향으로 step 샘플 이동한 시각.
    /// 커서가 샘플 사이에 있으면 첫 이동은 가까운 샘플로 맞춘다.
    /// </summary>
    public static double Step(double[] t, double cur, int dir, int step)
    {
        int i = LowerBound(t, cur);
        if (i < t.Length && t[i] == cur) i += dir * step;
        else i = dir > 0 ? i + (step - 1) : i - step;
        i = Math.Clamp(i, 0, t.Length - 1);
        return t[i];
    }
}
