namespace SwitchMonitoring.Forms;

public class PortGraphForm : Form
{
    private readonly string _switchName;
    private readonly string _switchIp;
    private readonly int _ifIndex;
    private readonly Queue<(DateTime ts,double inBps,double outBps)> _points = new();
    private readonly int _maxPoints;
    private readonly Panel _canvas;
    private readonly Label _legend;
    private double _lastMaxY = 0;
    private const double ScaleGrowFactor = 1.05; // slightly smaller hysteresis expansion
    private const double ScaleShrinkThreshold = 0.60; // shrink when current peak < threshold * last
    private const double StaticHeadroomFactor = 1.30; // always add 30% headroom to reduce "zoomed in" look
    private bool _showTotal = true;
    private bool _smoothing = false;
    private readonly ContextMenuStrip _ctx;
    // (Hover fjernet etter ønske)

    public PortGraphForm(string switchName, string switchIp, int ifIndex, int maxPoints = 240)
    {
        _switchName = switchName; _switchIp = switchIp; _ifIndex = ifIndex; _maxPoints = maxPoints;
        Text = $"Graf - {_switchName} ifIndex={_ifIndex}";
        Width = 1000; Height = 520;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;

    _legend = new Label{ Dock = DockStyle.Top, Height = 26, Text = "In (blå) | Out (oransje) | Total (grå)", ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6,0,0,0) };
    _canvas = new BufferedPanel{ Dock = DockStyle.Fill, BackColor = Color.FromArgb(20,20,24) };
    _canvas.Paint += CanvasOnPaint; // ingen hover events lenger
        _canvas.ContextMenuStrip = _ctx = BuildContext();
        Controls.Add(_canvas);
        Controls.Add(_legend);
    }

    private ContextMenuStrip BuildContext()
    {
        var m = new ContextMenuStrip();
        var totalItem = new ToolStripMenuItem("Vis total (areal)") { Checked = _showTotal, CheckOnClick = true };
        totalItem.CheckedChanged += (s,e) => { _showTotal = totalItem.Checked; _canvas.Invalidate(); };
        var smoothItem = new ToolStripMenuItem("Glatting (MA3)") { Checked = _smoothing, CheckOnClick = true };
        smoothItem.CheckedChanged += (s,e) => { _smoothing = smoothItem.Checked; _canvas.Invalidate(); };
        m.Items.Add(totalItem);
        m.Items.Add(smoothItem);
        return m;
    }

    public void AddSample(DateTime ts, double? inBps, double? outBps)
    {
        if (!inBps.HasValue && !outBps.HasValue) return;
        _points.Enqueue((ts, inBps ?? 0, outBps ?? 0));
        while (_points.Count > _maxPoints) _points.Dequeue();
        _canvas.Invalidate();
    }

    private void CanvasOnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_points.Count < 2)
        {
            g.DrawString("For lite data...", Font, Brushes.Gray, 10, 10);
            return;
        }
    var arr = _points.ToArray();
        var minT = arr.First().ts;
        var maxT = arr.Last().ts;
        var totalSeconds = (maxT - minT).TotalSeconds;
        if (totalSeconds <= 0) totalSeconds = 1;
        // Compute total and find max across total also
        double observedMax = arr.Select(p => p.inBps + p.outBps).Concat(new[]{arr.Max(p=>p.inBps), arr.Max(p=>p.outBps)}).Max();
        if (observedMax <= 0) observedMax = 1;
        // Hysterese: justér _lastMaxY
        if (_lastMaxY <= 0) _lastMaxY = observedMax;
        if (observedMax > _lastMaxY) _lastMaxY = observedMax * ScaleGrowFactor;
        else if (observedMax < _lastMaxY * ScaleShrinkThreshold)
            _lastMaxY = observedMax * ScaleGrowFactor;
        double maxVal = NiceCeiling(_lastMaxY * StaticHeadroomFactor); // fast headroom
        var rect = new Rectangle(55, 20, _canvas.Width - 90, _canvas.Height - 70);
        using var axisPen = new Pen(Color.DimGray,1);
        // background gradient subtle
        using (var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.FromArgb(28,28,32), Color.FromArgb(18,18,22), 90f))
        {
            g.FillRectangle(bgBrush, rect);
        }
        g.DrawRectangle(axisPen, rect);
        int horizontalLines = 5;
        for (int i=0;i<=horizontalLines;i++)
        {
            float frac = i/(float)horizontalLines;
            int y = rect.Top + (int)(rect.Height * frac);
            g.DrawLine(Pens.DimGray, rect.Left, y, rect.Right, y);
            double val = maxVal * (1 - frac);
            g.DrawString(FormatBps(val), Font, Brushes.Gainsboro, 2, y-8);
        }
        // Plot lines
        PointF[] inPts = new PointF[arr.Length];
        PointF[] outPts = new PointF[arr.Length];
        PointF[] totalPts = new PointF[arr.Length+2]; // for fill polygon (start + end base)
        // Optional smoothing (simple moving average length 3)
        double Smooth(double current, int i, Func<(DateTime ts,double inBps,double outBps), double> sel)
        {
            if (!_smoothing) return sel(arr[i]);
            double sum = 0; int c=0;
            for (int k = Math.Max(0,i-1); k <= Math.Min(arr.Length-1,i+1); k++){ sum += sel(arr[k]); c++; }
            return sum / c;
        }
        for (int i=0;i<arr.Length;i++)
        {
            var p = arr[i];
            double xFrac = (p.ts - minT).TotalSeconds / totalSeconds;
            float x = rect.Left + (float)(xFrac * rect.Width);
            double inVal = Smooth(p.inBps, i, q=>q.inBps);
            double outVal = Smooth(p.outBps, i, q=>q.outBps);
            float yIn = rect.Bottom - (float)(inVal / maxVal * rect.Height);
            float yOut = rect.Bottom - (float)(outVal / maxVal * rect.Height);
            float yTotal = rect.Bottom - (float)((inVal + outVal)/ maxVal * rect.Height);
            inPts[i] = new PointF(x,yIn);
            outPts[i] = new PointF(x,yOut);
            totalPts[i+1] = new PointF(x,yTotal);
        }
        // close polygon
        totalPts[0] = new PointF(inPts.First().X, rect.Bottom);
        totalPts[^1] = new PointF(inPts.Last().X, rect.Bottom);
        using var inPen = new Pen(Color.DeepSkyBlue,2f);
        using var outPen = new Pen(Color.Orange,2f);
        using var totalLinePen = new Pen(Color.LightGray,1.5f){ DashStyle = System.Drawing.Drawing2D.DashStyle.Dash};
        using var totalFillBrush = new SolidBrush(Color.FromArgb(60, 160,160,160));
        if (_showTotal && totalPts.Length > 3)
            g.FillPolygon(totalFillBrush, totalPts);
        if (inPts.Length > 1) g.DrawLines(inPen, inPts);
        if (outPts.Length > 1) g.DrawLines(outPen, outPts);
    if (_showTotal && totalPts.Length > 3) g.DrawLines(totalLinePen, totalPts.Skip(1).Take(totalPts.Length-2).ToArray());
        // min/max markers for total
        var maxPoint = arr.MaxBy(p => p.inBps + p.outBps);
        var minPoint = arr.MinBy(p => p.inBps + p.outBps);
        if (maxPoint.ts != DateTime.MinValue)
        {
            float xMax = rect.Left + (float)((maxPoint.ts - minT).TotalSeconds / totalSeconds * rect.Width);
            float yMax = rect.Bottom - (float)((maxPoint.inBps + maxPoint.outBps)/maxVal * rect.Height);
            DrawMarker(g, xMax, yMax, $"Max {FormatBps(maxPoint.inBps + maxPoint.outBps)}", Font, Color.LightGray);
        }
        if (minPoint.ts != DateTime.MinValue)
        {
            float xMin = rect.Left + (float)((minPoint.ts - minT).TotalSeconds / totalSeconds * rect.Width);
            float yMin = rect.Bottom - (float)((minPoint.inBps + minPoint.outBps)/maxVal * rect.Height);
            DrawMarker(g, xMin, yMin, $"Min {FormatBps(minPoint.inBps + minPoint.outBps)}", Font, Color.LightGray, false);
        }
        // X-axis time labels (left, mid, right)
        g.DrawString(minT.ToString("HH:mm:ss"), Font, Brushes.Gainsboro, rect.Left, rect.Bottom + 4);
        g.DrawString(maxT.ToString("HH:mm:ss"), Font, Brushes.Gainsboro, rect.Right - 70, rect.Bottom + 4);
        var midT = minT + TimeSpan.FromSeconds(totalSeconds/2);
        g.DrawString(midT.ToString("HH:mm:ss"), Font, Brushes.Gainsboro, rect.Left + rect.Width/2 - 35, rect.Bottom + 4);
        // Latest labels
        var last = arr.Last();
        double avgTotal = arr.Average(p => p.inBps + p.outBps);
        g.DrawString($"In {FormatBps(last.inBps)}", Font, Brushes.DeepSkyBlue, rect.Right - 140, rect.Top + 4);
        g.DrawString($"Out {FormatBps(last.outBps)}", Font, Brushes.Orange, rect.Right - 140, rect.Top + 20);
        g.DrawString($"Tot {FormatBps(last.inBps + last.outBps)} (Avg {FormatBps(avgTotal)})", Font, Brushes.LightGray, rect.Right - 220, rect.Top + 36);

        // (Ingen hover overlay lenger)
    }
    // (CanvasOnMouseMove fjernet)

    private class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            this.UpdateStyles();
        }
    }

    private static string FormatBps(double v)
    {
        if (v >= 1_000_000_000) return $"{v/1_000_000_000:0.00}G";
        if (v >= 1_000_000) return $"{v/1_000_000:0.00}M";
        if (v >= 1_000) return $"{v/1_000:0.00}K";
        return v.ToString("0");
    }

    private static double NiceCeiling(double value)
    {
        // Determine nice rounded ceiling (1,2,5 * 10^n)
        double exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double mant = value / exp;
        double niceMant = mant <= 1 ? 1 : mant <= 2 ? 2 : mant <= 5 ? 5 : 10;
        return niceMant * exp;
    }

    private static void DrawMarker(Graphics g, float x, float y, string text, Font font, Color color, bool above = true)
    {
        int dy = above ? -18 : 4;
        using var pen = new Pen(color,1f);
        g.DrawLine(pen, x, y, x, y + dy/2f);
        var size = g.MeasureString(text, font);
        float tx = x - size.Width/2;
        float ty = y + dy - size.Height/2;
        using var bg = new SolidBrush(Color.FromArgb(160, 32,32,36));
        g.FillRectangle(bg, tx-4, ty-2, size.Width+8, size.Height+4);
        g.DrawRectangle(pen, tx-4, ty-2, size.Width+8, size.Height+4);
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, tx, ty);
    }

    public bool Matches(string swIp, int ifIndex) => string.Equals(swIp, _switchIp, StringComparison.OrdinalIgnoreCase) && ifIndex == _ifIndex;
}
