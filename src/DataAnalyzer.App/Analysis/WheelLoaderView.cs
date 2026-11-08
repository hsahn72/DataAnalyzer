using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DataAnalyzer.Core.Excavator;
using DataAnalyzer.Core.WheelLoader;

namespace DataAnalyzer.App.Analysis;

/// <summary>휠로더 Z-bar 형상 2D 뷰 (HTML wlPreview 그림). 휠 = 확대, 드래그 = 이동, 더블클릭 = 맞춤.</summary>
public sealed class WheelLoaderView : FrameworkElement
{
    public WlGeom? G { get; set; }
    public WlPose? Pose { get; set; }
    /// <summary>핀 반력 [N] (없으면 표시 안 함)과 화살표 스케일 [px/kN]</summary>
    public WlPinForces? Pins { get; set; }
    public double PinScale { get; set; } = 1;
    public string Message { get; set; } = "";

    double _s, _ox, _oy; bool _fit = true; Point? _drag; double _dox, _doy;

    public WheelLoaderView()
    {
        ClipToBounds = true;
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Fit(); return; } _drag = e.GetPosition(this); _dox = _ox; _doy = _oy; CaptureMouse(); };
        MouseMove += (_, e) => { if (_drag is { } d) { var p = e.GetPosition(this); _fit = false; _ox = _dox + p.X - d.X; _oy = _doy + p.Y - d.Y; InvalidateVisual(); } };
        MouseLeftButtonUp += (_, _) => { _drag = null; ReleaseMouseCapture(); };
        MouseWheel += (_, e) =>
        {
            if (_s <= 0) return; var m = e.GetPosition(this); double f = Math.Exp(e.Delta * .0015), ns = Math.Min(1e4, Math.Max(1, _s * f)), k = ns / _s; _fit = false;
            _ox = m.X - (m.X - _ox) * k; _oy = m.Y - (m.Y - _oy) * k; _s = ns; InvalidateVisual();
        };
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void Fit() { _fit = true; InvalidateVisual(); }

    static SolidColorBrush B(string c) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c)); b.Freeze(); return b; }
    void Txt(DrawingContext g, string s, double x, double y, Brush b, double size = 11)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        g.DrawText(ft, new Point(x, y));
    }

    protected override void OnRender(DrawingContext g)
    {
        double W = ActualWidth, H = ActualHeight;
        g.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
        if (W < 10 || H < 10) return;
        if (G == null || Pose == null || Pose.Bad) { Txt(g, Message != "" ? Message : "형상을 조립할 수 없습니다", 10, 10, Ui.Err, 13); return; }
        var p = Pose;
        var pts = new[] { new V2(0, 0), G.P30, G.P40, p.Gw, p.B, p.P31, p.P41, p.Tip, p.PQ, p.PP };
        if (_fit || _s <= 0)
        {
            double x0 = pts.Min(q => q.X), x1 = pts.Max(q => q.X), y0 = pts.Min(q => q.Y), y1 = pts.Max(q => q.Y), m = 28;
            _s = Math.Min((W - 2 * m) / Math.Max(1e-9, x1 - x0), (H - 2 * m) / Math.Max(1e-9, y1 - y0)) * .8;
            _ox = W / 2 - _s * (x0 + x1) / 2; _oy = H / 2 + _s * (y0 + y1) / 2;
        }
        Point P(V2 q) => new(_ox + q.X * _s, _oy - q.Y * _s);
        void Ln(V2 a, V2 b, Brush c, double w) => g.DrawLine(new Pen(c, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, P(a), P(b));
        g.DrawLine(new Pen(B("#33999999"), 1), new Point(0, _oy), new Point(W, _oy));
        var O = new V2(0, 0);
        Ln(G.P30, p.P31, B("#6b7280"), 5); Ln(G.P40, p.PP, B("#6b7280"), 5);
        Ln(O, p.B, B("#2563eb"), 6); Ln(O, p.Gw, B("#2563eb"), 6); Ln(p.B, p.Gw, B("#2563eb"), 6);
        Ln(p.B, p.Tip, B("#ea580c"), 5); Ln(p.B, p.P41, B("#ea580c"), 3); Ln(p.P41, p.Tip, B("#55ea580c"), 2);
        Ln(p.Gw, p.PP, B("#16a34a"), 4); Ln(p.Gw, p.PQ, B("#16a34a"), 4); Ln(p.PQ, p.P41, B("#a855f7"), 3);
        if (Pins is { } fp)
        {
            var col = B("#2b8cc4"); var pos = new Dictionary<string, V2> { ["P40"] = G.P40, ["PGL"] = p.Gw, ["PP"] = p.PP, ["PQ"] = p.PQ, ["P41"] = p.P41 };
            double sc = PinScale / 1e3;
            foreach (var k in WlPinForces.Keys)
            {
                var f = fp[k]; double m = f.Length; if (!(m > 1)) continue;
                var a = P(pos[k]); double dx = f.X * sc, dy = -f.Y * sc;
                ExcavatorSideView.Arrow(g, a.X, a.Y, dx, dy, col, 1.6);
                Txt(g, (m / 1e3).ToString("F1", CultureInfo.InvariantCulture) + " kN", a.X + dx + 4, a.Y + dy + (dy >= 0 ? 0 : -14), col, 10);
            }
        }
        void Dot(V2 q, string l, Brush c) { var a = P(q); g.DrawEllipse(c, null, a, 4, 4); Txt(g, l, a.X + 6, a.Y - 17, Brushes.Black, 11); }
        var k1 = B("#111111"); var k2 = B("#e11d48");
        Dot(O, "O", k1); Dot(G.P30, "P30", k1); Dot(G.P40, "P40", k1);
        foreach (var (q, l) in new[] { (p.Gw, "PGL"), (p.B, "B"), (p.P31, "P31"), (p.P41, "P41"), (p.PQ, "PQ"), (p.PP, "PP"), (p.Tip, "Tip") }) Dot(q, l, k2);
    }
}
