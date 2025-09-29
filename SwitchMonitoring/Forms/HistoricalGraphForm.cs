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
    public HistoricalGraphForm(SwitchMonitor monitor, string switchIp, int ifIndex, string title, int minutesWindow = 120)
    {
        _monitor = monitor; _switchIp = switchIp; _ifIndex = ifIndex; _title = title; _minutesWindow = minutesWindow;
        Text = $"Historikk - {title}";
        Width = 1000; Height = 520;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;
        var top = new Panel{Dock=DockStyle.Top,Height=32,Padding=new Padding(6,4,6,4)};
    _titleLabel = new Label{Text=$"{title} (siste {_minutesWindow} min)",Dock=DockStyle.Left,Width=400,ForeColor=Color.White};
        var winBox = new ComboBox{Dock=DockStyle.Right,Width=100,DropDownStyle=ComboBoxStyle.DropDownList};
        winBox.Items.AddRange(new object[]{"30","60","120","240","480"});
        winBox.SelectedItem = _minutesWindow.ToString();
        top.Controls.Add(winBox);
        top.Controls.Add(_titleLabel);
        _canvas = new Panel{Dock=DockStyle.Fill, BackColor = Color.FromArgb(20,20,24)};
        // Event etter at _canvas er opprettet
        winBox.SelectedIndexChanged += (s,e)=>
        {
            var sel = winBox.SelectedItem;
            if (sel != null && int.TryParse(sel.ToString(), out var m))
            {
                _minutesWindow = m;
                RefreshHistory();
                _canvas.Invalidate();
                _titleLabel.Text = $"{title} (siste {_minutesWindow} min)";
            }
        };
        _canvas.Paint += CanvasOnPaint;
        var ctx = new ContextMenuStrip();
        var totalItem = new ToolStripMenuItem("Vis total areal"){Checked=_includeTotal,CheckOnClick=true}; totalItem.CheckedChanged += (s,e)=>{_includeTotal=totalItem.Checked; _canvas.Invalidate();};
        var smoothItem = new ToolStripMenuItem("Glatting (MA5)"){Checked=_smoothing,CheckOnClick=true}; smoothItem.CheckedChanged += (s,e)=>{_smoothing=smoothItem.Checked; _canvas.Invalidate();};
        ctx.Items.Add(totalItem); ctx.Items.Add(smoothItem);
        _canvas.ContextMenuStrip = ctx;
        Controls.Add(_canvas);
        Controls.Add(top);
        _refreshTimer.Interval = 10_000; // refresh each 10s
        _refreshTimer.Tick += (s,e)=>{ RefreshHistory(); _canvas.Invalidate(); };
        Load += (s,e)=>{ RefreshHistory(); _refreshTimer.Start(); };
        FormClosed += (s,e)=> _refreshTimer.Stop();
    }

    private void RefreshHistory()
    {
        var all = _monitor.GetHistory(_switchIp, _ifIndex).OrderBy(h=>h.Timestamp).ToList();
        if (all.Count == 0){ _history = all; return; }
        var cutoff = DateTime.UtcNow.AddMinutes(-_minutesWindow);
        _history = all.Where(h=>h.Timestamp >= cutoff).ToList();
    }

    private void CanvasOnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_history.Count < 2){ g.DrawString("Ingen historikk", Font, Brushes.Gray, 10,10); return; }
        var arr = _history.ToArray();
        DateTime minT = arr.First().Timestamp; DateTime maxT = arr.Last().Timestamp; double totalSec = (maxT-minT).TotalSeconds; if (totalSec<=0) totalSec=1;
        double maxVal = arr.Max(p=>p.InBps + p.OutBps); if (maxVal<=0) maxVal=1; maxVal = NiceCeiling(maxVal);
        var rect = new Rectangle(60,20,_canvas.Width-90,_canvas.Height-70);
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
    }

    private static string FormatBps(double v){ if (v>=1_000_000_000) return $"{v/1_000_000_000:0.00}G"; if (v>=1_000_000) return $"{v/1_000_000:0.00}M"; if (v>=1_000) return $"{v/1_000:0.00}K"; return v.ToString("0"); }
    private static double NiceCeiling(double value){ double exp=Math.Pow(10, Math.Floor(Math.Log10(value))); double mant = value/exp; double nice = mant<=1?1:mant<=2?2:mant<=5?5:10; return nice*exp; }
}
