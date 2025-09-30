using SwitchMonitoring.Models;
using SwitchMonitoring.Services;

namespace SwitchMonitoring.Forms;

public class HistoricalGraphForm : Form
{
    private readonly SwitchMonitor _monitor;
    private readonly string _switchIp;
    private readonly int _ifIndex;
    private readonly string _title;
    private readonly Panel _canvas;
    private List<InterfaceSnapshot> _history = new();
    private System.Windows.Forms.Timer _refreshTimer = new();
    private int _minutesWindow;
    private bool _includeTotal = true;
    private bool _smoothing = false;
    private readonly Label _titleLabel = null!; // initialized in ctor
    private bool _futurePadding = true; // 50 years forward axis padding
    private const double FutureYears = 50.0;
    // Hover / rendering state (custom drawn instead of ToolTip to prevent flicker)
    private DateTime _lastMinT;
    private DateTime _lastDrawMaxT;
    private Rectangle _lastRect;
    // _hoverIndex fjernet (tidligere brukt til snapping)
    private float _hoverX = -1;   // pixel x-pos for crosshair
    private float _lastHoverPixelX = -999; // for throttling av redraw
    private bool _hoverActive = false;
    private DateTime _hoverTimeUtc; // kontinuerlig tid for interpolasjon
    // Interpolasjon / visning utvidelser
    private bool _stepInterpolation = false; // step (discrete) vs linear interpolation
    private double? _easedHoverY = null; // easing buffer (total bps)
    private double _easingFactor = 0.25; // dynamic (Low=0.15, Medium=0.25, High=0.40)
    private const double EaseLow = 0.15;
    private const double EaseMed = 0.25;
    private const double EaseHigh = 0.40;
    private double? _lastDeltaValue = null; // for trend arrows
    private double _deltaHighlightThresholdPct = 25; // threshold for strong colors
    public HistoricalGraphForm(SwitchMonitor monitor, string switchIp, int ifIndex, string title, int minutesWindow = 120)
    {
        _monitor = monitor; _switchIp = switchIp; _ifIndex = ifIndex; _title = title;
    // Preferences: override with saved value unless an explicit minutesWindow was passed
        if (minutesWindow != 120)
            _minutesWindow = minutesWindow;
        else
            _minutesWindow = UserPreferences.Current.HistoricalWindowMinutes;
    _smoothing = UserPreferences.Current.SmoothingEnabled;
    _futurePadding = UserPreferences.Current.FuturePaddingEnabled;
    _easingFactor = UserPreferences.Current.EasingFactor;
    _deltaHighlightThresholdPct = UserPreferences.Current.DeltaHighlightThresholdPct;
    _stepInterpolation = UserPreferences.Current.StepInterpolation;
    _includeTotal = UserPreferences.Current.IncludeTotalArea;
    Text = $"History - {title}";
        Width = 1000; Height = 520;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;
        var top = new Panel{Dock=DockStyle.Top,Height=32,Padding=new Padding(6,4,6,4)};
    _titleLabel = new Label{Text=$"{title} (last {_minutesWindow} min)",Dock=DockStyle.Left,Width=400,ForeColor=Color.White};
        var winBox = new ComboBox{Dock=DockStyle.Right,Width=100,DropDownStyle=ComboBoxStyle.DropDownList};
    // minutes choices + larger windows (1d,7d,30d,180d,365d)
    winBox.Items.AddRange(new object[]{"30","60","120","240","480","1440","10080","43200","259200","525600"});
        winBox.SelectedItem = _minutesWindow.ToString();
        top.Controls.Add(winBox);
        top.Controls.Add(_titleLabel);
    _canvas = new BufferedPanel{Dock=DockStyle.Fill, BackColor = Color.FromArgb(20,20,24)};
        _canvas.MouseMove += CanvasOnMouseMove;
    _canvas.MouseLeave += (s,e)=>{ _hoverActive=false; _hoverX=-1; _easedHoverY=null; _canvas.Invalidate(); };
        // Event etter at _canvas er opprettet
        winBox.SelectedIndexChanged += (s,e)=>
        {
            var sel = winBox.SelectedItem;
            if (sel != null && int.TryParse(sel.ToString(), out var m))
            {
                _minutesWindow = m;
                UserPreferences.Current.HistoricalWindowMinutes = m;
                UserPreferences.Save();
                RefreshHistory();
                _canvas.Invalidate();
                _titleLabel.Text = $"{title} (last {_minutesWindow} min)";
            }
        };
        _canvas.Paint += CanvasOnPaint;
        var ctx = new ContextMenuStrip();
        var totalItem = new ToolStripMenuItem("Show total area"){Checked=_includeTotal,CheckOnClick=true}; totalItem.CheckedChanged += (s,e)=>{_includeTotal=totalItem.Checked; UserPreferences.Current.IncludeTotalArea=_includeTotal; UserPreferences.Save(); _canvas.Invalidate();};
        var smoothItem = new ToolStripMenuItem("Smoothing (MA5)"){Checked=_smoothing,CheckOnClick=true}; smoothItem.CheckedChanged += (s,e)=>{_smoothing=smoothItem.Checked; UserPreferences.Current.SmoothingEnabled=_smoothing; UserPreferences.Save(); _canvas.Invalidate();};
        var futureItem = new ToolStripMenuItem("Future +50 years"){Checked=_futurePadding,CheckOnClick=true}; futureItem.CheckedChanged += (s,e)=>{_futurePadding=futureItem.Checked; UserPreferences.Current.FuturePaddingEnabled=_futurePadding; UserPreferences.Save(); _canvas.Invalidate();};
    var interpItem = new ToolStripMenuItem("Step mode (discrete)"){Checked=_stepInterpolation,CheckOnClick=true}; interpItem.CheckedChanged += (s,e)=>{ _stepInterpolation = interpItem.Checked; UserPreferences.Current.StepInterpolation=_stepInterpolation; UserPreferences.Save(); _easedHoverY=null; _canvas.Invalidate(); };
    // Easing menu
    var easingMenu = new ToolStripMenuItem("Easing speed");
    var lowItem = new ToolStripMenuItem("Low"){CheckOnClick=true,Checked=_easingFactor==EaseLow};
    var medItem = new ToolStripMenuItem("Medium"){CheckOnClick=true,Checked=_easingFactor==EaseMed};
    var highItem = new ToolStripMenuItem("High"){CheckOnClick=true,Checked=_easingFactor==EaseHigh};
    void SetEase(double f){ _easingFactor=f; UserPreferences.Current.EasingFactor=f; UserPreferences.Save(); _easedHoverY=null; lowItem.Checked = f==EaseLow; medItem.Checked=f==EaseMed; highItem.Checked=f==EaseHigh; _canvas.Invalidate(); }
    lowItem.Click += (s,e)=> SetEase(EaseLow);
    medItem.Click += (s,e)=> SetEase(EaseMed);
    highItem.Click += (s,e)=> SetEase(EaseHigh);
    easingMenu.DropDownItems.Add(lowItem);
    easingMenu.DropDownItems.Add(medItem);
    easingMenu.DropDownItems.Add(highItem);
    // Delta threshold menu
    var deltaMenu = new ToolStripMenuItem("Δ threshold");
    void SetDeltaThresh(double v){ _deltaHighlightThresholdPct = v; UserPreferences.Current.DeltaHighlightThresholdPct=v; UserPreferences.Save(); foreach(ToolStripMenuItem it in deltaMenu.DropDownItems) it.Checked = it.Tag is double dv && Math.Abs(dv - v) < 0.001; _canvas.Invalidate(); }
    ToolStripMenuItem MakeThresh(string text, double v){ var it = new ToolStripMenuItem(text){CheckOnClick=true, Tag=v, Checked=Math.Abs(_deltaHighlightThresholdPct - v) < 0.01}; it.Click += (s,e)=> SetDeltaThresh(v); return it; }
    deltaMenu.DropDownItems.Add(MakeThresh("10%",10));
    deltaMenu.DropDownItems.Add(MakeThresh("25%",25));
    deltaMenu.DropDownItems.Add(MakeThresh("50%",50));
    // Copy menu
    var copyMenu = new ToolStripMenuItem("Copy");
    var copyPoint = new ToolStripMenuItem("Current point (CSV)");
    copyPoint.Click += (s,e)=> CopyCurrentPoint();
    var copyVisible = new ToolStripMenuItem("Visible points (CSV)");
    copyVisible.Click += (s,e)=> CopyVisibleSeries();
    var exportVisible = new ToolStripMenuItem("Save visible (CSV file)");
    exportVisible.Click += (s,e)=> SaveVisibleSeriesToFile();
    copyMenu.DropDownItems.Add(copyPoint); copyMenu.DropDownItems.Add(copyVisible);
    copyMenu.DropDownItems.Add(exportVisible);
    ctx.Items.Add(totalItem); ctx.Items.Add(smoothItem); ctx.Items.Add(futureItem); ctx.Items.Add(interpItem); ctx.Items.Add(easingMenu); ctx.Items.Add(deltaMenu); ctx.Items.Add(copyMenu);
        _canvas.ContextMenuStrip = ctx;
        Controls.Add(_canvas);
        Controls.Add(top);
    _refreshTimer.Interval = 10_000; // refresh every 10s
        _refreshTimer.Tick += (s,e)=>{ RefreshHistory(); _canvas.Invalidate(); };
        Load += (s,e)=>{ RefreshHistory(); _refreshTimer.Start(); };
    FormClosed += (s,e)=> { _refreshTimer.Stop(); try { UserPreferences.Save(); } catch { } };
    }

    private void RefreshHistory()
    {
        var mem = _monitor.GetHistory(_switchIp, _ifIndex).OrderBy(h=>h.Timestamp).ToList();
        var cutoff = DateTime.UtcNow.AddMinutes(-_minutesWindow);
        if (mem.Count == 0 || mem.First().Timestamp > cutoff)
        {
            // Trenger eldre data enn i minne – hent fra disk
            var disk = _monitor.LoadHistoryFromDisk(_switchIp, _ifIndex, cutoff);
                // merge (disk may overlap) – take distinct on timestamp
            var merged = disk.Concat(mem).GroupBy(x=>x.Timestamp).Select(g=>g.First()).OrderBy(x=>x.Timestamp).ToList();
            _history = merged.Where(h=>h.Timestamp >= cutoff).ToList();
        }
        else
        {
            _history = mem.Where(h=>h.Timestamp >= cutoff).ToList();
        }
    }

    private void CanvasOnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    if (_history.Count < 2){ g.DrawString("No history", Font, Brushes.Gray, 10,10); return; }
        var arr = _history.ToArray();
        DateTime minT = arr.First().Timestamp; DateTime maxT = arr.Last().Timestamp;
        DateTime drawMaxT = maxT;
        if (_futurePadding)
        {
            drawMaxT = maxT.AddYears((int)FutureYears);
        }
        double totalSec = (drawMaxT-minT).TotalSeconds; if (totalSec<=0) totalSec=1;
        double maxVal = arr.Max(p=>p.InBps + p.OutBps); if (maxVal<=0) maxVal=1; maxVal = NiceCeiling(maxVal);
        var rect = new Rectangle(60,20,_canvas.Width-90,_canvas.Height-70);
        // store for hover mapping
        _lastMinT = minT; _lastDrawMaxT = drawMaxT; _lastRect = rect;
        using(var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.FromArgb(26,26,30), Color.FromArgb(16,16,20),90f)) g.FillRectangle(bg, rect);
        using(var axisPen = new Pen(Color.DimGray,1)) g.DrawRectangle(axisPen, rect);
        int hLines = 6;
        for (int i=0;i<=hLines;i++){
            float frac = i/(float)hLines; int y = rect.Top + (int)(rect.Height*frac);
            g.DrawLine(Pens.DimGray, rect.Left, y, rect.Right, y);
            double val = maxVal*(1-frac); g.DrawString(FormatBps(val), Font, Brushes.Gainsboro, 4, y-8);
        }
        // time ticks every quarter
        for (int q=0;q<=4;q++){
            double frac = q/4.0; int x = rect.Left + (int)(rect.Width*frac);
            g.DrawLine(Pens.DimGray, x, rect.Bottom, x, rect.Bottom+4);
            var ts = minT.AddSeconds(totalSec*frac).ToLocalTime();
            g.DrawString(ts.ToString("HH:mm"), Font, Brushes.Gainsboro, x-25, rect.Bottom+6);
        }
        PointF[] inPts = new PointF[arr.Length];
        PointF[] outPts = new PointF[arr.Length];
        PointF[] totPts = new PointF[arr.Length+2];
        double Smooth(int i, Func<InterfaceSnapshot,double> sel){ if(!_smoothing) return sel(arr[i]); int span=2; double sum=0; int c=0; for(int k=Math.Max(0,i-span); k<=Math.Min(arr.Length-1,i+span); k++){ sum+=sel(arr[k]); c++; } return sum/c; }
        for (int i=0;i<arr.Length;i++){
            var p = arr[i]; double sec = (p.Timestamp - minT).TotalSeconds; float x = rect.Left + (float)(sec/totalSec*rect.Width);
            double inV = Smooth(i,a=>a.InBps); double outV = Smooth(i,a=>a.OutBps); double tot = inV+outV;
            float yIn = rect.Bottom - (float)(inV/maxVal*rect.Height);
            float yOut = rect.Bottom - (float)(outV/maxVal*rect.Height);
            float yTot = rect.Bottom - (float)(tot/maxVal*rect.Height);
            inPts[i] = new PointF(x,yIn); outPts[i]=new PointF(x,yOut); totPts[i+1]=new PointF(x,yTot);
        }
        totPts[0] = new PointF(inPts.First().X, rect.Bottom); totPts[^1] = new PointF(inPts.Last().X, rect.Bottom);
        using var inPen = new Pen(Color.DeepSkyBlue,1.6f);
        using var outPen = new Pen(Color.Orange,1.6f);
        using var totPen = new Pen(Color.LightGray,1.2f){DashStyle=System.Drawing.Drawing2D.DashStyle.Dash};
        using var totFill = new SolidBrush(Color.FromArgb(50,150,150,150));
        if (_includeTotal) g.FillPolygon(totFill, totPts);
        if (inPts.Length>1) g.DrawLines(inPen, inPts);
        if (outPts.Length>1) g.DrawLines(outPen, outPts);
        if (_includeTotal) g.DrawLines(totPen, totPts.Skip(1).Take(totPts.Length-2).ToArray());
        var last = arr[^1];
        g.DrawString($"In {FormatBps(last.InBps)}", Font, Brushes.DeepSkyBlue, rect.Right-140, rect.Top+4);
        g.DrawString($"Out {FormatBps(last.OutBps)}", Font, Brushes.Orange, rect.Right-140, rect.Top+20);
        g.DrawString($"Tot {FormatBps(last.InBps+last.OutBps)}", Font, Brushes.LightGray, rect.Right-140, rect.Top+36);

        // Hover crosshair & label
        if (_hoverActive && _hoverX>=rect.Left && _hoverX<=rect.Right)
        {
            // Finn segment for interpolasjon
            InterfaceSnapshot? a=null,b=null;
            if (_history.Count==1){ a=b=_history[0]; }
            else
            {
                for (int i=0;i<_history.Count-1;i++)
                {
                    var cA = _history[i]; var cB = _history[i+1];
                    if (cA.Timestamp <= _hoverTimeUtc && cB.Timestamp >= _hoverTimeUtc)
                    { a=cA; b=cB; break; }
                }
                a ??= _history.First();
                b ??= _history.Last();
            }
            double inVal, outVal; DateTime shownTs;
            if (a==b)
            {
                inVal = a!.InBps; outVal = a.OutBps; shownTs = a.Timestamp;
            }
            else
            {
                double span = (b!.Timestamp - a!.Timestamp).TotalSeconds; if (span<=0) span=1;
                double pos = (_hoverTimeUtc - a.Timestamp).TotalSeconds; if (pos<0) pos=0; if (pos>span) pos=span;
                double t = pos / span; // 0..1
                inVal = a.InBps + (b.InBps - a.InBps)*t;
                outVal = a.OutBps + (b.OutBps - a.OutBps)*t;
                shownTs = a.Timestamp.AddSeconds(pos);
            }
            if (_stepInterpolation && a!=null && b!=null)
            {
                // Step mode: use left point value while between points
                inVal = a!.InBps; outVal = a.OutBps;
            }
            double totVal = inVal + outVal;
            // Easing av Y (total verdi) for mykere vertikal bevegelse
            if (_hoverActive)
            {
                if (_easedHoverY == null) _easedHoverY = totVal;
                else _easedHoverY = _easedHoverY + (totVal - _easedHoverY) * _easingFactor;
            }
            double displayTot = _easedHoverY ?? totVal;
            using var cross = new Pen(Color.FromArgb(150,200,200,255),1){DashStyle=System.Drawing.Drawing2D.DashStyle.Dot};
            g.DrawLine(cross, _hoverX, rect.Top, _hoverX, rect.Bottom);
            float yh = rect.Bottom - (float)(displayTot/maxVal*rect.Height);
            g.FillEllipse(Brushes.Yellow, _hoverX-3, yh-3,6,6);
            // Delta since previous sample
            string deltaLine = ""; Brush? deltaBrush = null; string? arrow = null;
            var prev = _history.LastOrDefault(h=>h.Timestamp < shownTs);
            if (prev != null)
            {
                double prevTot = prev.InBps + prev.OutBps;
                double diff = totVal - prevTot;
                if (Math.Abs(prevTot) < 0.0001) { prevTot = 0; }
                double pct = prevTot > 0 ? (diff/prevTot)*100.0 : 0;
                string sign = diff > 0 ? "+" : diff < 0 ? "-" : "";
                double absDiff = Math.Abs(diff);
                deltaLine = $"ΔTot: {sign}{FormatBps(absDiff)}bps ({pct:0.0}%)";
                bool highlight = Math.Abs(pct) >= _deltaHighlightThresholdPct;
                if (diff > 0) deltaBrush = highlight ? Brushes.Lime : Brushes.LimeGreen; else if (diff < 0) deltaBrush = highlight ? Brushes.Red : Brushes.OrangeRed; else deltaBrush = Brushes.LightGray;
                if (_lastDeltaValue != null)
                {
                    if (diff > _lastDeltaValue) arrow = "↑"; else if (diff < _lastDeltaValue) arrow = "↓"; else arrow = "→";
                }
                _lastDeltaValue = diff;
            }
            string[] lines = new[]{
                shownTs.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                $"In  : {FormatBps(inVal)}bps", $"Out : {FormatBps(outVal)}bps", $"Tot : {FormatBps(totVal)}bps" };
            if (!string.IsNullOrEmpty(deltaLine)) lines = lines.Concat(new[]{deltaLine}).ToArray();
            int pad=6; int maxW=0; int lineH=(int)g.MeasureString("X", Font).Height+1;
            foreach(var ln in lines){ int w=(int)g.MeasureString(ln, Font).Width; if (w>maxW) maxW=w; }
            int boxW=maxW+pad*2; int boxH=lineH*lines.Length+pad*2;
            int boxX=(int)_hoverX + 12; if (boxX+boxW>rect.Right) boxX = rect.Right - boxW - 4; if (boxX<rect.Left) boxX = rect.Left + 2;
            int boxY=rect.Top+30; if (boxY+boxH>rect.Bottom) boxY = rect.Bottom - boxH - 4;
            using var bgBox = new SolidBrush(Color.FromArgb(180,25,25,30));
            using var border = new Pen(Color.FromArgb(200,120,120,160));
            g.FillRectangle(bgBox, new Rectangle(boxX,boxY,boxW,boxH));
            g.DrawRectangle(border, new Rectangle(boxX,boxY,boxW,boxH));
            for (int i=0;i<lines.Length;i++)
            {
                if (i == lines.Length-1 && !string.IsNullOrEmpty(deltaLine) && deltaBrush!=null)
                {
                    // draw arrow (trend) on last delta line
                    string text = arrow != null ? $"{lines[i]} {arrow}" : lines[i];
                    g.DrawString(text, Font, deltaBrush, boxX+pad, boxY+pad + i*lineH);
                }
                else
                {
                    g.DrawString(lines[i], Font, Brushes.Gainsboro, boxX+pad, boxY+pad + i*lineH);
                }
            }
        }
    }

    private void CanvasOnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_history.Count < 1){ return; }
        if (e.X < _lastRect.Left || e.X > _lastRect.Right){ if (_hoverActive){ _hoverActive=false; _canvas.Invalidate(); } return; }
        double totalSec = (_lastDrawMaxT - _lastMinT).TotalSeconds; if (totalSec<=0) totalSec=1;
        double frac = (e.X - _lastRect.Left)/(double)_lastRect.Width; if (frac<0) frac=0; if (frac>1) frac=1;
        _hoverTimeUtc = _lastMinT.AddSeconds(totalSec*frac);
        _hoverX = e.X; _hoverActive = true;
        if (Math.Abs(_hoverX - _lastHoverPixelX) > 1.5f){ _lastHoverPixelX=_hoverX; _canvas.Invalidate(); }
    else if (_easedHoverY != null) // Continue easing animation even for tiny moves
        {
            _canvas.Invalidate();
        }
    }

    private void CopyCurrentPoint()
    {
        try
        {
            if (!_hoverActive || _history.Count == 0) return;
            var point = _history.OrderBy(h=>Math.Abs((h.Timestamp - _hoverTimeUtc).TotalSeconds)).FirstOrDefault();
            if (point == null) return;
            var line = $"{point.Timestamp:O},{point.InBps},{point.OutBps},{point.InBps+point.OutBps}";
            Clipboard.SetText("timestamp,in_bps,out_bps,total_bps\r\n" + line);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CopyCurrentPoint failed: "+ex.Message);
        }
    }

    private void CopyVisibleSeries()
    {
        try
        {
            if (_history.Count == 0) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("timestamp,in_bps,out_bps,total_bps");
            foreach (var h in _history)
            {
                sb.Append(h.Timestamp.ToString("O")); sb.Append(',');
                sb.Append(h.InBps); sb.Append(',');
                sb.Append(h.OutBps); sb.Append(',');
                sb.Append(h.InBps + h.OutBps); sb.AppendLine();
            }
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CopyVisibleSeries failed: "+ex.Message);
        }
    }

    private void SaveVisibleSeriesToFile()
    {
        try
        {
            if (_history.Count == 0) return;
            using var dlg = new SaveFileDialog();
            dlg.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
            dlg.FileName = $"history_{_switchIp}_if{_ifIndex}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("timestamp,in_bps,out_bps,total_bps");
            foreach (var h in _history)
            {
                sb.Append(h.Timestamp.ToString("O")); sb.Append(',');
                sb.Append(h.InBps); sb.Append(',');
                sb.Append(h.OutBps); sb.Append(',');
                sb.Append(h.InBps + h.OutBps); sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SaveVisibleSeriesToFile failed: "+ex.Message);
        }
    }

    // Dedicated buffered panel to reduce flicker
    private class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            this.UpdateStyles();
        }
    }

    private static string FormatBps(double v){ if (v>=1_000_000_000) return $"{v/1_000_000_000:0.00}G"; if (v>=1_000_000) return $"{v/1_000_000:0.00}M"; if (v>=1_000) return $"{v/1_000:0.00}K"; return v.ToString("0"); }
    private static double NiceCeiling(double value){ double exp=Math.Pow(10, Math.Floor(Math.Log10(value))); double mant = value/exp; double nice = mant<=1?1:mant<=2?2:mant<=5?5:10; return nice*exp; }
}
