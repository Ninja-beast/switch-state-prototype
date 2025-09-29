using SwitchMonitoring.Services;

namespace SwitchMonitoring.Forms;

public class SnmpQueryForm : Form
{
    private readonly SwitchMonitor _monitor;
    private readonly TextBox _txtIp = new(){Width=180};
    private readonly TextBox _txtCommunity = new(){Width=120, Text="public"};
    private readonly TextBox _txtOid = new(){Width=260, Text="1.3.6.1.2.1.1.5.0"};
    private readonly Button _btnRun = new(){Text="run"};
    private readonly Button _btnClose = new(){Text="Lukk"};
    private readonly TextBox _txtResult = new(){Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, Dock=DockStyle.Fill, BackColor=Color.FromArgb(25,25,28), ForeColor=Color.LightGreen};
    private readonly Label _lblStatus = new(){AutoSize=true, ForeColor=Color.LightGray};

    public SnmpQueryForm(SwitchMonitor monitor)
    {
        _monitor = monitor;
        Text = "SNMP Query";
        Width = 640; Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(32,32,36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI",9F);

        var first = _monitor.GetSwitches().FirstOrDefault();
        if (first != null)
        {
            _txtIp.Text = first.IPAddress;
            _txtCommunity.Text = first.Community;
        }

        var top = new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=6,Height=70,Padding=new Padding(8)};
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label{Text="IP:",AutoSize=true,Margin=new Padding(0,6,4,0)},0,0);
        top.Controls.Add(_txtIp,1,0);
        top.Controls.Add(new Label{Text="Community:",AutoSize=true,Margin=new Padding(12,6,4,0)},2,0);
        top.Controls.Add(_txtCommunity,3,0);
        top.Controls.Add(new Label{Text="OID:",AutoSize=true,Margin=new Padding(12,6,4,0)},4,0);
        top.Controls.Add(_txtOid,5,0);
        top.Controls.Add(_btnRun,0,1); top.SetColumnSpan(_btnRun,2);
        top.Controls.Add(_btnClose,2,1); top.SetColumnSpan(_btnClose,2);
        top.Controls.Add(_lblStatus,4,1); top.SetColumnSpan(_lblStatus,2);

        Controls.Add(_txtResult);
        Controls.Add(top);

        _btnRun.Click += async (s,e) => await DoQueryAsync();
        _btnClose.Click += (s,e) => Close();
    }

    private async Task DoQueryAsync()
    {
        var ip = _txtIp.Text.Trim();
        var comm = _txtCommunity.Text.Trim();
        var oid = _txtOid.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(comm) || string.IsNullOrWhiteSpace(oid))
        {
            _lblStatus.Text = "Missing field"; _lblStatus.ForeColor = Color.OrangeRed; return;
        }
        try
        {
            _lblStatus.Text = "Ask..."; _lblStatus.ForeColor = Color.LightGray;
            var (ok,val,err) = await _monitor.QueryOidAsync(ip, comm, oid);
            if (ok)
            {
                _txtResult.Text = $"OID: {oid}\r\nVerdi: {val}";
                _lblStatus.Text = "OK"; _lblStatus.ForeColor = Color.LightGreen;
            }
            else
            {
                _txtResult.Text = $"OID: {oid}\r\nFeil: {err}";
                _lblStatus.Text = "FEIL"; _lblStatus.ForeColor = Color.OrangeRed;
            }
        }
        catch (Exception ex)
        {
            _txtResult.Text = ex.ToString();
            _lblStatus.Text = "EXC"; _lblStatus.ForeColor = Color.OrangeRed;
        }
    }
}
