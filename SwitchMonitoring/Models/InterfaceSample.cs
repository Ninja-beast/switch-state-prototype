using System;

namespace SwitchMonitoring.Models;

public class InterfaceSample
{
    public int IfIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong InOctets { get; set; }
    public ulong OutOctets { get; set; }
    public DateTime Timestamp { get; set; }
    // Calculated fields for sFlow (bps between two counter samples)
    public double RateInBps { get; set; }
    public double RateOutBps { get; set; }
    // Optional display of speed (if we later fetch ifSpeed)
    public string SpeedLabel { get; set; } = "-";
}
