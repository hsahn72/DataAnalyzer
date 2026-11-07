namespace DataAnalyzer.Core.Analysis;

/// <summary>
/// 위상지연 없는 필터 (HTML 뷰어의 Filter와 같은 계산).
/// 이동평균은 중심 대칭 창, Low-pass는 Butterworth 2차를 정·역방향으로 적용한다.
/// </summary>
public static class ZeroPhaseFilter
{
    /// <summary>샘플 간격의 중앙값으로 구한 샘플링 주파수 [Hz]. 시간 단위는 초.</summary>
    public static double SampleRate(double[] timeSec)
    {
        var d = new List<double>();
        for (int i = 1; i < timeSec.Length; i++) { double x = timeSec[i] - timeSec[i - 1]; if (x > 0) d.Add(x); }
        if (d.Count == 0) return double.NaN;
        d.Sort();
        return 1 / d[d.Count >> 1];
    }

    /// <summary>NaN을 선형 보간으로 채운다 (앞뒤 끝은 가장 가까운 값). 값이 하나도 없으면 null.</summary>
    public static double[]? FillGaps(double[] a)
    {
        int n = a.Length, last = -1;
        var o = (double[])a.Clone();
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(o[i])) continue;
            if (last >= 0 && i - last > 1)
                for (int k = last + 1; k < i; k++) o[k] = o[last] + (o[i] - o[last]) * (k - last) / (i - last);
            else if (last < 0)
                for (int k = 0; k < i; k++) o[k] = o[i];
            last = i;
        }
        if (last < 0) return null;
        for (int k = last + 1; k < n; k++) o[k] = o[last];
        return o;
    }

    /// <summary>창 크기 보정: 1 이상, 홀수.</summary>
    public static int OddWindow(double n)
    {
        int w = Math.Max(1, (int)Math.Round(n, MidpointRounding.AwayFromZero));
        return w % 2 == 0 ? w + 1 : w;
    }

    /// <summary>중심 대칭 이동평균. 양 끝은 창을 줄여 대칭을 유지한다.</summary>
    public static double[] MovingAverage(double[] a, double window)
    {
        int N = OddWindow(window), n = a.Length, h = N >> 1;
        var ps = new double[n + 1];
        for (int i = 0; i < n; i++) ps[i + 1] = ps[i] + a[i];
        var o = new double[n];
        for (int i = 0; i < n; i++)
        {
            int k = Math.Min(h, Math.Min(i, n - 1 - i));
            o[i] = (ps[i + k + 1] - ps[i - k]) / (2 * k + 1);
        }
        return o;
    }

    /// <summary>
    /// Butterworth 2차 Low-pass, 정·역방향 (filtfilt). fc = 최종 −3 dB 주파수.
    /// 두 번 적용으로 −6 dB가 되는 것을 보정하려고 설계 주파수를 1.2472배로 올린다.
    /// 끝단은 반사 패딩(2·끝값 − 안쪽 값)으로 과도 응답을 줄인다.
    /// </summary>
    public static double[] LowPass(double[] a, double fc, double fs)
    {
        int n = a.Length;
        if (n == 0) return Array.Empty<double>();
        fc = Math.Min(fc, fs * .45);
        double fd = Math.Min(fc * 1.2472, fs * .49), wc = Math.Tan(Math.PI * fd / fs), k = Math.Sqrt(2);
        double nm = 1 / (1 + k * wc + wc * wc), b0 = wc * wc * nm, b1 = 2 * b0, a1 = 2 * (wc * wc - 1) * nm, a2 = (1 - k * wc + wc * wc) * nm;
        int pad = Math.Min(n - 1, Math.Max(20, (int)Math.Round(3 * fs / fc, MidpointRounding.AwayFromZero)));
        int m = n + 2 * pad;
        var x = new double[m];
        for (int i = 0; i < pad; i++)
        {
            x[pad - 1 - i] = 2 * a[0] - a[Math.Min(n - 1, i + 1)];
            x[pad + n + i] = 2 * a[n - 1] - a[Math.Max(0, n - 2 - i)];
        }
        Array.Copy(a, 0, x, pad, n);
        double[] Pass(double[] v)
        {
            var y = new double[m];
            double x1 = v[0], x2 = v[0], y1 = v[0], y2 = v[0];
            for (int i = 0; i < m; i++)
            {
                double xi = v[i], yi = b0 * xi + b1 * x1 + b0 * x2 - a1 * y1 - a2 * y2;
                y[i] = yi; x2 = x1; x1 = xi; y2 = y1; y1 = yi;
            }
            return y;
        }
        var f = Pass(x); Array.Reverse(f);
        var g = Pass(f); Array.Reverse(g);
        return g[pad..(pad + n)];
    }

    /// <summary>
    /// 신호에 필터 적용. 계산은 NaN을 보간해서 하고, 원래 NaN이던 자리는 NaN으로 되돌린다.
    /// 계산할 수 없으면 null.
    /// </summary>
    public static double[]? Apply(double[] signal, FilterKind kind, double param, double fs = double.NaN)
    {
        var f = FillGaps(signal);
        if (f == null) return null;
        double[] y;
        if (kind == FilterKind.MovingAverage) y = MovingAverage(f, param);
        else
        {
            if (!(fs > 0) || !(param > 0)) return null;
            y = LowPass(f, param, fs);
        }
        for (int i = 0; i < y.Length; i++) if (double.IsNaN(signal[i])) y[i] = double.NaN;
        return y;
    }

    /// <summary>결과 신호 이름 (HTML과 동일: 이름_MA11, 이름_LP5Hz)</summary>
    public static string ResultName(string signal, FilterKind kind, double param) =>
        kind == FilterKind.MovingAverage
            ? signal + "_MA" + OddWindow(param)
            : signal + "_LP" + Data.Format.Precision(param, 15) + "Hz";
}

public enum FilterKind { MovingAverage, LowPass }
