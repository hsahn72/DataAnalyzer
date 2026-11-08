using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DataAnalyzer.Core.Excavator;

namespace DataAnalyzer.App.Analysis;

/// <summary>Tip 궤적 마커 하나</summary>
public readonly record struct TipMarker(double X, double Z, string Shape, Color Color);

/// <summary>굴착기 측면 2D 뷰 (HTML exDrawSide). 드래그 = 이동, 휠 = 확대, 더블클릭 = 맞춤.</summary>
public sealed class ExcavatorSideView : FrameworkElement
{
    public Kinematics? G { get; set; }
    public PoseAtTime? Pose { get; set; }
    /// <summary>다른 커서 위치의 자세 (흐리게)</summary>
    public PoseAtTime? Ghost { get; set; }
    public List<TipMarker> Markers { get; } = new();
    /// <summary>Tip 외력 [kN] (Fx, Fy)과 전체 최대 |F|</summary>
    public (double Fx, double Fy, double Fmax)? Force { get; set; }
    public bool HasDynamics { get; set; }
    public bool ShowLabels { get; set; }
    public string Message { get; set; } = "";

    double _s, _ox, _oz; bool _fit = true; Point? _drag; double _dox, _doz;

    public ExcavatorSideView()
    {
        ClipToBounds = true; Focusable = false;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Fit(); return; }
            _drag = e.GetPosition(this); _dox = _ox; _doz = _oz; CaptureMouse();
        };
        MouseMove += (_, e) => { if (_drag is { } d) { var p = e.GetPosition(this); _fit = false; _ox = _dox + p.X - d.X; _oz = _doz + p.Y - d.Y; InvalidateVisual(); } };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseWheel += (_, e) =>
        {
            if (_s <= 0) return;
            var m = e.GetPosition(this); double f = Math.Exp(e.Delta * .0015); _fit = false;
            _ox = m.X - (m.X - _ox) * f; _oz = m.Y - (m.Y - _oz) * f; _s *= f; InvalidateVisual();
        };
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void Fit() { _fit = true; InvalidateVisual(); }

    static readonly Pen Outline = Freeze(new Pen(B("#111827"), 1.5));
    static SolidColorBrush B(string c) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return b; }
    static Pen Freeze(Pen p) { p.Freeze(); return p; }
    static readonly Typeface Face = new("Segoe UI");

    void Txt(DrawingContext g, string s, double x, double y, Brush b, double size = 11, bool bold = false, TextAlignment al = TextAlignment.Left, bool bottom = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? new Typeface(Face.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = al };
        g.DrawText(ft, new Point(x, bottom ? y - ft.Height : y));
    }

    protected override void OnRender(DrawingContext g)
    {
        double W = ActualWidth, H = ActualHeight;
        g.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
        if (W < 10 || H < 10) return;
        if (G == null) { Txt(g, "① Kinematic 파라미터를 입력하면 굴착기가 표시됩니다", W / 2, H / 2, Ui.Mute, 13, al: TextAlignment.Center); return; }
        double sc = G.P.Y / 1.5;
        if (_fit || _s <= 0)
        {
            double reach = G.Lb + G.La + G.Lk, x0 = -2.2 * sc - .3, x1 = G.P.X + reach + .5, z0 = -reach * .45, z1 = G.P.Y + reach * .85;
            _s = Math.Min(W / (x1 - x0), H / (z1 - z0)) * .94; _ox = W / 2 - _s * (x0 + x1) / 2; _oz = H / 2 + _s * (z0 + z1) / 2;
        }
        double s = _s, ox = _ox, oz = _oz;
        double X(double x) => ox + s * x; double Z(double z) => oz - s * z;
        Point Pt(V2 p) => new(X(p.X), Z(p.Y));

        // 격자 · 지면
        double gs = s > 38 ? 1 : s > 18 ? 2 : 5; var gp = new Pen(B("#E5E7EB"), 1);
        for (double x = Math.Ceiling(-ox / s / gs) * gs; X(x) < W; x += gs)
        {
            g.DrawLine(gp, new Point(X(x), 0), new Point(X(x), H));
            if (oz < H - 12) Txt(g, x + "m", X(x), Math.Min(H - 12, oz + 3), Ui.Mute, 10, al: TextAlignment.Center);
        }
        for (double z = Math.Ceiling((oz - H) / s / gs) * gs; Z(z) > 0; z += gs)
        {
            g.DrawLine(gp, new Point(0, Z(z)), new Point(W, Z(z)));
            if (z != 0) Txt(g, z + "m", 3, Z(z) - 12, Ui.Mute, 10);
        }
        g.DrawRectangle(new SolidColorBrush(Color.FromArgb(56, 139, 109, 75)), null, new Rect(0, Math.Max(0, oz), W, Math.Max(0, H - oz)));
        g.DrawLine(new Pen(B("#8b6d4b"), 2), new Point(0, oz), new Point(W, oz));

        // 차체
        void R(double a0, double b0, double a1, double b1, string c, double r) =>
            g.DrawRoundedRectangle(B(c), Outline, new Rect(X(a0 * sc), Z(b1 * sc), (a1 - a0) * sc * s, (b1 - b0) * sc * s), r * s * sc, r * s * sc);
        R(-1.9, 0, 1.9, .95, "#374151", .35); R(-1.9, 1.0, -1.1, 2.1, "#6b7280", .1); R(-1.7, .95, 1.2, 2.1, "#f5b301", .12); R(.1, 2.1, 1.1, 3.1, "#bfdbfe", .12);

        if (Ghost?.Fk is { } gh) DrawPose(g, gh, .25, Pt, s, sc);
        if (Pose?.Fk is { } p) DrawPose(g, p, 1, Pt, s, sc);

        foreach (var m in Markers) Shape(g, X(m.X), Z(m.Z), m.Shape, 4.5, m.Color);

        if (Pose?.Fk is { } q)
        {
            var t = Pt(q.Tip); var red = new Pen(B("#ef4444"), 2);
            g.DrawEllipse(null, red, t, 7, 7);
            g.DrawLine(red, new Point(t.X - 11, t.Y), new Point(t.X + 11, t.Y)); g.DrawLine(red, new Point(t.X, t.Y - 11), new Point(t.X, t.Y + 11));
            if (!HasDynamics) Txt(g, "Tip 외력: ③ [동역학 계산] 후 화살표가 표시됩니다", 8, H - 6, Ui.Mute, 11, bottom: true);
            else if (Force is { } f && double.IsFinite(f.Fx) && double.IsFinite(f.Fy) && f.Fmax > 0)
            {
                double k = Math.Min(W, H) * .3 / f.Fmax, F = Math.Sqrt(f.Fx * f.Fx + f.Fy * f.Fy); var col = B("#d946ef");
                Arrow(g, t.X, t.Y, f.Fx * k, -f.Fy * k, col, 3);
                Txt(g, $"F = {F:F1} kN  (Fx {f.Fx:F1}, Fy {f.Fy:F1})", 8, H - 6, col, 12, true, bottom: true);
            }
        }
        else if (Pose != null) Txt(g, "자세를 계산할 수 없습니다 — ② 신호 연결을 확인하세요", W / 2, 8, Ui.Err, 12, al: TextAlignment.Center);
        if (Message != "") Txt(g, Message, W / 2, H / 2, Ui.Mute, 12, al: TextAlignment.Center);
    }

    void DrawPose(DrawingContext g, Pose f, double alpha, Func<V2, Point> Pt, double s, double sc)
    {
        var G = this.G!;
        g.PushOpacity(alpha);
        void Line(V2 a, V2 b, double w, string c) =>
            g.DrawLine(new Pen(B(c), Math.Max(1.5, w * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Pt(a), Pt(b));
        void Cyl(V2 a, V2 b, int n = 1)
        {
            var m = new V2(a.X + (b.X - a.X) * .55, a.Y + (b.Y - a.Y) * .55); double k = 1 + .3 * (n - 1);
            Line(a, m, .17 * sc * k, "#4b5563"); Line(m, b, .09 * sc * k, "#d1d5db");
        }
        void Solid(List<V2> hh, string fill, string stroke)
        {
            if (hh.Count < 3) { Line(hh[0], hh[^1], .3 * sc, fill); return; }
            var geo = RoundPoly(hh.Select(Pt).ToList(), .16 * sc * s);
            g.DrawGeometry(B(fill), new Pen(B(stroke), Math.Max(1.5, .05 * sc * s)) { LineJoin = PenLineJoin.Round }, geo);
        }
        void Bar(V2 u, V2 v, double w, string fill, string stroke) { Line(u, v, w + .05 * sc, stroke); Line(u, v, w, fill); }

        double u0 = G.Lk / 1.5; int sd = G.P4.Y > 0 ? 1 : -1;
        var top = f.P2 + Kinematics.Rot(f.A[2], G.Lk * .62, sd * .95 * u0);
        V2 a0w = f.ArmCyl.Base, b1w = f.BoomCyl.Rod, a1w = f.ArmCyl.Rod;
        Solid(Body(new() { f.P0, f.P1, a0w }, new() { b1w }), "#f5b301", "#3b2a05");
        Solid(Body(new() { a1w, f.P1, f.P2, f.BkGP }, new() { f.BkC }), "#f5b301", "#3b2a05");
        Solid(Body(new() { f.P2, f.Tip, top, f.BkP4 }, new()), "#4b5563", "#111827");
        Cyl(f.BoomCyl.Base, f.BoomCyl.Rod, Dynamics.CylN(G.K, 0)); Cyl(f.ArmCyl.Base, f.ArmCyl.Rod); Cyl(f.BkC, f.BkJ);
        Bar(f.BkGP, f.BkJ, .11 * sc, "#0ea5e9", "#0c4a6e"); Bar(f.BkJ, f.BkP4, .11 * sc, "#a855f7", "#4c1d95");
        void Pin(V2 q, double r, string nm)
        {
            var c = Pt(q);
            g.DrawEllipse(B("#111827"), new Pen(Brushes.White, 1.5), c, Math.Max(3, r * sc * s), Math.Max(3, r * sc * s));
            if (ShowLabels && alpha >= 1) Txt(g, nm, c.X + 5, c.Y - 3, B("#111827"), 10, true, bottom: true);
        }
        foreach (var (q, nm) in new[] { (f.BoomCyl.Base, "P20"), (b1w, "P21"), (a0w, "P30"), (a1w, "P31"), (f.BkC, "P40"), (f.BkGP, "PGL"), (f.BkP4, "P41") }) Pin(q, .06, nm);
        Pin(f.BkJ, .07, "J"); Pin(f.P0, .11, "붐 핀"); Pin(f.P1, .11, "붐-암 핀"); Pin(f.P2, .11, "암-버켓 핀"); Pin(f.Tip, .05, "Tip");
        g.Pop();
    }

    static List<V2> Hull(List<V2> pts)
    {
        var q = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (q.Count < 3) return q;
        static double Cr(V2 o, V2 u, V2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        var lo = new List<V2>(); var up = new List<V2>();
        foreach (var t in q) { while (lo.Count >= 2 && Cr(lo[^2], lo[^1], t) <= 0) lo.RemoveAt(lo.Count - 1); lo.Add(t); }
        for (int i = q.Count - 1; i >= 0; i--) { var t = q[i]; while (up.Count >= 2 && Cr(up[^2], up[^1], t) <= 0) up.RemoveAt(up.Count - 1); up.Add(t); }
        up.RemoveAt(up.Count - 1); lo.RemoveAt(lo.Count - 1);
        return lo.Concat(up).ToList();
    }
    static bool InHull(List<V2> hh, V2 t) => hh.Count >= 3 && hh.Select((u, i) => (u, v: hh[(i + 1) % hh.Count])).All(e => (e.v.X - e.u.X) * (t.Y - e.u.Y) - (e.v.Y - e.u.Y) * (t.X - e.u.X) >= -1e-9);
    static List<V2> Body(List<V2> b, List<V2> extra)
    {
        var hh = Hull(b);
        foreach (var t in extra) if (!InHull(hh, t)) hh = Hull(b.Append(t).ToList());
        return hh;
    }

    static StreamGeometry RoundPoly(List<Point> S, double r)
    {
        var geo = new StreamGeometry(); int n = S.Count;
        using (var c = geo.Open())
        {
            for (int i = 0; i < n; i++)
            {
                Point u = S[(i + n - 1) % n], v = S[i], w = S[(i + 1) % n];
                double d1 = Math.Max(1e-9, (v - u).Length), d2 = Math.Max(1e-9, (w - v).Length), k1 = Math.Min(r, d1 / 2) / d1, k2 = Math.Min(r, d2 / 2) / d2;
                var p1 = new Point(v.X + (u.X - v.X) * k1, v.Y + (u.Y - v.Y) * k1); var p2 = new Point(v.X + (w.X - v.X) * k2, v.Y + (w.Y - v.Y) * k2);
                if (i == 0) c.BeginFigure(p1, true, true); else c.LineTo(p1, true, true);
                c.QuadraticBezierTo(v, p2, true, true);
            }
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>마커 도형 (HTML mkShape)</summary>
    public static void Shape(DrawingContext g, double x, double y, string shape, double r, Color color)
    {
        var b = new SolidColorBrush(color);
        switch (shape)
        {
            case "circle": g.DrawEllipse(b, null, new Point(x, y), r, r); return;
            case "square": g.DrawRectangle(b, null, new Rect(x - r, y - r, 2 * r, 2 * r)); return;
            case "cross":
                var p = new Pen(b, 3); g.DrawLine(p, new Point(x - r, y - r), new Point(x + r, y + r)); g.DrawLine(p, new Point(x + r, y - r), new Point(x - r, y + r)); return;
        }
        var pts = shape == "triangle" ? new[] { new Point(x, y - r * 1.2), new Point(x + r * 1.05, y + r * .8), new Point(x - r * 1.05, y + r * .8) }
            : new[] { new Point(x, y - r * 1.25), new Point(x + r * 1.25, y), new Point(x, y + r * 1.25), new Point(x - r * 1.25, y) };
        var geo = new StreamGeometry();
        using (var c = geo.Open()) { c.BeginFigure(pts[0], true, true); foreach (var q in pts.Skip(1)) c.LineTo(q, true, false); }
        g.DrawGeometry(b, null, geo);
    }

    /// <summary>흰 테두리 화살표 (HTML exArrow)</summary>
    public static void Arrow(DrawingContext g, double x, double y, double dx, double dy, Brush col, double w)
    {
        double L = Math.Sqrt(dx * dx + dy * dy); if (!(L > 1)) return;
        double ux = dx / L, uy = dy / L, h = Math.Min(12, L * .5), ex = x + dx, ey = y + dy;
        foreach (var (c, lw) in new[] { ((Brush)Brushes.White, w + 3), (col, w) })
        {
            g.DrawLine(new Pen(c, lw) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(x, y), new Point(ex - ux * h * .8, ey - uy * h * .8));
            var geo = new StreamGeometry();
            using (var s = geo.Open())
            {
                s.BeginFigure(new Point(ex, ey), true, true);
                s.LineTo(new Point(ex - ux * h - uy * h * .5, ey - uy * h + ux * h * .5), true, false);
                s.LineTo(new Point(ex - ux * h + uy * h * .5, ey - uy * h - ux * h * .5), true, false);
            }
            g.DrawGeometry(c, ReferenceEquals(c, Brushes.White) ? new Pen(c, 2) : null, geo);
        }
    }
}

/// <summary>상부 뷰: 선회각과 횡방향 Tip 외력 (HTML exTop)</summary>
public sealed class ExcavatorTopView : FrameworkElement
{
    public Kinematics? G { get; set; }
    public PoseAtTime? Pose { get; set; }
    /// <summary>선회각 [deg] (NaN = 없음)</summary>
    public double PsiDeg { get; set; } = double.NaN;
    /// <summary>횡방향 Tip 외력 [kN]과 최대 |F|</summary>
    public (double Flat, double Fmax)? Lateral { get; set; }

    public ExcavatorTopView() { ClipToBounds = true; SizeChanged += (_, _) => InvalidateVisual(); }

    static SolidColorBrush B(string c) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return b; }

    void Txt(DrawingContext g, string s, double x, double y, Brush b, double size = 11, bool bold = false, TextAlignment al = TextAlignment.Left, bool bottom = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = al };
        g.DrawText(ft, new Point(x, bottom ? y - ft.Height : y));
    }

    protected override void OnRender(DrawingContext g)
    {
        double W = ActualWidth, H = ActualHeight;
        g.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
        if (W < 10 || H < 10) return;
        Txt(g, "상부 뷰 (선회각)", 8, 6, Ui.Mute, 11, true);
        if (G == null) { Txt(g, "① 파라미터 입력 필요", W / 2, H / 2, Ui.Mute, al: TextAlignment.Center); return; }
        bool have = double.IsFinite(PsiDeg); double psi = have ? PsiDeg * Kinematics.Rad : 0;
        double sc = G.P.Y / 1.5, reach = G.P.X + G.Lb + G.La + G.Lk, s = Math.Min(W, H) * .44 / Math.Max(reach, 3.5 * sc), cx = W / 2, cy = H / 2 + 4;
        Point T(double x, double y, double a) => new(cx + s * (x * -Math.Sin(a) + y * Math.Cos(a)), cy + s * (x * -Math.Cos(a) - y * Math.Sin(a)));
        double gs = reach > 12 ? 5 : reach > 5 ? 2 : 1; var grid = new Pen(B("#E5E7EB"), 1);
        for (double r = gs; r <= reach * 1.15; r += gs) { g.DrawEllipse(null, grid, new Point(cx, cy), r * s, r * s); Txt(g, r + "m", cx + r * s * .707 + 2, cy - r * s * .707 - 6, Ui.Mute, 10); }
        g.DrawLine(grid, new Point(cx, cy - reach * 1.15 * s), new Point(cx, cy + reach * .5 * s)); g.DrawLine(grid, new Point(cx - reach * 1.15 * s, cy), new Point(cx + reach * 1.15 * s, cy));
        g.DrawLine(new Pen(Ui.Mute, 1.2) { DashStyle = new DashStyle(new double[] { 5, 3 }, 0) }, new Point(cx, cy), new Point(cx, cy - reach * 1.1 * s));
        Txt(g, "0°", cx, cy - reach * 1.1 * s - 16, Ui.Mute, 10, al: TextAlignment.Center);
        foreach (var y in new[] { -1.2, 1.2 }) g.DrawRectangle(B("#374151"), new Pen(B("#111827"), 1.2), new Rect(cx + s * (y * sc - .3 * sc), cy - s * 1.9 * sc, .6 * sc * s, 3.8 * sc * s));
        void Poly((double, double)[] pts, Brush fill, Brush? stroke, double lw, double a)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open()) { c.BeginFigure(T(pts[0].Item1, pts[0].Item2, a), true, true); foreach (var p in pts.Skip(1)) c.LineTo(T(p.Item1, p.Item2, a), true, false); }
            g.DrawGeometry(fill, stroke == null ? null : new Pen(stroke, lw), geo);
        }
        double hw = 1.15 * sc;
        Poly(new[] { (-1.9 * sc, -hw), (1.2 * sc, -hw), (1.2 * sc, hw), (-1.9 * sc, hw) }, B("#f5b301"), B("#3b2a05"), 1.5, psi);
        Poly(new[] { (.2 * sc, -hw - .05 * sc), (1.1 * sc, -hw - .05 * sc), (1.1 * sc, -hw + .9 * sc), (.2 * sc, -hw + .9 * sc) }, B("#bfdbfe"), B("#111827"), 1, psi);
        g.DrawEllipse(B("#111827"), null, new Point(cx, cy), 3, 3);
        if (Pose?.Fk is { } f)
        {
            double[] xs = { G.P.X, f.P1.X, f.P2.X, f.Tip.X };
            void Ln(double u, double v, double w, string c) => g.DrawLine(new Pen(B(c), Math.Max(2, w * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, T(u, 0, psi), T(v, 0, psi));
            Ln(0, xs[0], .3 * sc, "#4b5563"); Ln(xs[0], xs[1], .52 * sc, "#3b2a05"); Ln(xs[0], xs[1], .45 * sc, "#f5b301"); Ln(xs[1], xs[2], .39 * sc, "#3b2a05"); Ln(xs[1], xs[2], .32 * sc, "#f5b301");
            double bw2 = .55 * sc;
            Poly(new[] { (xs[2], -bw2), (xs[3], -bw2), (xs[3], bw2), (xs[2], bw2) }, B("#4b5563"), B("#111827"), 1.5, psi);
            var tp = T(xs[3], 0, psi); g.DrawEllipse(null, new Pen(B("#ef4444"), 2), tp, 6, 6);
            if (Lateral is { } l && double.IsFinite(l.Flat) && l.Fmax > 0)
            {
                double k = Math.Min(W, H) * .3 / l.Fmax; var col = B("#d946ef");
                ExcavatorSideView.Arrow(g, tp.X, tp.Y, -Math.Cos(psi) * l.Flat * k, Math.Sin(psi) * l.Flat * k, col, 3);
                Txt(g, $"횡방향 F = {l.Flat:F1} kN", 8, H - 6, col, 12, true, bottom: true);
            }
        }
        if (have && Math.Abs(psi) > 1e-4)
        {
            double R = Math.Max(26, Math.Min(W, H) * .14);
            var a0 = new Point(cx, cy - R); var a1 = new Point(cx - R * Math.Sin(psi), cy - R * Math.Cos(psi));
            var geo = new StreamGeometry();
            using (var c = geo.Open()) { c.BeginFigure(a0, false, false); c.ArcTo(a1, new Size(R, R), 0, Math.Abs(psi) > Math.PI, psi > 0 ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true, false); }
            g.DrawGeometry(null, new Pen(B("#ef4444"), 2), geo);
        }
        Txt(g, have ? $"ψ = {PsiDeg:F1}°" : Pose != null ? "선회각 없음" : "—", W - 8, 6, have ? B("#ef4444") : Ui.Mute, 13, true, TextAlignment.Right);
        if (!have && Pose != null) Txt(g, "② 선회 각도 열 지정 (또는 ③ 선회 속도 + [각도 변환])", W - 8, 26, Ui.Mute, 10, al: TextAlignment.Right);
        Txt(g, "위=0°, 반시계 방향 +", W - 8, H - 6, Ui.Mute, 10, al: TextAlignment.Right, bottom: true);
    }
}
