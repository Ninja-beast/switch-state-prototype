namespace SwitchMonitoring.Models;

public class SnmpV3User
{
    public string Name { get; set; } = string.Empty; // username
    public string AuthProtocol { get; set; } = "SHA"; // SHA|MD5|NONE
    public string? AuthPassword { get; set; }
    public string PrivProtocol { get; set; } = "NONE"; // AES|DES|NONE
    public string? PrivPassword { get; set; }
    public string? Context { get; set; }
    public override string ToString() => $"SnmpV3User(Name={Name},Auth={AuthProtocol},Priv={PrivProtocol})";
}
