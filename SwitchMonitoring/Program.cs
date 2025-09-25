using System.Text.Json;
using SwitchMonitoring.Models;
using SwitchMonitoring.Services;
using SwitchMonitoring.Forms;

// Enkle WinForms oppstart uten generert designer
ApplicationConfiguration.Initialize();

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(configPath))
{
    MessageBox.Show("Finner ikke appsettings.json", "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
    return;
}
var json = File.ReadAllText(configPath);
var root = JsonDocument.Parse(json).RootElement;
int pollInterval = root.GetProperty("PollIntervalSeconds").GetInt32();
int maxInterfaces = root.GetProperty("MaxInterfaces").GetInt32();
bool useIfXTable = root.TryGetProperty("UseIfXTable", out var useX) && useX.GetBoolean();

var switchList = new List<SwitchInfo>();
foreach (var sw in root.GetProperty("Switches").EnumerateArray())
{
    switchList.Add(new SwitchInfo
    {
        Name = sw.GetProperty("Name").GetString()!,
        IPAddress = sw.GetProperty("IPAddress").GetString()!,
        Community = sw.GetProperty("Community").GetString()!
    });
}

var monitor = new SwitchMonitor(pollInterval, maxInterfaces, useIfXTable, switchList);
Application.Run(new MainForm(monitor, pollInterval));
