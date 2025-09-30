using SwitchMonitoring.Services;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Forms;

public class UnifiedMainForm : Form
{
    private readonly SwitchMonitor _snmpMonitor;
    private readonly SFlowCollector _sflowCollector;
    private readonly int _pollInterval;
    private readonly Dictionary<string,string> _agentMap;
    private readonly TabControl _tabs = new();
    private readonly ComboBox _modeCombo = new();
    private readonly Button _applyButton = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _fallbackTimer = new();
    private readonly int _fallbackSeconds;
    private DateTime _startTime;
    private string _initialMode; // Auto | SNMP | SFlow

    private MainForm? _snmpForm; // embedded
    private SFlowMainForm? _sflowForm; // embedded

    private readonly bool _combined;
    public UnifiedMainForm(SwitchMonitor snmpMonitor, SFlowCollector sflowCollector, int pollInterval, Dictionary<string,string> agentMap, string mode, int fallbackSeconds, bool combined)
    {
        _snmpMonitor = snmpMonitor;
        _sflowCollector = sflowCollector;
        _pollInterval = pollInterval;
        _agentMap = agentMap;
        _initialMode = mode;
        _fallbackSeconds = Math.Max(10, fallbackSeconds);
        _combined = combined;
        Text = "Switch Trafikk (Unified)";
        Width = 1250; Height = 780;
        BackColor = Color.FromArgb(30,30,34);
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI",9F);

        _tabs.Dock = DockStyle.Fill;
        Controls.Add(_tabs);

        var topPanel = new Panel{Dock=DockStyle.Top, Height=38, Padding=new Padding(6,6,6,2), BackColor=Color.FromArgb(25,25,28)};
        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.Items.AddRange(new[]{"Auto","SNMP","SFlow"});
        _modeCombo.SelectedItem = _initialMode;
        _modeCombo.Width = 100;
        _applyButton.Text = "Bytt";
        _applyButton.Width = 60;
        _applyButton.Click += (s,e)=> ApplyModeChange();
        topPanel.Controls.Add(_modeCombo);
        topPanel.Controls.Add(_applyButton);
        _modeCombo.Left = 4; _modeCombo.Top = 6;
        _applyButton.Left = _modeCombo.Right + 6; _applyButton.Top = 6;
        Controls.Add(topPanel);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 22;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.BackColor = Color.FromArgb(25,25,28);
        Controls.Add(_status);

        _fallbackTimer.Interval = 3000; // check every 3s
        _fallbackTimer.Tick += (s,e)=> CheckAutoFallback();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _startTime = DateTime.UtcNow;
        EnsureEmbeddedForms();
        ActivateMode(_initialMode, log:true);
        if (_initialMode == "Auto") _fallbackTimer.Start();
    }

    private void EnsureEmbeddedForms()
    {
        if (_snmpForm == null)
        {
            _snmpForm = new MainForm(_snmpMonitor, _pollInterval, _combined){TopLevel=false, Dock=DockStyle.Fill, FormBorderStyle=FormBorderStyle.None};
            var tp = new TabPage("SNMP"); tp.Controls.Add(_snmpForm); _tabs.TabPages.Add(tp); _snmpForm.Show();
        }
        if (_sflowForm == null)
        {
            _sflowForm = new SFlowMainForm(_sflowCollector, _pollInterval, _agentMap, _combined){TopLevel=false, Dock=DockStyle.Fill, FormBorderStyle=FormBorderStyle.None};
            var tp = new TabPage("sFlow"); tp.Controls.Add(_sflowForm); _tabs.TabPages.Add(tp); _sflowForm.Show();
        }
    }

    private void ActivateMode(string mode, bool log=false)
    {
        if (log) AppLogger.Info("Enables mode " + mode);
        if (mode == "SNMP")
        {
            _tabs.SelectedIndex = 0; // SNMP tab
            _status.Text = "Mode: SNMP";
        }
        else if (mode == "SFlow")
        {
            _tabs.SelectedIndex = 1; // sFlow tab
            _status.Text = "Mode: sFlow";
        }
        else // Auto
        {
            // initial preference: sFlow first (passive) - show sFlow tab
            _tabs.SelectedIndex = 1;
            _status.Text = "Mode: Auto (starts sFlow, fallback SNMP after timeout)";
        }
        _initialMode = mode;
    }

    private void CheckAutoFallback()
    {
        if (_initialMode != "Auto") return;
        var sinceStart = (DateTime.UtcNow - _startTime).TotalSeconds;
        if (sinceStart < _fallbackSeconds) return;
        // no fallback if we already got sFlow traffic
        if (_sflowCollector.RawDatagramCount > 0) return;
        // fallback to SNMP
        _fallbackTimer.Stop();
        AppLogger.Warn($"No sFlow traffic after {_fallbackSeconds}s – falls back to SNMP");
        ActivateMode("SNMP", log:true);
        _modeCombo.SelectedItem = "SNMP";
        PersistMode("SNMP");
    }

    private void ApplyModeChange()
    {
        var sel = _modeCombo.SelectedItem?.ToString() ?? "Auto";
        ActivateMode(sel, log:true);
        if (sel == "Auto")
        {
            _startTime = DateTime.UtcNow; _fallbackTimer.Start();
        }
        else
        {
            _fallbackTimer.Stop();
        }
        PersistMode(sel);
    }

    private void PersistMode(string mode)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            using var ms = new MemoryStream();
            using var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions{Indented=true});
            var root = doc.RootElement;
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("Mode")) continue; // override below
                prop.WriteTo(w);
            }
            w.WriteString("Mode", mode);
            w.WriteEndObject();
            w.Flush();
            File.WriteAllBytes(path, ms.ToArray());
            _status.Text = $"Saved Mode={mode}";
        }
        catch (Exception ex)
        {
            _status.Text = "Failed to save Mode: " + ex.Message;
        }
    }
}
