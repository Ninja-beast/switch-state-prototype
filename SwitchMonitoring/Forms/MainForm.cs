using System.Data;
using SwitchMonitoring.Services;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Forms;

// Main window for the Switch Monitoring application
public class MainForm : Form
{
    // Fields
    private readonly SwitchMonitor _monitor; // The switch monitor service
    private readonly System.Windows.Forms.Timer _uiTimer; // Timer for updating UI
    private readonly System.Windows.Forms.Timer _autoTimer; // Timer for periodic updates
    private readonly DataGridView _grid; // Table/grid for data display
    private readonly int _pollSeconds; // Polling interval in seconds
    private List<InterfaceSnapshot> _lastSnapshots = new(); // Last polled data
    private readonly Label _statusLabel; // Status display at the bottom
    private readonly Button _refreshButton; // Manual refresh button
    private bool _busy; // Busy flag to avoid concurrent actions
    private readonly MenuStrip _menu; // Top menu bar
    private readonly bool _combined; // Whether to show combined traffic

    // Constructor
    public MainForm(SwitchMonitor monitor, int pollSeconds, bool combined=false)
    {
        _monitor = monitor;
        _pollSeconds = pollSeconds;
        _combined = combined;
        Text = "Switch Traffic";
        Width = 1200;
        Height = 700;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // Configure DataGridView
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

        // Status label at the bottom
        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = Color.LightGray,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6,0,0,0),
            BackColor = Color.FromArgb(25,25,28)
        };

        // Manual refresh button
        _refreshButton = new Button
        {
        Text = "Refresh now",
            Dock = DockStyle.Top,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55,55,62),
            ForeColor = Color.White
        };
        _refreshButton.FlatAppearance.BorderSize = 0;
        _refreshButton.Click += async (s,e) => await ManualRefreshAsync();
    // Menu bar
    _menu = new MenuStrip{Dock=DockStyle.Top, BackColor=Color.FromArgb(45,45,50), ForeColor=Color.White};
    var cfgItem = new ToolStripMenuItem("Configuration");
    var editItem = new ToolStripMenuItem("Edit...");
    var testItem = new ToolStripMenuItem("Test SNMP");
    var diagItem = new ToolStripMenuItem("Diagnostics...");
    var commTestItem = new ToolStripMenuItem("Test communities...");
    var snmpPingItem = new ToolStripMenuItem("SNMP Ping...");
    var listIfItem = new ToolStripMenuItem("List interfaces...");
    var graphItem = new ToolStripMenuItem("Live graph (selected port)");
    // Removed standalone SNMP Query test window
    cfgItem.DropDownItems.Add(editItem);
    cfgItem.DropDownItems.Add(testItem);
    cfgItem.DropDownItems.Add(new ToolStripSeparator());
    cfgItem.DropDownItems.Add(snmpPingItem);
    cfgItem.DropDownItems.Add(listIfItem);
    cfgItem.DropDownItems.Add(new ToolStripSeparator());
    cfgItem.DropDownItems.Add(diagItem);
    cfgItem.DropDownItems.Add(commTestItem);
    cfgItem.DropDownItems.Add(new ToolStripSeparator());
    cfgItem.DropDownItems.Add(graphItem);
    _menu.Items.Add(cfgItem);

        // Hook up menu actions
        editItem.Click += async (s,e) => await ShowConfigAsync();
        testItem.Click += async (s,e) => await TestCurrentAsync();
        diagItem.Click += async (s,e) => await DiagnoseAsync();
        commTestItem.Click += async (s,e) => await TestCommunitiesAsync();
        snmpPingItem.Click += async (s,e) => await SnmpPingAsync();
        listIfItem.Click += async (s,e) => await ListInterfacesAsync();
        graphItem.Click += (s,e) => OpenGraphForSelected();

    // History menu
    var histMenu = new ToolStripMenuItem("History");
    var histOpenItem = new ToolStripMenuItem("History graph (selected port)...");
    histOpenItem.Click += (s,e) => OpenHistoricalGraphForSelected();
    histMenu.DropDownItems.Add(histOpenItem);
    _menu.Items.Add(histMenu);

        // Add controls to the form
        Controls.Add(_grid);
        Controls.Add(_refreshButton);
        Controls.Add(_statusLabel);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
        KeyPreview = true;
        InitColumns();

        // Timers for polling and UI updating
        _uiTimer = new System.Windows.Forms.Timer();
        _uiTimer.Interval = _pollSeconds * 1000; // Frequent for continuous rate
        _uiTimer.Tick += async (s,e) => await RefreshDataAsync();

        _autoTimer = new System.Windows.Forms.Timer();
        _autoTimer.Interval = 120_000; // 2 minutes
        _autoTimer.Tick += async (s,e) => await RefreshDataAsync();
    }

    // Called when the form is loaded
    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        await RefreshDataAsync();
        _uiTimer.Start();
        _autoTimer.Start();
        // Extra poll after short delay to capture initial SNMP timeouts in the log
        _ = Task.Run(async () => { await Task.Delay(2000); await RefreshDataAsync(); });
    }

    // Keyboard shortcuts
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5)
        {
            _ = ManualRefreshAsync();
            return true;
        }
        if (keyData == (Keys.Control | Keys.G))
        {
            OpenGraphForSelected();
            return true;
        }
        if (keyData == (Keys.Control | Keys.H))
        {
            OpenHistoricalGraphForSelected();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Initialize grid columns
    private void InitColumns()
    {
        _grid.Columns.Clear();
        _grid.Columns.Add("SwitchName", "Switch");
        _grid.Columns.Add("SwitchIp", "IP");
        _grid.Columns.Add("IfIndex", "Port");
        _grid.Columns.Add("IfName", "Interface");
        _grid.Columns.Add("Status", "Status");
        _grid.Columns.Add("SpeedLabel", "Speed");
        if (_combined)
            _grid.Columns.Add("Traffic", "In|Out (bps)");
        else
        {
            _grid.Columns.Add("InBps", "In (bps)");
            _grid.Columns.Add("OutBps", "Out (bps)");
        }
        _grid.Columns.Add("UtilIn", "Util In %");
        _grid.Columns.Add("UtilOut", "Util Out %");
        _grid.Columns.Add("LastUpdated", "Last Seen");
    }

    // Manual refresh method
    private async Task ManualRefreshAsync() => await RefreshDataAsync(manual:true);

    // Data polling and grid updating
    private async Task RefreshDataAsync(bool manual = false)
    {
        if (_busy) return;
    try { _busy = true; _refreshButton.Enabled = false; _statusLabel.Text = "Refreshing..."; } catch { }
        try
        {
            var snaps = await _monitor.PollOnceAsync();
            _lastSnapshots = snaps;
            // Update open live graph windows
            foreach (Form f in Application.OpenForms)
            {
                if (f is PortGraphForm pg)
                {
                    var match = snaps.FirstOrDefault(s => pg.Matches(s.SwitchIp, s.IfIndex));
                    if (match != null)
                        pg.AddSample(match.Timestamp.ToLocalTime(), match.InBps, match.OutBps);
                }
            }
            BindGrid(snaps);
            var errors = snaps.Count(s => s.Status == "ERR");
            _statusLabel.Text = $"Last {(manual?"manual":"auto")} refresh: {DateTime.Now:HH:mm:ss}  Rows: {snaps.Count}  Errors: {errors}";
            if (snaps.Count == 0)
            {
                _statusLabel.Text += " | No data – check Diagnostics";
                AppLogger.Warn("Poll returned 0 snapshots");
            }
        }
        catch (Exception ex)
        {
            // show minimal error status in title
            Text = $"Switch Traffic - error: {ex.Message}";
            _statusLabel.Text = ex.Message;
            AppLogger.Exception("RefreshData", ex);
        }
        finally
        {
            _busy = false;
            _refreshButton.Enabled = true;
        }
    }

    // Configuration dialog
    private async Task ShowConfigAsync()
    {
        if (_busy) return;
        using var dlg = new ConfigForm(_monitor, GetCurrentSwitches());
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null)
        {
            var r = dlg.Result;
            _monitor.UpdateConfiguration(r.Poll, r.MaxIf, r.UseIfXTable, r.Switches, r.TimeoutMs, r.Retries);
            _statusLabel.Text = "Saved new configuration";
            await RefreshDataAsync(manual:true);
            // Save settings to file
            PersistSettings(r);
        }
    }

    // Gather current switch list
    private List<SwitchInfo> GetCurrentSwitches()
    {
        // Use the real configuration from monitor
        return _monitor.GetSwitches().Select(s => new SwitchInfo{ Name=s.Name, IPAddress=s.IPAddress, Community=s.Community}).ToList();
    }

    // SNMP test on current switch
    private async Task TestCurrentAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null) return;
    _statusLabel.Text = "Testing SNMP...";
        var (ok,msg) = await _monitor.TestSnmpAsync(sw.IPAddress, sw.Community);
        _statusLabel.Text = msg;
    }

    // Diagnostic check
    private async Task DiagnoseAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null)
        {
            MessageBox.Show(this, "No switch is configured.", "Diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _statusLabel.Text = "Running diagnostics...";
            var text = await _monitor.TestDiagnosticAsync(sw.IPAddress, sw.Community);
            _statusLabel.Text = "Diagnostics completed";
            // Show in its own window for better readability
            using var diag = new Form
            {
                Text = $"Diagnostics - {sw.Name} ({sw.IPAddress})",
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
            _statusLabel.Text = "Diagnostics failed";
            MessageBox.Show(this, ex.Message, "Diagnostics error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // SNMP ping
    private async Task SnmpPingAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null)
        {
            MessageBox.Show(this, "No switch is configured.", "SNMP Ping", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _statusLabel.Text = "SNMP ping..."; // Keep short status
            var (ok,msg) = await _monitor.SnmpPingAsync(sw.IPAddress);
            _statusLabel.Text = msg;
            MessageBox.Show(this, msg, "SNMP Ping", MessageBoxButtons.OK, ok?MessageBoxIcon.Information:MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "SNMP ping error";
            MessageBox.Show(this, ex.Message, "SNMP Ping error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // List interfaces/ports of current switch
    private async Task ListInterfacesAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null)
        {
            MessageBox.Show(this, "No switch is configured.", "Port list", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _statusLabel.Text = "Fetching ports...";
            var (ok, list, err) = await _monitor.ListInterfacesAsync(sw.IPAddress, 256);
            if (!ok && list.Count == 0)
            {
                MessageBox.Show(this, err ?? "Unknown error", "Port list", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _statusLabel.Text = err ?? "Error";
                return;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var (idx,name) in list)
                sb.AppendLine($"{idx}: {name}");
            using var dlg = new Form
            {
                Text = $"Ports - {sw.Name}",
                Width = 480,
                Height = 600,
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
                Text = sb.ToString()
            };
            dlg.Controls.Add(tb);
            dlg.ShowDialog(this);
            _statusLabel.Text = $"Ports fetched: {list.Count}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Port list error";
            MessageBox.Show(this, ex.Message, "Port list error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Test SNMP communities
    private async Task TestCommunitiesAsync()
    {
        if (_busy) return;
        var sw = _monitor.GetSwitches().FirstOrDefault();
        if (sw == null)
        {
            MessageBox.Show(this, "No switch is configured.", "Community test", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            _statusLabel.Text = "Testing communities...";
            var (rows, note) = await _monitor.TestCommunitiesAsync(sw.IPAddress);
            _statusLabel.Text = note;
            var text = string.Join("\n", rows);
            using var dlg = new Form
            {
                Text = $"Community test - {sw.Name}",
                Width = 520,
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
            dlg.Controls.Add(tb);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Community test error";
            MessageBox.Show(this, ex.Message, "Community test", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Save config to file
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
                SnmpTimeoutMs = r.TimeoutMs,
                SnmpRetries = r.Retries,
                Switches = r.Switches
            }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Could not save config: " + ex.Message;
        }
    }

    // Bind/present snapshot list to grid
    private void BindGrid(List<InterfaceSnapshot> snaps)
    {
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var s in snaps.OrderBy(x => x.SwitchName).ThenBy(x => x.IfIndex))
        {
            var displaySwitch = s.SwitchName;
            int idx;
            if (_combined)
            {
                idx = _grid.Rows.Add(
                    displaySwitch,
                    s.SwitchIp,
                    s.IfIndex,
                    s.IfName,
                    s.Status,
                    FormatSpeedShort(s.SpeedLabel),
                    $"{FormatBps(s.InBps)}/{FormatBps(s.OutBps)}",
                    s.UtilInPercent.ToString("0.0"),
                    s.UtilOutPercent.ToString("0.0"),
                    s.Timestamp.ToLocalTime().ToString("HH:mm:ss")
                );
            }
            else
            {
                idx = _grid.Rows.Add(
                    displaySwitch,
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
            }
            var row = _grid.Rows[idx];
            if (s.Status == "ERR" || s.Status == "TIMEOUT" || s.Status == "AUTH" || s.Status == "SOCKET" || s.Status == "NOSUCH" || s.Status == "REFUSED")
            {
                row.DefaultCellStyle.BackColor = Color.DarkRed;
                row.DefaultCellStyle.ForeColor = Color.White;
                row.Cells[4].ToolTipText = s.Status switch
                {
                    "TIMEOUT" => "SNMP request timeout (no response within deadline)",
                    "AUTH" => "Authentication error (community / v3 user)",
                    "SOCKET" => "Socket error (network or port unavailable)",
                    "NOSUCH" => "MIB/OID responded with noSuchObject/Instance",
                    "REFUSED" => "Connection refused (port closed / ACL)",
                    _ => "General SNMP error"
                };
            }
            else if (s.Status.Equals("DOWN", StringComparison.OrdinalIgnoreCase))
            {
                row.DefaultCellStyle.BackColor = Color.FromArgb(90,60,0);
                row.DefaultCellStyle.ForeColor = Color.White;
                row.Cells[4].ToolTipText = "Interface operationally down (ifOperStatus=down)";
            }
        }
        _grid.ResumeLayout();
    }

    // Format bits-per-second value with units
    private static string FormatBps(double? bps)
    {
        if (!bps.HasValue) return "-";
        var v = bps.Value;
        if (v >= 1_000_000_000) return $"{v/1_000_000_000:0.00}G";
        if (v >= 1_000_000) return $"{v/1_000_000:0.00}M";
        if (v >= 1_000) return $"{v/1_000:0.00}K";
        return v.ToString("0");
    }

    // Format speed short string
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

    // Open live graph for selected row/port
    private void OpenGraphForSelected()
    {
        if (_grid.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a row first", "Graph", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var row = _grid.SelectedRows[0];
        if (row.Cells.Count < 3) return;
        var swName = row.Cells[0].Value?.ToString() ?? "?";
        var swIp = row.Cells[1].Value?.ToString() ?? "?";
        if (!int.TryParse(row.Cells[2].Value?.ToString(), out var ifIndex) || ifIndex <= 0)
        {
            MessageBox.Show(this, "Selected row is missing a valid ifIndex", "Graph", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Find existing graph window if already open
        foreach (Form f in Application.OpenForms)
        {
            if (f is PortGraphForm pg && pg.Matches(swIp, ifIndex))
            {
                pg.Focus();
                return;
            }
        }
        var graph = new PortGraphForm(swName, swIp, ifIndex);
    // Set initial sample if we have it in the snapshot list
        var snap = _lastSnapshots.FirstOrDefault(s => s.SwitchIp == swIp && s.IfIndex == ifIndex);
        if (snap != null)
            graph.AddSample(DateTime.Now, snap.InBps, snap.OutBps);
        graph.Show(this);
    }

    // Open historical graph for selected row/port
    private void OpenHistoricalGraphForSelected()
    {
        if (_grid.SelectedRows.Count == 0) { MessageBox.Show(this, "Select a row first", "History", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var row = _grid.SelectedRows[0];
        if (row.Cells.Count < 4) return;
        var swName = row.Cells[0].Value?.ToString() ?? "?";
        var swIp = row.Cells[1].Value?.ToString() ?? "?";
        if (!int.TryParse(row.Cells[2].Value?.ToString(), out var ifIndex) || ifIndex <= 0)
        {
            MessageBox.Show(this, "Selected row is missing a valid ifIndex", "History", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var ifName = row.Cells[3].Value?.ToString() ?? $"if{ifIndex}";
        // Check existing historical graph window
        foreach (Form f in Application.OpenForms)
        {
            if (f is HistoricalGraphForm hg && hg.Text.Contains(swName) && hg.Text.Contains($"ifIndex={ifIndex}"))
            {
                hg.Focus();
                return;
            }
        }
        var title = $"{swName} {ifName} ifIndex={ifIndex}";
        var hist = new HistoricalGraphForm(_monitor, swIp, ifIndex, title);
        hist.Show(this);
    }
}
