using System.Windows.Media;
using DataAnalyzer.Core.Data;

namespace DataAnalyzer.App;

/// <summary>
/// 모든 차트가 함께 쓰는 상태: 데이터, 기준 데이터, 표시 구간(X), 커서 A/B.
/// 값이 바뀌면 <see cref="Changed"/>를 한 번(다음 렌더 프레임에) 알린다.
/// </summary>
public sealed class AppState
{
    public static readonly Color[] Palette =
        new[] { "#e6194b", "#3b82f6", "#16a34a", "#f59e0b", "#9333ea", "#0891b2", "#db2777", "#65a30d", "#ea580c", "#6366f1" }
        .Select(h => (Color)ColorConverter.ConvertFromString(h)).ToArray();
    public static readonly Color CursorAColor = (Color)ColorConverter.ConvertFromString("#10b981");
    public static readonly Color CursorBColor = (Color)ColorConverter.ConvertFromString("#f97316");

    public RawTable? Raw { get; private set; }
    public DataSet? Data { get; private set; }
    public string FileName { get; private set; } = "";
    public ReferenceData? Reference { get; private set; }
    public bool ShowReference { get; set; } = true;
    /// <summary>표시 중인 기준 데이터 (없거나 숨김이면 null)</summary>
    public ReferenceData? Ref => ShowReference ? Reference : null;

    public double ViewMin { get; private set; }
    public double ViewMax { get; private set; } = 1;
    public double FullMin { get; private set; }
    public double FullMax { get; private set; } = 1;
    public double MinSpan { get; private set; } = 1e-9;

    public double? CursorA { get; private set; }
    public double? CursorB { get; private set; }
    /// <summary>클릭이 커서 B를 움직이는지</summary>
    public bool SelectB { get; private set; }

    /// <summary>표시 구간·커서 등 화면만 바뀜</summary>
    public event Action? Changed;
    /// <summary>데이터 자체가 바뀜 (새 파일, 시간 열 변경, 기준 데이터 변경)</summary>
    public event Action? DataChanged;

    bool _pending;

    public Color ColorOf(string signal)
    {
        int i = Data?.Names.IndexOf(signal) ?? -1;
        return Palette[(i < 0 ? 0 : i) % Palette.Length];
    }

    public string UnitOf(string signal) => Data != null && Data.Units.TryGetValue(signal, out var u) ? u : "";

    public void Load(RawTable raw, string fileName, int timeColumn = 0)
    {
        Raw = raw;
        FileName = fileName;
        ApplyTimeColumn(timeColumn);
    }

    public void ApplyTimeColumn(int k)
    {
        if (Raw == null) return;
        Data = DataSet.FromRaw(Raw, k);
        CursorA = CursorB = null;
        SelectB = false;
        if (Reference != null)
        {
            var e = Reference.Rebuild(Raw, Data);
            if (e.Length > 0) { Reference = null; ReferenceMessage = "기준 Data 해제: " + e; }
        }
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
    }

    public string ReferenceMessage { get; set; } = "";

    /// <summary>기준 CSV를 연다. 실패하면 이유를 돌려준다.</summary>
    public string LoadReference(RawTable raw, string fileName)
    {
        if (Raw == null || Data == null) return "먼저 현재 CSV를 여세요";
        var r = ReferenceData.Create(raw, fileName, Raw, Data, out var err);
        if (r == null) return err;
        Reference = r;
        ReferenceMessage = "";
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
        return "";
    }

    public void ClearReference()
    {
        Reference = null;
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
    }

    public void SetReferenceOffset(double seconds)
    {
        if (Reference == null) return;
        Reference.SetOffset(seconds);
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
    }

    public void AlignReference()
    {
        if (Reference == null || Data == null) return;
        Reference.AlignStart(Data);
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
    }

    public void SetShowReference(bool on)
    {
        ShowReference = on;
        DataChanged?.Invoke();
        Notify();
    }

    /// <summary>데이터 전체 구간 (기준 데이터 포함)으로 표시 구간을 초기화</summary>
    void RecomputeFull()
    {
        if (Data == null || Data.Count == 0) { FullMin = 0; FullMax = 1; ViewMin = 0; ViewMax = 1; return; }
        FullMin = Data.Start; FullMax = Data.End;
        if (Reference != null && Reference.Time.Length > 0)
        {
            FullMin = Math.Min(FullMin, Reference.Time[0]);
            FullMax = Math.Max(FullMax, Reference.Time[^1]);
        }
        if (!(FullMax > FullMin)) FullMax = FullMin + 1;
        MinSpan = Math.Max((FullMax - FullMin) / Math.Max(1, Data.Count) * 3, 1e-9);
        ViewMin = FullMin; ViewMax = FullMax;
    }

    /// <summary>폭을 유지한 채 전체 구간 안으로 이동</summary>
    public void PanTo(double a, double b)
    {
        double s = b - a;
        if (a < FullMin) { a = FullMin; b = a + s; }
        if (b > FullMax) { b = FullMax; a = Math.Max(FullMin, b - s); }
        ViewMin = a; ViewMax = b;
        Notify();
    }

    /// <summary>tm을 기준으로 폭을 factor배로 확대/축소</summary>
    public void ZoomAt(double tm, double fraction, double factor)
    {
        double span = ViewMax - ViewMin;
        double ns = Math.Min(FullMax - FullMin, Math.Max(MinSpan, span * factor));
        PanTo(tm - fraction * ns, tm - fraction * ns + ns);
    }

    public void SetView(double a, double b)
    {
        if (double.IsNaN(a)) a = ViewMin;
        if (double.IsNaN(b)) b = ViewMax;
        a = Math.Max(a, FullMin); b = Math.Min(b, FullMax);
        if (b - a >= MinSpan) { ViewMin = a; ViewMax = b; }
        Notify();
    }

    public void ResetView() { ViewMin = FullMin; ViewMax = FullMax; Notify(); }

    public double Snap(double t) => Data == null || Data.Count == 0 ? t : Series.Snap(Data.Time, t);

    /// <summary>선택된 커서(A 또는 B)</summary>
    public double? SelectedCursor => SelectB && CursorB != null ? CursorB : CursorA;

    /// <summary>클릭 위치 t에서 가까운 커서를 고른다 (B가 켜져 있을 때).</summary>
    public void PickCursorNear(double t)
    {
        if (CursorA == null || CursorB == null) { SelectB = false; return; }
        SelectB = Math.Abs(t - CursorA.Value) > Math.Abs(t - CursorB.Value);
    }

    public void SetCursor(double t)
    {
        if (Data == null) return;
        t = Snap(Math.Min(FullMax, Math.Max(FullMin, t)));
        if (SelectB && CursorB != null) CursorB = t; else CursorA = t;
        Notify();
    }

    public void ToggleB()
    {
        if (Data == null) return;
        if (CursorB != null) { CursorB = null; SelectB = false; }
        else
        {
            double s = ViewMax - ViewMin;
            CursorA ??= Snap(ViewMin + s * .35);
            CursorB = Snap(Math.Min(FullMax, CursorA.Value + s * .3));
            SelectB = true;
        }
        Notify();
    }

    /// <summary>←/→ 키: 선택된 커서를 샘플 단위로 이동 (Shift = 10샘플)</summary>
    public void StepCursor(int dir, bool big)
    {
        if (Data == null || Data.Count == 0) return;
        var cur = SelectedCursor;
        if (cur == null) { SetCursor((ViewMin + ViewMax) / 2); return; }
        double tm = Series.Step(Data.Time, cur.Value, dir, big ? 10 : 1);
        if (tm < ViewMin || tm > ViewMax) { double s = ViewMax - ViewMin; PanTo(tm - s / 2, tm + s / 2); }
        SetCursor(tm);
    }

    public string TimeText(double t, double? span = null) => Format.Time(t, Data?.IsDate == true, span);

    public string CursorReadout()
    {
        if (CursorA == null) return "Cursor: —";
        var s = "A: " + TimeText(CursorA.Value);
        if (CursorB != null)
        {
            double d = CursorB.Value - CursorA.Value;
            s += " · B: " + TimeText(CursorB.Value) + " · ΔT: " +
                 (Data?.IsDate == true ? Format.Precision(d / 1000, 8) + " s" : Format.Precision(d, 8));
        }
        return s;
    }

    /// <summary>시계열 차트 칩에 표시할 값: A 값, 또는 A · B · Δ · 평균 (+ 기준 값)</summary>
    public string ValueText(string signal)
    {
        if (Data == null || CursorA == null || !Data.Signals.TryGetValue(signal, out var a)) return "—";
        var t = Data.Time;
        double va = Series.ValAt(t, a, CursorA.Value);
        string s;
        if (CursorB == null) s = Format.Value(va);
        else
        {
            double vb = Series.ValAt(t, a, CursorB.Value);
            double lo = Math.Min(CursorA.Value, CursorB.Value), hi = Math.Max(CursorA.Value, CursorB.Value);
            s = $"A {Format.Value(va)} · B {Format.Value(vb)} · Δ {Format.Value(vb - va)} · 평균 {Format.Value(Series.MeanBetween(t, a, lo, hi))}";
        }
        var r = Ref;
        if (r != null && r.Signals.ContainsKey(signal))
        {
            double ra = r.ValNear(signal, CursorA.Value);
            if (CursorB == null) s += $"  ┃ 기준 {Format.Value(ra)} · 차 {Format.Value(va - ra)}";
            else
            {
                double rb = r.ValNear(signal, CursorB.Value);
                s += $"  ┃ 기준 A {Format.Value(ra)} · B {Format.Value(rb)} · Δ {Format.Value(rb - ra)}";
            }
        }
        return s;
    }

    public void Clear()
    {
        Raw = null; Data = null; Reference = null; FileName = "";
        CursorA = CursorB = null; SelectB = false;
        RecomputeFull();
        DataChanged?.Invoke();
        Notify();
    }

    /// <summary>변경 알림을 다음 렌더 프레임에 한 번만 보낸다 (마우스 드래그 중 과도한 다시 그리기 방지).</summary>
    public void Notify()
    {
        if (_pending) return;
        _pending = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            new Action(() => { _pending = false; Changed?.Invoke(); }));
    }
}
