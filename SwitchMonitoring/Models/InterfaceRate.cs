using System;

namespace SwitchMonitoring.Models;

public class InterfaceRate
{
    public int IfIndex { get; set; }
    public string Name { get; set; } = string.Empty;
    public double InBps { get; set; }
    public double OutBps { get; set; }
    public double UtilizationInPercent { get; set; }
    public double UtilizationOutPercent { get; set; }
}
