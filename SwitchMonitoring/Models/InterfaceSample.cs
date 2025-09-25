using System;

namespace SwitchMonitoring.Models;

public class InterfaceSample
{
    public int IfIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong InOctets { get; set; }
    public ulong OutOctets { get; set; }
    public DateTime Timestamp { get; set; }
}
