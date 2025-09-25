using System.Data;
using SwitchMonitoring.Services;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Forms;

public class MainForm : Form
{
    private readonly SwitchMonitor _monitor;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private readonly System.Windows.Forms.Timer _autoTimer;
    private readonly DataGridView _grid;
    private readonly int _pollSeconds;
    private List<InterfaceSnapshot> _lastSnapshots = new();
    private readonly Label _statusLabel;
    private readonly Button _refreshButton;
    private bool _busy;
    private readonly MenuStrip _menu;

    public MainForm(SwitchMonitor monitor, int pollSeconds)
    {
        _monitor = monitor;
        _pollSeconds = pollSeconds;
        Text = "Switch Trafikk";
        Width = 1200;
        Height = 700;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(40,40,46),
            BorderStyle = BorderStyle.None,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(55,55,62);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(40,40,46);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(70,70,80);
        _grid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6,0,0,0),
            BackColor = Color.FromArgb(25,25,28)
        };

        _refreshButton = new Button
        {
            Text = "Oppdater nå",
            Dock = DockStyle.Top,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55,55,62),
            ForeColor = Color.White
        };
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.Click += async (s,e) => await ManualRefreshAsync();

    _menu = new MenuStrip{Dock=DockStyle.Top, BackColor=Color.FromArgb(45,45,50), ForeColor=Color.White};
    var cfgItem = new ToolStripMenuItem("Konfigurasjon");
    var editItem = new ToolStripMenuItem("Endre...");
    var testItem = new ToolStripMenuItem("Test SNMP");
    var diagItem = new ToolStripMenuItem("Diagnose...");
    cfgItem.DropDownItems.Add(editItem);
    cfgItem.DropDownItems.Add(testItem);
    cfgItem.DropDownItems.Add(new ToolStripSeparator());
    cfgItem.DropDownItems.Add(diagItem);
    _menu.Items.Add(cfgItem);

    editItem.Click += async (s,e) => await ShowConfigAsync();
    testItem.Click += async (s,e) => await TestCurrentAsync();
    diagItem.Click += async (s,e) => await DiagnoseAsync();

    Controls.Add(_grid);
    Controls.Add(_refreshButton);
    Controls.Add(_statusLabel);
    Controls.Add(_menu);
    MainMenuStrip = _menu;
        InitColumns();

    _uiTimer = new System.Windows.Forms.Timer();
    _uiTimer.Interval = _pollSeconds * 1000; // hyppig for løpende rate
    _uiTimer.Tick += async (s,e) => await RefreshDataAsync();

    _autoTimer = new System.Windows.Forms.Timer();
    _autoTimer.Interval = 120_000; // 2 minutter
    _autoTimer.Tick += async (s,e) => await RefreshDataAsync();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RefreshDataAsync();
        _uiTimer.Start();
    _autoTimer.Start();
    }

    private void InitColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add("SwitchName", "Switch");
    _grid.Columns.Add("SwitchIp", "IP");
        _grid.Columns.Add("IfIndex", "Idx");
    _grid.Columns.Add("IfName", "Interface");
        _grid.Columns.Add("Status", "Status");
    _grid.Columns.Add("SpeedLabel", "Speed");
        _grid.Columns.Add("InBps", "In (bps)");
        _grid.Columns.Add("OutBps", "Out (bps)");
        _grid.Columns.Add("UtilIn", "Util In %");
        _grid.Columns.Add("UtilOut", "Util Out %");
        _grid.Columns.Add("LastUpdated", "Last Seen");
    }

    private async Task ManualRefreshAsync() => await RefreshDataAsync(manual:true);

    private async Task RefreshDataAsync(bool manual = false)
    {
        if (_busy) return;
        try { _busy = true; _refreshButton.Enabled = false; _statusLabel.Text = "Oppdaterer..."; } catch { }
        try
        {
            var snaps = await _monitor.PollOnceAsync();
            _lastSnapshots = snaps;
            BindGrid(snaps);
            var errors = snaps.Count(s => s.Status == "ERR");
            _statusLabel.Text = $"Sist {(manual?"manuell":"auto")} oppdatert: {DateTime.Now:HH:mm:ss}  Rader: {snaps.Count}  Feil: {errors}";
        }
        catch (Exception ex)
        {
            // show minimal error status in title
            Text = $"Switch Trafikk - feil: {ex.Message}";
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _refreshButton.Enabled = true;
        }
    }

    private async Task ShowConfigAsync()
    {
        if (_busy) return;
        using var dlg = new ConfigForm(_monitor, GetCurrentSwitches());
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            var r = dlg.Result;
            _monitor.UpdateConfiguration(r.Poll, r.MaxIf, r.UseIfXTable, r.Switches);
            _statusLabel.Text = "Lagret ny konfigurasjon";
            await RefreshDataAsync(manual:true);
            // Persist to file
            PersistSettings(r);
        }
    }

    private List<SwitchInfo> GetCurrentSwitches()
    {
        // Bruk den ekte konfigurasjonen fra monitor
        return _monitor.GetSwitches().Select(s => new SwitchInfo{ Name=s.Name, IPAddress=s.IPAddress, Community=s.Community}).ToList();
    }

    private async Task TestCurrentAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null) return;
        _statusLabel.Text = "Tester SNMP...";
        var (ok,msg) = await _monitor.TestSnmpAsync(sw.IPAddress, sw.Community);
        _statusLabel.Text = msg;
    }

    private async Task DiagnoseAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null)
        {
            MessageBox.Show(this, "Ingen switch er konfigurert.", "Diagnose", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _statusLabel.Text = "Kjører diagnose...";
            var text = await _monitor.TestDiagnosticAsync(sw.IPAddress, sw.Community);
            _statusLabel.Text = "Diagnose ferdig";
            // Vis i eget vindu for bedre lesbarhet
            using var diag = new Form
            {
                Text = $"Diagnose - {sw.Name} ({sw.IPAddress})",
                Width = 620,
                Height = 480,
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Color.FromArgb(32,32,36),
                ForeColor = Color.Gainsboro,
                Font = Font
            };
            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(25,25,28),
                ForeColor = Color.LightGreen,
                Text = text.Replace("\n", Environment.NewLine)
            };
            diag.Controls.Add(tb);
            diag.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Diagnose feilet";
            MessageBox.Show(this, ex.Message, "Diagnose feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PersistSettings(ConfigForm.ResultConfig r)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                PollIntervalSeconds = r.Poll,
                MaxInterfaces = r.MaxIf,
                UseIfXTable = r.UseIfXTable,
                Switches = r.Switches
            }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Kunne ikke lagre konfig: " + ex.Message;
        }
    }

    private void BindGrid(List<InterfaceSnapshot> snaps)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var s in snaps.OrderBy(x => x.SwitchName).ThenBy(x => x.IfIndex))
        {
            var idx = _grid.Rows.Add(
                s.SwitchName,
                s.SwitchIp,
                s.IfIndex,
                s.IfName,
                s.Status,
                FormatSpeedShort(s.SpeedLabel),
                FormatBps(s.InBps),
                FormatBps(s.OutBps),
                s.UtilInPercent.ToString("0.0"),
                s.UtilOutPercent.ToString("0.0"),
                s.Timestamp.ToLocalTime().ToString("HH:mm:ss")
            );
            var row = _grid.Rows[idx];
            if (s.Status == "ERR")
            {
                row.DefaultCellStyle.BackColor = Color.DarkRed;
                row.DefaultCellStyle.ForeColor = Color.White;
            }
            else if (s.Status.Equals("DOWN", StringComparison.OrdinalIgnoreCase))
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(90,60,0);
                row.DefaultCellStyle.ForeColor = Color.White;
            }
        }
        _grid.ResumeLayout();
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
    private static string FormatSpeedShort(string raw)
    {
        if (double.TryParse(raw, out var sp))
        {
            if (sp >= 1_000_000_000) return $"{sp/1_000_000_000:0.#}G";
            if (sp >= 1_000_000) return $"{sp/1_000_000:0.#}M";
            if (sp >= 1_000) return $"{sp/1_000:0.#}K";
            return sp.ToString("0");
        }
        return raw;
    }
}
