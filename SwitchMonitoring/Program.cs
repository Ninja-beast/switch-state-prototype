using System.Text.Json;
using SwitchMonitoring.Models;
using SwitchMonitoring.Services;
using SwitchMonitoring.Forms;

// Enkle WinForms oppstart uten generert designer
ApplicationConfiguration.Initialize();

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
AppLogger.Info("Starter applikasjon");
if (!File.Exists(configPath))
{
    AppLogger.Error("Mangler appsettings.json");
    MessageBox.Show("Finner ikke appsettings.json", "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}
var json = File.ReadAllText(configPath);
var root = JsonDocument.Parse(json).RootElement;
int pollInterval = root.GetProperty("PollIntervalSeconds").GetInt32();
string mode = root.TryGetProperty("Mode", out var modeProp) && modeProp.ValueKind==JsonValueKind.String ? modeProp.GetString()! : (root.TryGetProperty("UseSFlow", out var usf) && usf.GetBoolean() ? "SFlow" : "SNMP");
int fallbackSeconds = root.TryGetProperty("SFlowFallbackSeconds", out var fbs) && fbs.ValueKind==JsonValueKind.Number ? fbs.GetInt32() : 60;
int sflowPort = root.TryGetProperty("SFlowPort", out var sfp) ? sfp.GetInt32() : 6343;
bool sflowDebug = root.TryGetProperty("SFlowDebug", out var sdbg) && sdbg.GetBoolean();
string? bindIp = root.TryGetProperty("SFlowBindIP", out var bip) ? bip.GetString() : null;
int dumpFirstN = root.TryGetProperty("SFlowDumpFirstN", out var dfn) && dfn.ValueKind==JsonValueKind.Number ? dfn.GetInt32() : 0;
bool combined = root.TryGetProperty("ShowCombinedBps", out var scb) && scb.ValueKind==JsonValueKind.True || (scb.ValueKind==JsonValueKind.False && scb.GetBoolean());
bool showSnmpErr = root.TryGetProperty("ShowSnmpErrorDetails", out var sed) && (sed.ValueKind==JsonValueKind.True || sed.ValueKind==JsonValueKind.False) ? sed.GetBoolean() : false;
// Last agent mapping (frivillig)
var agentNameMap = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
if (root.TryGetProperty("SFlowAgents", out var agentsElem) && agentsElem.ValueKind == JsonValueKind.Array)
{
    foreach (var a in agentsElem.EnumerateArray())
    {
        if (a.TryGetProperty("IPAddress", out var ipProp) && a.TryGetProperty("Name", out var nProp))
        {
            var ip = ipProp.GetString(); var name = nProp.GetString();
            if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(name))
            {
                agentNameMap[ip!] = name!;
            }
        }
    }
}

// Alltid initialiser begge, Unified form styrer visning
int maxIf2 = root.TryGetProperty("MaxInterfaces", out var mi2) && mi2.ValueKind==JsonValueKind.Number ? mi2.GetInt32() : 10;
bool useIfX2 = root.TryGetProperty("UseIfXTable", out var uix2) && (uix2.ValueKind==JsonValueKind.True || uix2.ValueKind==JsonValueKind.False) ? uix2.GetBoolean() : true;
int? snmpTimeout2 = root.TryGetProperty("SnmpTimeoutMs", out var sto2) && sto2.ValueKind==JsonValueKind.Number ? sto2.GetInt32() : null;
int? snmpRetries2 = root.TryGetProperty("SnmpRetries", out var sr2) && sr2.ValueKind==JsonValueKind.Number ? sr2.GetInt32() : null;
int defaultSnmpPort = root.TryGetProperty("DefaultSnmpPort", out var dsp) && dsp.ValueKind==JsonValueKind.Number ? dsp.GetInt32() : 161;
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
                switchList2.Add(swInfo);
            }
        }
        catch { }
    }
}
var monitor2 = new SwitchMonitor(pollInterval, maxIf2, useIfX2, switchList2, snmpTimeout2, snmpRetries2, showSnmpErr, defaultSnmpPort);
var collector2 = string.IsNullOrWhiteSpace(bindIp)
    ? new SFlowCollector(sflowPort, sflowDebug, dumpFirstN)
    : new SFlowCollector(sflowPort, sflowDebug, bindIp!, dumpFirstN);
AppLogger.Info($"Starter Unified form Mode={mode} SNMPswitches={switchList2.Count} sFlowPort={sflowPort}");
Application.Run(new UnifiedMainForm(monitor2, collector2, pollInterval, agentNameMap, mode, fallbackSeconds, combined));
