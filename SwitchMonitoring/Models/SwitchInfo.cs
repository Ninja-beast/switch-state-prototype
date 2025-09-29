namespace SwitchMonitoring.Models;

public class SwitchInfo
{
    public string Name { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public string Community { get; set; } = "public";
    // Optional custom SNMP UDP port (default 161 if null)
    public int? SnmpPort { get; set; }
    // Optional explicit list of interface indices to poll (overrides MaxInterfaces if provided)
    public List<int>? IncludeIfIndices { get; set; }
    // Optional SNMPv3 user to use (if set, we attempt v3 first)
    public string? SnmpV3User { get; set; }
}
