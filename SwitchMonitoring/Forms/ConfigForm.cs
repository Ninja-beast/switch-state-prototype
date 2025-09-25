using SwitchMonitoring.Models;
using SwitchMonitoring.Services;

namespace SwitchMonitoring.Forms;

public class ConfigForm : Form
{
    private readonly SwitchMonitor _monitor;
    private readonly TextBox _txtName = new(){Width=220};
    private readonly TextBox _txtIp = new(){Width=220};
    private readonly TextBox _txtCommunity = new(){Width=220, Text="public"};
    private readonly NumericUpDown _numPoll = new(){Minimum=2, Maximum=3600, Value=30, Width=100};
    private readonly NumericUpDown _numMaxIf = new(){Minimum=1, Maximum=4096, Value=10, Width=100};
    private readonly CheckBox _chkIfX = new(){Text="Bruk ifX (64-bit)"};
    private readonly Button _btnTest = new(){Text="Test SNMP"};
    private readonly Button _btnDiag = new(){Text="Diagnose"};
    private readonly Button _btnSave = new(){Text="Lagre"};
    private readonly Label _lblResult = new(){AutoSize=true, ForeColor=Color.LightGray};
    private List<SwitchInfo> _current;

    public record ResultConfig(List<SwitchInfo> Switches,int Poll,int MaxIf,bool UseIfXTable);

    public ResultConfig? Result { get; private set; }

    public ConfigForm(SwitchMonitor monitor, List<SwitchInfo> existing)
    {
        _monitor = monitor;
        _current = existing.Select(s => new SwitchInfo{ Name=s.Name, IPAddress=s.IPAddress, Community=s.Community}).ToList();
        Text = "Konfigurasjon";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Width = 450; Height = 380;
        BackColor = Color.FromArgb(32,32,36);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI",9F);

    var layout = new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=12,Padding=new Padding(10),AutoSize=true};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));

        AddRow(layout,"Navn:", _txtName);
        AddRow(layout,"IP:", _txtIp);
        AddRow(layout,"Community:", _txtCommunity);
        AddRow(layout,"Poll (s):", _numPoll);
        AddRow(layout,"Max Interfaces:", _numMaxIf);
        AddRow(layout,"", _chkIfX);
    layout.Controls.Add(_btnTest,0,6); layout.SetColumnSpan(_btnTest,2);
    layout.Controls.Add(_btnDiag,0,7); layout.SetColumnSpan(_btnDiag,2);
    layout.Controls.Add(_btnSave,0,8); layout.SetColumnSpan(_btnSave,2);
    layout.Controls.Add(_lblResult,0,9); layout.SetColumnSpan(_lblResult,2);

        Controls.Add(layout);

        // Init values from existing config/monitor
        try
        {
            if (_current.Any())
            {
                var first = _current[0];
                _txtName.Text = first.Name;
                _txtIp.Text = first.IPAddress;
                _txtCommunity.Text = first.Community;
            }
            _numPoll.Value = Math.Clamp(_monitor.GetPollInterval(), (int)_numPoll.Minimum, (int)_numPoll.Maximum);
            _numMaxIf.Value = Math.Clamp(_monitor.GetMaxInterfaces(), (int)_numMaxIf.Minimum, (int)_numMaxIf.Maximum);
            _chkIfX.Checked = _monitor.GetUseIfXTable();
        }
        catch { }

        _btnTest.Click += async (s,e) => await DoTestAsync();
        _btnDiag.Click += async (s,e) => await DoDiagAsync();
        _btnSave.Click += (s,e) => SaveAndClose();
    }

    private static void AddRow(TableLayoutPanel p,string label, Control c)
    {
        var row = p.RowCount-1;
        p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        p.Controls.Add(new Label{Text=label,Anchor=AnchorStyles.Left,AutoSize=true,Margin=new Padding(0,6,8,4)},0,row);
        p.Controls.Add(c,1,row);
        p.RowCount++;
    }

    private async Task DoTestAsync()
    {
        _lblResult.Text = "Tester...";
        _lblResult.ForeColor = Color.LightGray;
        var (ok,msg) = await _monitor.TestSnmpAsync(_txtIp.Text.Trim(), _txtCommunity.Text.Trim());
        _lblResult.Text = msg;
        _lblResult.ForeColor = ok ? Color.LightGreen : Color.OrangeRed;
    }

    private void SaveAndClose()
    {
        var list = new List<SwitchInfo>{ new(){ Name=_txtName.Text.Trim(), IPAddress=_txtIp.Text.Trim(), Community=_txtCommunity.Text.Trim() } };
        Result = new ResultConfig(list,(int)_numPoll.Value,(int)_numMaxIf.Value,_chkIfX.Checked);
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task DoDiagAsync()
    {
        _lblResult.Text = "Diagnose...";
        _lblResult.ForeColor = Color.LightGray;
        try
        {
            var text = await _monitor.TestDiagnosticAsync(_txtIp.Text.Trim(), _txtCommunity.Text.Trim());
            using var diag = new Form
            {
                Text = $"Diagnose - {_txtName.Text.Trim()} ({_txtIp.Text.Trim()})",
                Width = 620,
                Height = 480,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(32,32,36),
                ForeColor = Color.Gainsboro,
                Font = Font
            };
            var tb = new TextBox{ Multiline=true, ReadOnly=true, Dock=DockStyle.Fill, ScrollBars=ScrollBars.Vertical, BackColor=Color.FromArgb(25,25,28), ForeColor=Color.LightGreen, Text=text.Replace("\n", Environment.NewLine)};
            diag.Controls.Add(tb);
            diag.ShowDialog(this);
            _lblResult.Text = "Diagnose ferdig";
            _lblResult.ForeColor = Color.LightGreen;
        }
        catch (Exception ex)
        {
            _lblResult.Text = ex.Message;
            _lblResult.ForeColor = Color.OrangeRed;
        }
    }
}
