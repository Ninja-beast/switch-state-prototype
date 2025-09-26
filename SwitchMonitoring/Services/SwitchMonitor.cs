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
    private int _snmpTimeoutMs = 1500; // default
    private int _snmpRetries = 2;      // attempts total
    private readonly bool _showErrorDetails;
    private readonly int _defaultSnmpPort;

    public SwitchMonitor(int pollIntervalSeconds, int maxInterfaces, bool useIfXTable, List<SwitchInfo> switches, int? snmpTimeoutMs = null, int? snmpRetries = null, bool showErrorDetails = false, int defaultSnmpPort = 161)
    {
        _pollIntervalSeconds = pollIntervalSeconds;
        _maxInterfaces = maxInterfaces;
        _useIfXTable = useIfXTable;
        _switches = switches;
        if (snmpTimeoutMs.HasValue) _snmpTimeoutMs = snmpTimeoutMs.Value;
        if (snmpRetries.HasValue) _snmpRetries = Math.Max(1, snmpRetries.Value);
        _showErrorDetails = showErrorDetails;
        _defaultSnmpPort = defaultSnmpPort;
    }

    private IPEndPoint EndpointFor(SwitchInfo sw) => new(IPAddress.Parse(sw.IPAddress), sw.SnmpPort ?? _defaultSnmpPort);

        public IReadOnlyList<SwitchInfo> GetSwitches() => _switches.AsReadOnly();
        public int GetPollInterval() => _pollIntervalSeconds;
        public int GetMaxInterfaces() => _maxInterfaces;
        public bool GetUseIfXTable() => _useIfXTable;
        public int GetSnmpTimeoutMs() => _snmpTimeoutMs;
        public int GetSnmpRetries() => _snmpRetries;

        public void UpdateConfiguration(int pollIntervalSeconds, int maxInterfaces, bool useIfXTable, List<SwitchInfo> switches, int? snmpTimeoutMs = null, int? snmpRetries = null)
        {
            _pollIntervalSeconds = pollIntervalSeconds;
            _maxInterfaces = maxInterfaces;
            _useIfXTable = useIfXTable;
            _switches = switches;
            if (snmpTimeoutMs.HasValue) _snmpTimeoutMs = snmpTimeoutMs.Value;
            if (snmpRetries.HasValue) _snmpRetries = Math.Max(1, snmpRetries.Value);
            var validIps = new HashSet<string>(_switches.Select(s => s.IPAddress));
            foreach (var key in _lastSamples.Keys.ToList())
            {
                if (!validIps.Contains(key))
                    _lastSamples.Remove(key);
            }
        }

        // Generic single OID query (kept default port 161 for direct IP usage)
        public async Task<(bool success,string value,string? error)> QueryOidAsync(string ip, string community, string oid, int port = 161)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(ip), port);
                var v = await GetSingleAsync(ep, community, oid);
                return (true, v, null);
            }
            catch (Exception ex)
            {
                return (false, string.Empty, ex.Message);
            }
        }

        public async Task<(bool success,string message)> TestSnmpAsync(string ip, string community, int port = 161)
        {
            bool ping = PingHost(ip);
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(ip), port);
                var sysName = await GetSingleAsync(ep, community, "1.3.6.1.2.1.1.5.0");
                var ifCount = await GetSingleAsync(ep, community, "1.3.6.1.2.1.2.1.0");
                return (true, $"SNMP OK (Ping={(ping?"OK":"FEIL")}) – sysName={sysName}, ifCount={ifCount}");
            }
            catch (Exception ex)
            {
                return (false, $"Ping={(ping?"OK":"FEIL")} SNMP feil: {ex.Message}");
            }
        }

        public async Task<string> TestDiagnosticAsync(string ip, string community, int port = 161)
        {
            var report = new List<string>();
            bool ping = PingHost(ip);
            report.Add($"Ping: {(ping?"OK":"FEIL")}");
            var ep = new IPEndPoint(IPAddress.Parse(ip), port);
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
            await probe("sysObjectID", "1.3.6.1.2.1.1.2.0");
            await probe("sysUpTime", "1.3.6.1.2.1.1.3.0");
            await probe("sysContact", "1.3.6.1.2.1.1.4.0");
            await probe("sysName", "1.3.6.1.2.1.1.5.0");
            await probe("sysLocation", "1.3.6.1.2.1.1.6.0");
            await probe("ifNumber", "1.3.6.1.2.1.2.1.0");
            await probe("ifDescr.1", "1.3.6.1.2.1.2.2.1.2.1");
            await probe("ifType.1", "1.3.6.1.2.1.2.2.1.3.1");
            await probe("ifMtu.1", "1.3.6.1.2.1.2.2.1.4.1");
            await probe("ifSpeed.1", "1.3.6.1.2.1.2.2.1.5.1");
            await probe("ifPhysAddress.1", "1.3.6.1.2.1.2.2.1.6.1");
            await probe("ifAdminStatus.1", "1.3.6.1.2.1.2.2.1.7.1");
            await probe("ifOperStatus.1", "1.3.6.1.2.1.2.2.1.8.1");
            await probe("ifInOctets.1", "1.3.6.1.2.1.2.2.1.10.1");
            await probe("ifOutOctets.1", "1.3.6.1.2.1.2.2.1.16.1");
            await probe("ifHCInOctets.1", "1.3.6.1.2.1.31.1.1.1.6.1");
            await probe("ifHCOutOctets.1", "1.3.6.1.2.1.31.1.1.1.10.1");
            await probe("Custom47196", "1.3.6.1.4.1.47196.4.1.1.3.17");
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
            catch { }
        }

        public async Task<List<InterfaceSnapshot>> PollOnceAsync()
        {
            AppLogger.Info("Starter poll runde");
            var list = new List<InterfaceSnapshot>();
            foreach (var sw in _switches)
            {
                AppLogger.Info($"Poll switch {sw.IPAddress} ({sw.Name})");
                var ep = EndpointFor(sw);
                try
                {
                    bool pingOk = PingHost(sw.IPAddress);
                    if (!pingOk)
                    {
                        Console.WriteLine($"Advarsel: Ping feilet mot {sw.IPAddress}, forsøker SNMP likevel.");
                        AppLogger.Warn($"Ping feilet {sw.IPAddress}");
                    }
                    try { _ = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.1.5.0"); }
                    catch (Exception probeEx)
                    {
                        var cls = ClassifyException(probeEx);
                        AppLogger.Warn($"sysName probe feilet {sw.IPAddress}:{ep.Port} -> {cls}: {probeEx.Message}");
                        if (_showErrorDetails)
                        {
                            list.Add(new InterfaceSnapshot
                            {
                                SwitchName = sw.Name,
                                SwitchIp = sw.IPAddress,
                                IfIndex = 0,
                                IfName = $"(probe {cls}:{ep.Port})",
                                Status = cls.ToUpperInvariant(),
                                SpeedLabel = "-",
                                Timestamp = DateTime.UtcNow
                            });
                            continue;
                        }
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
                    if (!list.Any(x => x.SwitchIp == sw.IPAddress))
                    {
                        list.Add(new InterfaceSnapshot
                        {
                            SwitchName = sw.Name,
                            SwitchIp = sw.IPAddress,
                            IfIndex = 0,
                            IfName = $"(ingen SNMP data:{ep.Port})",
                            Status = _showErrorDetails ? "ERR" : "ERR",
                            SpeedLabel = "-",
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }
                catch (Exception ex)
                {
                    var cls = ClassifyException(ex);
                    AppLogger.Exception($"Polling feil {sw.IPAddress}:{EndpointFor(sw).Port} klassifisering={cls}", ex);
                    list.Add(new InterfaceSnapshot
                    {
                        SwitchName = sw.Name,
                        SwitchIp = sw.IPAddress,
                        IfIndex = 0,
                        IfName = _showErrorDetails ? $"({cls}:{EndpointFor(sw).Port})" : $"(ingen data:{EndpointFor(sw).Port})",
                        Status = _showErrorDetails ? cls.ToUpperInvariant() : "ERR",
                        SpeedLabel = "-",
                        Timestamp = DateTime.UtcNow
                    });
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
            catch (Exception ex)
            {
                AppLogger.Exception($"CollectSnapshot {sw.IPAddress} ifIndex={ifIndex}", ex);
                return null;
            }
        }

        private async Task<ulong> GetCounterAsync(IPEndPoint ep, SwitchInfo sw, int ifIndex, bool inbound)
        {
            string oid = inbound
                ? $"1.3.6.1.2.1.2.2.1.10.{ifIndex}"
                : $"1.3.6.1.2.1.2.2.1.16.{ifIndex}";
            var val = await GetSingleAsync(ep, sw.Community, oid);
            return ulong.TryParse(val, out var v) ? v : 0UL;
        }

        private static ulong CounterDelta(ulong prev, ulong current)
        {
            if (current >= prev) return current - prev;
            ulong wrapPoint = prev > uint.MaxValue ? ulong.MaxValue : uint.MaxValue;
            return (wrapPoint - prev) + current;
        }

        private async Task<string> GetSingleAsync(IPEndPoint ep, string community, string oid)
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= _snmpRetries; attempt++)
            {
                var varbinds = new List<Variable> { new(new ObjectIdentifier(oid)) };
                int id = Interlocked.Increment(ref _requestId);
                var msg = new GetRequestMessage(id, VersionCode.V2, new OctetString(community), varbinds);
                try
                {
                    using var cts = new CancellationTokenSource(_snmpTimeoutMs);
                    var response = await msg.GetResponseAsync(ep, cts.Token).ConfigureAwait(false);
                    var pdu = response.Pdu();
                    if (pdu.ErrorStatus.ToInt32() != 0)
                        throw new Exception("SNMP error " + pdu.ErrorStatus);
                    var val = pdu.Variables[0].Data.ToString();
                    if (attempt > 1) AppLogger.Info($"SNMP v2 retry suksess {ep.Address} oid={oid} attempt={attempt}");
                    return val;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 1)
                        AppLogger.Warn($"SNMP v2 første forsøk feilet {ep.Address}:{ep.Port} oid={oid} : {ex.Message}");
                    if (attempt == _snmpRetries)
                        AppLogger.Warn($"SNMP v2 alle {_snmpRetries} forsøk feilet {ep.Address}:{ep.Port} oid={oid} : {ex.Message}");
                    if (attempt < _snmpRetries)
                    {
                        int backoff = 150 * attempt;
                        await Task.Delay(backoff);
                    }
                }
            }
            try
            {
                AppLogger.Info($"SNMP v2 feilet for oid={oid} {ep.Address}:{ep.Port} – prøver fallback v1");
                var varbinds = new List<Variable> { new(new ObjectIdentifier(oid)) };
                int id = Interlocked.Increment(ref _requestId);
                var msg = new GetRequestMessage(id, VersionCode.V1, new OctetString(community), varbinds);
                using var cts = new CancellationTokenSource(_snmpTimeoutMs);
                var response = await msg.GetResponseAsync(ep, cts.Token).ConfigureAwait(false);
                var pdu = response.Pdu();
                if (pdu.ErrorStatus.ToInt32() != 0)
                    throw new Exception("SNMP v1 error " + pdu.ErrorStatus);
                var val = pdu.Variables[0].Data.ToString();
                AppLogger.Info($"SNMP v1 fallback suksess {ep.Address}:{ep.Port} oid={oid}");
                return val;
            }
            catch (Exception ex2)
            {
                AppLogger.Exception($"SNMP v1 fallback feilet {ep.Address}:{ep.Port} oid={oid}", ex2);
                throw last ?? ex2;
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

        private static string ClassifyException(Exception ex)
        {
            if (ex is TaskCanceledException || ex is OperationCanceledException)
                return "timeout";
            if (ex is System.Net.Sockets.SocketException sex)
            {
                if (sex.ErrorCode == 10060) return "timeout";
                return "socket";
            }
            var msg = ex.Message.ToLowerInvariant();
            if (msg.Contains("timeout")) return "timeout";
            if (msg.Contains("no such") || msg.Contains("not found")) return "nosuch";
            if (msg.Contains("auth") || msg.Contains("community")) return "auth";
            if (msg.Contains("refused")) return "refused";
            return "error";
        }
    }
