using SwitchMonitoring.Services;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Forms;

public class SFlowMainForm : Form
{
    private readonly SFlowCollector _collector;
    private readonly int _refreshSeconds;
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Label _status = new();
    private readonly FlowLayoutPanel _topPanel = new();
    private readonly Button _btnInject = new();
    private readonly Button _btnBurst = new();
    private readonly Button _btnHelp = new();
    private bool _busy;
    private readonly Dictionary<string,string> _agentNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _combined;

    public SFlowMainForm(SFlowCollector collector, int refreshSeconds, Dictionary<string,string> agentNameMap, bool combined=false)
        : this(collector, refreshSeconds, combined)
    {
        _agentNameMap = agentNameMap;
    }

    public SFlowMainForm(SFlowCollector collector, int refreshSeconds, bool combined=false)
    {
        _collector = collector;
        _refreshSeconds = Math.Max(5, refreshSeconds); // sFlow counters kommer gjerne hvert 30. sekund; men vi kan oppdatere oftere
        _combined = combined;
        Text = "sFlow Trafikk";
        Width = 1100; Height = 700;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI",9F);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = Color.FromArgb(40,40,46);
        _grid.BorderStyle = BorderStyle.None;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(55,55,62);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI",9F,FontStyle.Bold);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(40,40,46);
        _grid.DefaultCellStyle.ForeColor = Color.Gainsboro;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 22;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.BackColor = Color.FromArgb(25,25,28);

        _topPanel.Dock = DockStyle.Top;
        _topPanel.Height = 34;
        _topPanel.Padding = new Padding(4,4,4,0);
        _topPanel.BackColor = Color.FromArgb(25,25,28);

        _btnInject.Text = "Inject test";
        _btnInject.Width = 90;
        _btnInject.Click += (s,e)=> { _collector.InjectTestSample(); _ = RefreshAsync(); };

        _btnBurst.Text = "Inject burst";
        _btnBurst.Width = 90;
        _btnBurst.Click += (s,e)=> {
            for (int i=0;i<10;i++) _collector.InjectTestSample(ifIndex: (i%3)+1, inOctets: (ulong)(10_000_000 + i*1_000_000), outOctets:(ulong)(5_000_000 + i*500_000));
            _ = RefreshAsync(); };

        _btnHelp.Text = "Info";
        _btnHelp.Width = 70;
        _btnHelp.Click += (s,e)=> ShowHelp();

        _topPanel.Controls.Add(_btnInject);
        _topPanel.Controls.Add(_btnBurst);
        _topPanel.Controls.Add(_btnHelp);

        Controls.Add(_grid);
        Controls.Add(_topPanel);
        Controls.Add(_status);

        InitColumns();

        _timer.Interval = 3000; // refresh visning
        _timer.Tick += async (s,e) => await RefreshAsync();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RefreshAsync();
        _timer.Start();
    }

    private void ShowHelp()
    {
        MessageBox.Show("Testknapper:\nInject test = én syntetisk interface sample.\nInject burst = flere interfaces.\nDisse brukes bare for å verifisere UI når ingen ekte sFlow mottas.", "Hjelp", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void InitColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add("Agent","Agent");
        _grid.Columns.Add("IfIndex","Idx");
        _grid.Columns.Add("IfName","Interface");
        if (_combined)
        {
            _grid.Columns.Add("Traffic","In|Out (bps)");
        }
        else
        {
            _grid.Columns.Add("InBps","In (bps)");
            _grid.Columns.Add("OutBps","Out (bps)");
        }
        _grid.Columns.Add("Last","Last Sample");
    }

    private async Task RefreshAsync()
    {
        if (_busy) return; _busy = true;
        try
        {
            var snaps = _collector.GetSnapshots();
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            var seenAgents = new HashSet<string>();
            foreach (var s in snaps.OrderBy(x=>x.SwitchIp).ThenBy(x=>x.IfIndex))
            {
                seenAgents.Add(s.SwitchIp);
                var agentLabel = _agentNameMap.TryGetValue(s.SwitchIp, out var friendly) ? $"{friendly} ({s.SwitchIp})" : s.SwitchIp;
                if (_combined)
                {
                    _grid.Rows.Add(
                        agentLabel,
                        s.IfIndex,
                        s.IfName,
                        $"{FormatBps(s.InBps)}/{FormatBps(s.OutBps)}",
                        s.Timestamp.ToLocalTime().ToString("HH:mm:ss")
                    );
                }
                else
                {
                    _grid.Rows.Add(
                        agentLabel,
                        s.IfIndex,
                        s.IfName,
                        FormatBps(s.InBps),
                        FormatBps(s.OutBps),
                        s.Timestamp.ToLocalTime().ToString("HH:mm:ss")
                    );
                }
            }
            // placeholder rows for configured agents without data
            foreach (var kv in _agentNameMap)
            {
                if (!seenAgents.Contains(kv.Key))
                {
                    if (_combined)
                        _grid.Rows.Add($"{kv.Value} ({kv.Key})", "-", "(ingen data ennå)", "-/-", "-");
                    else
                        _grid.Rows.Add($"{kv.Value} ({kv.Key})", "-", "(ingen data ennå)", "-", "-", "-");
                }
            }
            _grid.ResumeLayout();
            var (total, perAgent, raw, invalid, parseErr, lastMap, flowOnly) = _collector.GetDatagramStats();
            string agg = string.Join(", ", perAgent.Select(p=>$"{p.Key}:{p.Value}"));
            // Oppdater placeholder med flow-only info
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Cells[1].Value?.ToString()=="-" && row.Cells[2].Value?.ToString()=="(ingen data ennå)")
                {
                    // hent IP fra agent label i parentes
                    var agentLabel = row.Cells[0].Value?.ToString() ?? "";
                    var ipStart = agentLabel.LastIndexOf('(');
                    var ipEnd = agentLabel.LastIndexOf(')');
                    if (ipStart>0 && ipEnd>ipStart)
                    {
                        var ip = agentLabel.Substring(ipStart+1, ipEnd-ipStart-1);
                        if (flowOnly.Contains(ip))
                        {
                            row.Cells[2].Value = "(flow only)";
                        }
                        if (lastMap.TryGetValue(ip, out var t))
                        {
                            // Last sample column index avhenger av combined
                            var lastCol = _combined ? 4 : 5;
                            row.Cells[lastCol].Value = t.ToLocalTime().ToString("HH:mm:ss");
                        }
                    }
                }
            }
            string lastInfo = string.Join(", ", lastMap.Select(k=>$"{k.Key}:{k.Value:HH:mm:ss}"));
            if (lastInfo.Length > 120) lastInfo = lastInfo.Substring(0,120) + "...";
            _status.Text = $"Rader: {snaps.Count} Datagram: {total} (rå:{raw} inv:{invalid} parse:{parseErr}) [{agg}] Last:[{lastInfo}] Oppdatert {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _status.Text = "Feil: " + ex.Message;
        }
        finally { _busy = false; }
        await Task.CompletedTask;
    }

    private static string FormatBps(double? bps)
    {
        if (!bps.HasValue) return "-";
        var v = bps.Value;
        if (v >= 1_000_000_000) return $"{v/1_000_000_000:0.00}G";
        if (v >= 1_000_000) return $"{v/1_000_000:0.00}M";
        if (v >= 1_000) return $"{v/1_000:0.00}K";
        return v.ToString("0");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        try { _collector.Stop(); } catch { }
    }
}
