using System.Windows.Media;
using DataAnalyzer.Core.Analysis;
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
        RaiseDataChanged();
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
        RaiseDataChanged();
        Notify();
        return "";
    }

    public void ClearReference()
    {
        Reference = null;
        RecomputeFull();
        RaiseDataChanged();
        Notify();
    }

    public void SetReferenceOffset(double seconds)
    {
        if (Reference == null) return;
        Reference.SetOffset(seconds);
        RecomputeFull();
        RaiseDataChanged();
        Notify();
    }

    public void AlignReference()
    {
        if (Reference == null || Data == null) return;
        Reference.AlignStart(Data);
        RecomputeFull();
        RaiseDataChanged();
        Notify();
    }

    public void SetShowReference(bool on)
    {
        ShowReference = on;
        RaiseDataChanged();
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
        // 겹쳐보기 중이면 상대 시간축을 유지
        _savedRange = null;
        ClearCycleCaches();
        if (OverlayOn)
        {
            _savedRange = (FullMin, FullMax, ViewMin, ViewMax);
            double m = Groups.MaxRel;
            FullMin = 0; FullMax = m > 0 ? m : 1; ViewMin = FullMin; ViewMax = FullMax;
        }
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
        if (Data == null || OverlayOn) return;
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

    public string TimeText(double t, double? span = null) =>
        OverlayOn ? Format.Precision(t, 6) : Format.Time(t, Data?.IsDate == true, span);

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

    /// <summary>계산한 신호를 데이터에 추가 (같은 이름이면 바꿈)하고 화면을 갱신한다.</summary>
    public void PutSignal(string name, double[] values, string unit = "")
    {
        if (Data == null) return;
        Data.Put(name, values, unit);
        RaiseDataChanged();
        Notify();
    }

    /// <summary>여러 신호를 한꺼번에 추가 (화면 갱신은 한 번)</summary>
    public void PutSignals(IEnumerable<(string Name, double[] Values, string Unit)> list)
    {
        if (Data == null) return;
        foreach (var (n, v, u) in list) Data.Put(n, v, u);
        RaiseDataChanged();
        Notify();
    }

    public void Clear()
    {
        Raw = null; Data = null; Reference = null; FileName = "";
        CursorA = CursorB = null; SelectB = false;
        RecomputeFull();
        RaiseDataChanged();
        Notify();
    }

    // ===================== Cycle / Action 거르기 · 겹쳐보기 =====================

    /// <summary>Cycle 번호 열 (자동 감지: Cycle, Cycle_No …)</summary>
    public string? CycleColumn { get; private set; }
    /// <summary>Action 열 (자동 감지: Action, Action_Code …)</summary>
    public string? ActionColumn { get; private set; }
    /// <summary>선택한 Cycle (null = 전체)</summary>
    public int? CycleSelection { get; private set; }
    /// <summary>선택한 Action (null = 전체)</summary>
    public int? ActionSelection { get; private set; }
    /// <summary>'Cycle 기준 View' 체크</summary>
    public bool OverlayChecked { get; private set; }
    /// <summary>겹침 표시: auto / ind(개별선) / band(평균±산포)</summary>
    public string OverlayMode { get; private set; } = "auto";
    /// <summary>개별선 자동 전환 기준 (이보다 많으면 평균±산포)</summary>
    public const int MaxIndividual = 15;

    double[]? CycArr => CycleColumn != null && Data != null && Data.Signals.TryGetValue(CycleColumn, out var a) ? a : null;
    double[]? ActArr => ActionColumn != null && Data != null && Data.Signals.TryGetValue(ActionColumn, out var a) ? a : null;

    readonly Dictionary<string, double[]> _maskedCache = new();
    readonly Dictionary<string, CycleBand> _bandCache = new();
    byte[]? _mask;
    (List<CycleGroup> List, double MaxRel)? _groups;
    (double Min, double Max, double VMin, double VMax)? _savedRange;
    double[]? _lastCyc, _lastAct;

    /// <summary>Cycle·Action으로 거르는 중인지</summary>
    public bool Filtering => Data != null && ((CycleSelection != null && CycArr != null) || (ActionSelection != null && ActArr != null));

    /// <summary>겹쳐보기 중인지 (체크했거나, Cycle 전체 + Action 하나를 골랐을 때)</summary>
    public bool OverlayOn
    {
        get
        {
            if (Data == null || CycArr == null) return false;
            bool want = OverlayChecked || (CycleSelection == null && ActionSelection != null && ActArr != null);
            return want && Groups.List.Count > 0;
        }
    }

    public (List<CycleGroup> List, double MaxRel) Groups =>
        _groups ??= CycArr == null || Data == null ? (new List<CycleGroup>(), 0)
            : CycleView.Groups(Data.Time, CycArr, ActArr, CycleSelection, ActionSelection, Data.IsDate ? 1e-3 : 1);

    public bool UseBand
    {
        get
        {
            int n = Groups.List.Count;
            return OverlayMode == "band" ? n > 1 : OverlayMode != "ind" && n > MaxIndividual;
        }
    }

    public CycleBand Band(string s)
    {
        if (!_bandCache.TryGetValue(s, out var b))
            _bandCache[s] = b = CycleView.Band(Data!.Signals[s], Groups.List, Groups.MaxRel);
        return b;
    }

    /// <summary>표시용 신호 값 (Cycle·Action으로 거르는 중이면 밖은 NaN)</summary>
    public double[] Arr(string s)
    {
        var a = Data!.Signals[s];
        if (!Filtering) return a;
        if (!_maskedCache.TryGetValue(s, out var o))
        {
            _mask ??= CycleView.Mask(Data.Count, CycArr, ActArr, CycleSelection, ActionSelection);
            _maskedCache[s] = o = CycleView.ApplyMask(a, _mask);
        }
        return o;
    }

    /// <summary>Action 코드 이름 (CSV에 문자열이 있었으면 그 이름)</summary>
    public string ActionLabel(int v)
    {
        IReadOnlyDictionary<int, string>? cats = null;
        if (ActionColumn != null && Raw != null && Raw.Categories.TryGetValue(ActionColumn, out var c)) cats = c;
        return CycleAnalysis.ActionLabel(v, cats);
    }

    public List<int> CycleValues => CycleView.CycleValues(CycArr);
    public List<int> ActionValues => CycleView.ActionValues(ActArr);

    public void SetCycleSelection(int? cyc, int? act, bool overlay)
    {
        CycleSelection = cyc; ActionSelection = act; OverlayChecked = overlay;
        ApplyCycleView();
    }

    public void SetOverlayMode(string mode)
    {
        OverlayMode = mode;
        DataChanged?.Invoke();
        Notify();
    }

    void ClearCycleCaches()
    {
        _maskedCache.Clear(); _bandCache.Clear(); _mask = null; _groups = null;
    }

    /// <summary>거르기·겹쳐보기 변경: 겹쳐보기면 X축을 상대 시간 [0, 최장 Cycle]로 바꾸고, 끄면 원래 구간으로 되돌린다.</summary>
    void ApplyCycleView()
    {
        ClearCycleCaches();
        if (Data != null)
        {
            if (OverlayOn)
            {
                _savedRange ??= (FullMin, FullMax, ViewMin, ViewMax);
                double m = Groups.MaxRel;
                FullMin = 0; FullMax = m > 0 ? m : 1; ViewMin = FullMin; ViewMax = FullMax;
                MinSpan = Math.Max(FullMax / Math.Max(1, Data.Count) * 3, 1e-9);
            }
            else if (_savedRange is { } sr)
            {
                FullMin = sr.Min; FullMax = sr.Max; ViewMin = sr.VMin; ViewMax = sr.VMax;
                MinSpan = Math.Max((FullMax - FullMin) / Math.Max(1, Data.Count) * 3, 1e-9);
                _savedRange = null;
            }
        }
        DataChanged?.Invoke();
        Notify();
    }

    /// <summary>데이터가 바뀜: Cycle·Action 열을 다시 찾고, 그 값이 바뀌었으면 선택을 전체로 되돌린다.</summary>
    void RaiseDataChanged()
    {
        ClearCycleCaches();
        CycleColumn = Data == null ? null : CycleView.FindCycleColumn(Data.Names);
        ActionColumn = Data == null ? null : CycleView.FindActionColumn(Data.Names);
        if (!ReferenceEquals(CycArr, _lastCyc) || !ReferenceEquals(ActArr, _lastAct))
        {
            _lastCyc = CycArr; _lastAct = ActArr;
            if (CycleSelection != null || ActionSelection != null || OverlayChecked)
            {
                CycleSelection = null; ActionSelection = null; OverlayChecked = false;
                CycleSelectionReset?.Invoke();
            }
        }
        if (_savedRange is { } sr && !OverlayOn)
        {
            // 선택이 풀려 겹쳐보기가 꺼짐: 원래 시간축으로
            FullMin = sr.Min; FullMax = sr.Max; ViewMin = sr.VMin; ViewMax = sr.VMax;
            _savedRange = null;
        }
        DataChanged?.Invoke();
    }

    /// <summary>데이터가 바뀌어 Cycle·Action 선택이 전체로 돌아갔을 때 (콤보 갱신용)</summary>
    public event Action? CycleSelectionReset;

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
