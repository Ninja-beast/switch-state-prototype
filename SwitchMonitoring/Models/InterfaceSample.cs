using System;

namespace SwitchMonitoring.Models;

public class InterfaceSample
{
    public int IfIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong InOctets { get; set; }
    public ulong OutOctets { get; set; }
    public DateTime Timestamp { get; set; }
    // Kalkulerte felt for sFlow (bps mellom to counter samples)
    public double RateInBps { get; set; }
    public double RateOutBps { get; set; }
    // Valgfri visning av hastighet (hvis vi senere henter ifSpeed)
    public string SpeedLabel { get; set; } = "-";
}
