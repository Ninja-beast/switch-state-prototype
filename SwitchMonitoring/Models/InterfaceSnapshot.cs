namespace SwitchMonitoring.Models;

public class InterfaceSnapshot
{
    public DateTime Timestamp { get; set; }
    public string SwitchName { get; set; } = string.Empty;
    public string SwitchIp { get; set; } = string.Empty;
    public int IfIndex { get; set; }
    public string IfName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double InBps { get; set; }
    public double OutBps { get; set; }
    public double UtilInPercent { get; set; }
    public double UtilOutPercent { get; set; }
    public string SpeedLabel { get; set; } = string.Empty;
        public string Id => $"{SwitchName}-{IfIndex}";
        // Opprinnelig felter (kan beholdes for bakoverkompabilitet hvis brukt andre steder)
        public double? UtilizationInPercent
        {
            get => UtilInPercent;
            set { if (value.HasValue) UtilInPercent = value.Value; }
        }
        public double? UtilizationOutPercent
        {
            get => UtilOutPercent;
            set { if (value.HasValue) UtilOutPercent = value.Value; }
        }
        public DateTime LastUpdated
        {
            get => Timestamp;
            set => Timestamp = value;
        }
        public string Speed { get; set; } = string.Empty; // rå ifSpeed verdi
    }
