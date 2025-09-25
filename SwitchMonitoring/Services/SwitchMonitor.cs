using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Services;

public class SwitchMonitor
{
    private static int _requestId = 1;
    private int _pollIntervalSeconds;
    private int _maxInterfaces;
    private bool _useIfXTable;
    private List<SwitchInfo> _switches;
    private readonly Dictionary<string, Dictionary<int, InterfaceSample>> _lastSamples = new();

    public SwitchMonitor(int pollIntervalSeconds, int maxInterfaces, bool useIfXTable, List<SwitchInfo> switches)
    {
        _pollIntervalSeconds = pollIntervalSeconds;
        _maxInterfaces = maxInterfaces;
        _useIfXTable = useIfXTable;
        _switches = switches;
    }

    public IReadOnlyList<SwitchInfo> GetSwitches() => _switches.AsReadOnly();
    public int GetPollInterval() => _pollIntervalSeconds;
    public int GetMaxInterfaces() => _maxInterfaces;
    public bool GetUseIfXTable() => _useIfXTable;

    public void UpdateConfiguration(int pollIntervalSeconds, int maxInterfaces, bool useIfXTable, List<SwitchInfo> switches)
    {
        _pollIntervalSeconds = pollIntervalSeconds;
        _maxInterfaces = maxInterfaces;
        _useIfXTable = useIfXTable;
        _switches = switches;
        // Ikke tøm samples helt – men fjern de som ikke finnes lenger
        var validIps = new HashSet<string>(_switches.Select(s => s.IPAddress));
        foreach (var key in _lastSamples.Keys.ToList())
        {
            if (!validIps.Contains(key))
                _lastSamples.Remove(key);
        }
    }

    public async Task<(bool success,string message)> TestSnmpAsync(string ip, string community)
    {
        if (!PingHost(ip)) return (false, "Ping feilet");
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(ip), 161);
            var sysName = await GetSingleAsync(ep, community, "1.3.6.1.2.1.1.5.0");
            var ifCount = await GetSingleAsync(ep, community, "1.3.6.1.2.1.2.1.0");
            return (true, $"SNMP OK – sysName={sysName}, ifCount={ifCount}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string> TestDiagnosticAsync(string ip, string community)
    {
        var report = new List<string>();
        bool ping = PingHost(ip);
        report.Add($"Ping: {(ping?"OK":"FEIL")}");
        var ep = new IPEndPoint(IPAddress.Parse(ip), 161);
        async Task probe(string label,string oid)
        {
            try
            {
                var val = await GetSingleAsync(ep, community, oid);
                report.Add($"{label} ({oid}): OK -> {Truncate(val,40)}");
            }
            catch (Exception ex)
            {
                report.Add($"{label} ({oid}): FEIL -> {ex.Message}");
            }
        }
        await probe("sysDescr", "1.3.6.1.2.1.1.1.0");
        await probe("sysName", "1.3.6.1.2.1.1.5.0");
        await probe("ifNumber", "1.3.6.1.2.1.2.1.0");
        // Test en interface beskrivelse (ifDescr.1) kan feile hvis interface 1 ikke finnes, ignorer det som informativt
        await probe("ifDescr.1", "1.3.6.1.2.1.2.2.1.2.1");
        var text = string.Join("\n", report);
        LogToFile("DIAG", ip + "\n" + text);
        return text;
    }

    private static void LogToFile(string tag, string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "snmp-log.txt");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {message}\n");
        }
        catch { /* ignorér logging-feil */ }
    }

    public async Task RunAsync()
    {
        Console.WriteLine("Starter SNMP overvåkning... (Ctrl+C for å stoppe)\n");
        while (true)
        {
            foreach (var sw in _switches)
            {
                await MonitorOneAsync(sw);
            }
            Console.WriteLine(new string('-', 70));
            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds));
        }
    }

    // Ny metode: kjør ett enkelt poll og returner snapshots for alle interfaces
    public async Task<List<InterfaceSnapshot>> PollOnceAsync()
    {
        var list = new List<InterfaceSnapshot>();
        foreach (var sw in _switches)
        {
            var ep = new IPEndPoint(IPAddress.Parse(sw.IPAddress), 161);
            try
            {
                bool pingOk = PingHost(sw.IPAddress);
                if (!pingOk)
                {
                    // Fortsett – noen enheter blokkerer ICMP selv om SNMP svarer.
                    Console.WriteLine($"Advarsel: Ping feilet mot {sw.IPAddress}, forsøker SNMP likevel.");
                }
                var ifCountStr = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.2.1.0");
                if (!int.TryParse(ifCountStr, out var ifCount)) ifCount = 0;
                var max = Math.Min(ifCount, _maxInterfaces);
                if (!_lastSamples.ContainsKey(sw.IPAddress))
                    _lastSamples[sw.IPAddress] = new();
                for (int i = 1; i <= max; i++)
                {
                    var snap = await CollectSnapshotAsync(ep, sw, i);
                    if (snap != null) list.Add(snap);
                }
            }
            catch (Exception ex)
            {
                list.Add(new InterfaceSnapshot
                {
                    SwitchName = sw.Name,
                    SwitchIp = sw.IPAddress,
                    IfIndex = 0,
                    IfName = "(ingen data)",
                    Status = "ERR",
                    SpeedLabel = "-",
                    InBps = 0,
                    OutBps = 0,
                    UtilInPercent = 0,
                    UtilOutPercent = 0,
                    Timestamp = DateTime.UtcNow,
                    // reuse SpeedLabel or Status only; error text to console
                });
                Console.WriteLine($"Feil ved polling {sw.IPAddress}: {ex.Message}");
            }
        }
        return list;
    }

    private static bool PingHost(string ip)
    {
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var r = p.Send(ip, 1000);
            return r.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch { return false; }
    }

    private async Task MonitorOneAsync(SwitchInfo sw)
    {
        Console.WriteLine($"Switch: {sw.Name} ({sw.IPAddress})");
        var ep = new IPEndPoint(IPAddress.Parse(sw.IPAddress), 161);
        try
        {
            var sysName = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.1.5.0");
            var sysUp = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.1.3.0");
            Console.WriteLine($"  System Name: {sysName}");
            Console.WriteLine($"  Uptime: {FormatUptime(sysUp)}");
            await ShowInterfacesAsync(ep, sw);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Feil: {ex.Message}");
        }
    }

    private async Task ShowInterfacesAsync(IPEndPoint ep, SwitchInfo sw)
    {
        try
        {
            var ifCountStr = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.2.1.0");
            if (!int.TryParse(ifCountStr, out var ifCount)) ifCount = 0;
            Console.WriteLine($"  Antall interfaces (ifTable): {ifCount}");

            var max = Math.Min(ifCount, _maxInterfaces);
            if (!_lastSamples.ContainsKey(sw.IPAddress))
                _lastSamples[sw.IPAddress] = new();

            for (int i = 1; i <= max; i++)
            {
                await CollectInterfaceAsync(ep, sw, i);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Feil ved henting av interface data: {ex.Message}");
        }
    }

    private async Task CollectInterfaceAsync(IPEndPoint ep, SwitchInfo sw, int ifIndex)
    {
        try
        {
            string descr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.2.{ifIndex}");
            string oper = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.8.{ifIndex}");
            string speedStr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.5.{ifIndex}");
            ulong inOct = await GetCounterAsync(ep, sw, ifIndex, true);
            ulong outOct = await GetCounterAsync(ep, sw, ifIndex, false);

            var nowSample = new InterfaceSample
            {
                IfIndex = ifIndex,
                Name = descr,
                InOctets = inOct,
                OutOctets = outOct,
                Timestamp = DateTime.UtcNow
            };

            InterfaceRate? rate = null;
            if (_lastSamples[sw.IPAddress].TryGetValue(ifIndex, out var prev))
            {
                var deltaSeconds = (nowSample.Timestamp - prev.Timestamp).TotalSeconds;
                if (deltaSeconds > 0)
                {
                    ulong deltaIn = CounterDelta(prev.InOctets, nowSample.InOctets);
                    ulong deltaOut = CounterDelta(prev.OutOctets, nowSample.OutOctets);
                    double inBps = deltaIn * 8 / deltaSeconds;
                    double outBps = deltaOut * 8 / deltaSeconds;
                    double speed = double.TryParse(speedStr, out var sp) ? sp : 0;
                    double utilIn = speed > 0 ? (inBps / speed) * 100 : 0;
                    double utilOut = speed > 0 ? (outBps / speed) * 100 : 0;
                    rate = new InterfaceRate
                    {
                        IfIndex = ifIndex,
                        Name = descr,
                        InBps = inBps,
                        OutBps = outBps,
                        UtilizationInPercent = utilIn,
                        UtilizationOutPercent = utilOut
                    };
                }
            }
            _lastSamples[sw.IPAddress][ifIndex] = nowSample;

            string status = oper == "1" ? "UP" : oper == "2" ? "DOWN" : oper;
            Console.WriteLine($"    {ifIndex,2}: {Truncate(descr,28),-28} {status,-4} {FormatSpeed(speedStr),8} " +
                              (rate != null ? $"In:{FormatBits(rate.InBps),10} Out:{FormatBits(rate.OutBps),10} Util:{rate.UtilizationInPercent,5:F1}%/{rate.UtilizationOutPercent,5:F1}%" : "(samler første prøve)"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {ifIndex,2}: Feil - {ex.Message}");
        }
    }

    private async Task<InterfaceSnapshot?> CollectSnapshotAsync(IPEndPoint ep, SwitchInfo sw, int ifIndex)
    {
        try
        {
            string descr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.2.{ifIndex}");
            string oper = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.8.{ifIndex}");
            string speedStr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.5.{ifIndex}");
            ulong inOct = await GetCounterAsync(ep, sw, ifIndex, true);
            ulong outOct = await GetCounterAsync(ep, sw, ifIndex, false);
            var nowSample = new InterfaceSample
            {
                IfIndex = ifIndex,
                Name = descr,
                InOctets = inOct,
                OutOctets = outOct,
                Timestamp = DateTime.UtcNow
            };
            InterfaceRate? rate = null;
            if (_lastSamples[sw.IPAddress].TryGetValue(ifIndex, out var prev))
            {
                var deltaSeconds = (nowSample.Timestamp - prev.Timestamp).TotalSeconds;
                if (deltaSeconds > 0)
                {
                    ulong deltaIn = CounterDelta(prev.InOctets, nowSample.InOctets);
                    ulong deltaOut = CounterDelta(prev.OutOctets, nowSample.OutOctets);
                    double inBps = deltaIn * 8 / deltaSeconds;
                    double outBps = deltaOut * 8 / deltaSeconds;
                    double speed = double.TryParse(speedStr, out var sp) ? sp : 0;
                    double utilIn = speed > 0 ? (inBps / speed) * 100 : 0;
                    double utilOut = speed > 0 ? (outBps / speed) * 100 : 0;
                    rate = new InterfaceRate
                    {
                        IfIndex = ifIndex,
                        Name = descr,
                        InBps = inBps,
                        OutBps = outBps,
                        UtilizationInPercent = utilIn,
                        UtilizationOutPercent = utilOut
                    };
                }
            }
            _lastSamples[sw.IPAddress][ifIndex] = nowSample;
            var snap = new InterfaceSnapshot
            {
                SwitchName = sw.Name,
                SwitchIp = sw.IPAddress,
                IfIndex = ifIndex,
                IfName = descr,
                Status = oper == "1" ? "UP" : oper == "2" ? "DOWN" : oper,
                SpeedLabel = speedStr,
                Timestamp = nowSample.Timestamp
            };
            if (rate != null)
            {
                snap.InBps = rate.InBps;
                snap.OutBps = rate.OutBps;
                snap.UtilInPercent = rate.UtilizationInPercent;
                snap.UtilOutPercent = rate.UtilizationOutPercent;
            }
            return snap;
        }
        catch
        {
            return null;
        }
    }

    private async Task<ulong> GetCounterAsync(IPEndPoint ep, SwitchInfo sw, int ifIndex, bool inbound)
    {
        // Prefer 64-bit ifXTable if enabled
        if (_useIfXTable)
        {
            string ifHC = inbound ? $"1.3.6.1.2.1.31.1.1.1.6.{ifIndex}" : $"1.3.6.1.2.1.31.1.1.1.10.{ifIndex}"; // ifHCInOctets / ifHCOutOctets
            var val = await TryGetSingleAsync(ep, sw.Community, ifHC);
            if (val.success && ulong.TryParse(val.value, out var hc))
                return hc;
        }
        string oid = inbound ? $"1.3.6.1.2.1.2.2.1.10.{ifIndex}" : $"1.3.6.1.2.1.2.2.1.16.{ifIndex}"; // ifInOctets / ifOutOctets
        var legacy = await GetSingleAsync(ep, sw.Community, oid);
        return ulong.TryParse(legacy, out var v) ? v : 0UL;
    }

    private static ulong CounterDelta(ulong prev, ulong current)
    {
        if (current >= prev) return current - prev;
        // wrap, assume 32-bit wrap if prev large and current small
        ulong wrapPoint = prev > uint.MaxValue ? ulong.MaxValue : uint.MaxValue;
        return (wrapPoint - prev) + current;
    }

    private async Task<string> GetSingleAsync(IPEndPoint ep, string community, string oid)
    {
        var varbinds = new List<Variable> { new(new ObjectIdentifier(oid)) };
        int id = Interlocked.Increment(ref _requestId);
        var msg = new GetRequestMessage(id, VersionCode.V2, new OctetString(community), varbinds);
        var response = await msg.GetResponseAsync(ep).ConfigureAwait(false);
        var pdu = response.Pdu();
        if (pdu.ErrorStatus.ToInt32() != 0)
            throw new Exception($"SNMP error {pdu.ErrorStatus}");
        return pdu.Variables[0].Data.ToString();
    }

    private async Task<(bool success,string value)> TryGetSingleAsync(IPEndPoint ep, string community, string oid)
    {
        try
        {
            var v = await GetSingleAsync(ep, community, oid);
            return (true, v);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    private static string FormatUptime(string ticks)
    {
        if (long.TryParse(ticks, out var tv))
        {
            var ts = TimeSpan.FromMilliseconds(tv * 10);
            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        }
        return "?";
    }

    private static string FormatSpeed(string bps)
    {
        if (double.TryParse(bps, out var sp))
        {
            if (sp >= 1_000_000_000) return $"{sp/1_000_000_000:0.#}G";
            if (sp >= 1_000_000) return $"{sp/1_000_000:0.#}M";
            if (sp >= 1_000) return $"{sp/1_000:0.#}K";
            return sp.ToString("0");
        }
        return "-";
    }

    private static string FormatBits(double bps)
    {
        if (bps >= 1_000_000_000) return $"{bps/1_000_000_000:0.00}Gbps";
        if (bps >= 1_000_000) return $"{bps/1_000_000:0.00}Mbps";
        if (bps >= 1_000) return $"{bps/1_000:0.00}Kbps";
        return $"{bps:0}bps";
    }

    private static string Truncate(string s, int len)
    {
        if (s.Length <= len) return s;
        if (len <= 1) return "…";
        return s.Substring(0, len - 1) + '…';
    }
}
