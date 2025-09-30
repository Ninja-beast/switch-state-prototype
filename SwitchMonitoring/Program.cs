using System.Text.Json;
using SwitchMonitoring.Models;
using SwitchMonitoring.Services;
using SwitchMonitoring.Forms;

// Simple WinForms startup without designer
ApplicationConfiguration.Initialize();

// Global exception handling
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    try
    {
        AppLogger.Error("UNHANDLED: " + e.ExceptionObject);
    }
    catch { }
};
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    try
    {
        AppLogger.Error("TASK-UNOBSERVED: " + e.Exception);
        e.SetObserved();
    }
    catch { }
};

try
{

// Load user preferences early
UserPreferences.Load();

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
AppLogger.Info("Starting application");
if (!File.Exists(configPath))
{
    AppLogger.Error("Missing appsettings.json");
    MessageBox.Show("Cannot find appsettings.json", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}
var json = File.ReadAllText(configPath);
var root = JsonDocument.Parse(json).RootElement;
int pollInterval = root.GetProperty("PollIntervalSeconds").GetInt32();
bool combined = root.TryGetProperty("ShowCombinedBps", out var scb) && (scb.ValueKind==JsonValueKind.True || scb.ValueKind==JsonValueKind.False) ? scb.GetBoolean() : false;
bool showSnmpErr = root.TryGetProperty("ShowSnmpErrorDetails", out var sed) && (sed.ValueKind==JsonValueKind.True || sed.ValueKind==JsonValueKind.False) ? sed.GetBoolean() : false;

// Alltid initialiser begge, Unified form styrer visning
int maxIf2 = root.TryGetProperty("MaxInterfaces", out var mi2) && mi2.ValueKind==JsonValueKind.Number ? mi2.GetInt32() : 10;
bool useIfX2 = root.TryGetProperty("UseIfXTable", out var uix2) && (uix2.ValueKind==JsonValueKind.True || uix2.ValueKind==JsonValueKind.False) ? uix2.GetBoolean() : true;
int? snmpTimeout2 = root.TryGetProperty("SnmpTimeoutMs", out var sto2) && sto2.ValueKind==JsonValueKind.Number ? sto2.GetInt32() : null;
int? snmpRetries2 = root.TryGetProperty("SnmpRetries", out var sr2) && sr2.ValueKind==JsonValueKind.Number ? sr2.GetInt32() : null;
int defaultSnmpPort = root.TryGetProperty("DefaultSnmpPort", out var dsp) && dsp.ValueKind==JsonValueKind.Number ? dsp.GetInt32() : 161;
bool probeLogEnabled = root.TryGetProperty("ProbeLogEnabled", out var ple) && (ple.ValueKind==JsonValueKind.True || ple.ValueKind==JsonValueKind.False) ? ple.GetBoolean() : true;
string? probeLogFile = root.TryGetProperty("ProbeLogFile", out var plf) && plf.ValueKind==JsonValueKind.String ? plf.GetString() : null;
// SNMPv3 users (optional)
var v3Users = new Dictionary<string,SnmpV3User>(StringComparer.OrdinalIgnoreCase);
if (root.TryGetProperty("SnmpV3Users", out var v3Arr) && v3Arr.ValueKind==JsonValueKind.Array)
{
    foreach (var u in v3Arr.EnumerateArray())
    {
        try
        {
            var name = u.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var user = new SnmpV3User{ Name = name! };
            if (u.TryGetProperty("AuthProtocol", out var ap) && ap.ValueKind==JsonValueKind.String) user.AuthProtocol = ap.GetString()!;
            if (u.TryGetProperty("AuthPassword", out var apw) && apw.ValueKind==JsonValueKind.String) user.AuthPassword = apw.GetString();
            if (u.TryGetProperty("PrivProtocol", out var pp) && pp.ValueKind==JsonValueKind.String) user.PrivProtocol = pp.GetString()!;
            if (u.TryGetProperty("PrivPassword", out var ppw) && ppw.ValueKind==JsonValueKind.String) user.PrivPassword = ppw.GetString();
            if (u.TryGetProperty("Context", out var ctx) && ctx.ValueKind==JsonValueKind.String) user.Context = ctx.GetString();
            v3Users[user.Name] = user;
        }
        catch { }
    }
}
// Ports to auto-probe if a switch does not specify an explicit SnmpPort
var probePorts = new List<int>();
if (root.TryGetProperty("SnmpProbePorts", out var spp) && spp.ValueKind == JsonValueKind.Array)
{
    foreach (var p in spp.EnumerateArray())
    {
        if (p.ValueKind == JsonValueKind.Number)
        {
            var portVal = p.GetInt32();
            if (!probePorts.Contains(portVal)) probePorts.Add(portVal);
        }
    }
}
if (probePorts.Count == 0)
{
    probePorts.Add(defaultSnmpPort);
}
var switchList2 = new List<SwitchInfo>();
if (root.TryGetProperty("Switches", out var swArr2) && swArr2.ValueKind == JsonValueKind.Array)
{
    foreach (var s in swArr2.EnumerateArray())
    {
        try
        {
            var name = s.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var ip = s.TryGetProperty("IPAddress", out var ipEl) ? ipEl.GetString() : null;
            var comm = s.TryGetProperty("Community", out var c) ? c.GetString() : null;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(comm))
            {
                var swInfo = new SwitchInfo{ Name = name!, IPAddress = ip!, Community = comm! };
                if (s.TryGetProperty("SnmpPort", out var spProp) && spProp.ValueKind==JsonValueKind.Number)
                {
                    swInfo.SnmpPort = spProp.GetInt32();
                }
                if (s.TryGetProperty("SnmpV3User", out var uProp) && uProp.ValueKind==JsonValueKind.String)
                {
                    swInfo.SnmpV3User = uProp.GetString();
                }
                if (s.TryGetProperty("IncludeIfIndices", out var inclArr) && inclArr.ValueKind==JsonValueKind.Array)
                {
                    var list = new List<int>();
                    foreach (var v in inclArr.EnumerateArray())
                        if (v.ValueKind==JsonValueKind.Number) list.Add(v.GetInt32());
                    if (list.Count > 0) swInfo.IncludeIfIndices = list;
                }
                switchList2.Add(swInfo);
            }
        }
        catch { }
    }
}
var monitor2 = new SwitchMonitor(pollInterval, maxIf2, useIfX2, switchList2, snmpTimeout2, snmpRetries2, showSnmpErr, defaultSnmpPort, probePorts, probeLogEnabled, probeLogFile);
// Provide SNMPv3 users if defined
if (v3Users.Count > 0) monitor2.SetV3Users(v3Users.Values);
// CommunitiesToTest (multi-community) from config
if (root.TryGetProperty("CommunitiesToTest", out var ctest) && ctest.ValueKind==JsonValueKind.Array)
{
    var comms = new List<string>();
    foreach (var ce in ctest.EnumerateArray())
        if (ce.ValueKind==JsonValueKind.String) comms.Add(ce.GetString()!);
    if (comms.Count > 0) SwitchMonitor.SetCommunitiesToTest(comms);
}
AppLogger.Info($"Starting SNMP form Switches={switchList2.Count}");
Application.Run(new MainForm(monitor2, pollInterval, combined));
}
catch (Exception ex)
{
    AppLogger.Error("Fatal startup error: " + ex);
    MessageBox.Show("Fatal error during startup: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
