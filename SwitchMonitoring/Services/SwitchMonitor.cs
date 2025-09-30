using System.Net;
using System.Diagnostics;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
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
    private readonly List<int> _probePorts; // global liste for auto-probing
    private readonly Dictionary<string,int> _resolvedPortCache = new(); // IP -> valgt port etter vellykket probe
    private readonly Dictionary<string,string> _lastProbeFailureSummary = new(); // IP -> attempts summary
    private readonly bool _probeLogEnabled;
    private readonly string _probeLogFile;
    private readonly Dictionary<string,SnmpV3User> _v3Users = new(StringComparer.OrdinalIgnoreCase);
    // High capacity counter support cache (ifHCIn/OutOctets) per switch -> set of interface indices
    private readonly Dictionary<string, HashSet<int>> _hcSupported = new();
    // Optional wrap debug
    private readonly bool _wrapDebug = (Environment.GetEnvironmentVariable("WRAP_DEBUG") ?? "0").Trim() == "1";
    // History: key = SwitchIp|IfIndex
    private readonly Dictionary<string, List<InterfaceSnapshot>> _history = new();
    private readonly object _historyLock = new();
    private const int MaxHistoryPerInterface = 20000; // increased for longer retention (memory usage consideration)
    private readonly HistoryStore _historyStore = new(Path.Combine(AppContext.BaseDirectory, "history"), 30);

    public SwitchMonitor(int pollIntervalSeconds, int maxInterfaces, bool useIfXTable, List<SwitchInfo> switches, int? snmpTimeoutMs = null, int? snmpRetries = null, bool showErrorDetails = false, int defaultSnmpPort = 161, List<int>? probePorts = null, bool probeLogEnabled = true, string? probeLogFile = null)
    {
        _pollIntervalSeconds = pollIntervalSeconds;
        _maxInterfaces = maxInterfaces;
        _useIfXTable = useIfXTable;
        _switches = switches;
        if (snmpTimeoutMs.HasValue) _snmpTimeoutMs = snmpTimeoutMs.Value;
        if (snmpRetries.HasValue) _snmpRetries = Math.Max(1, snmpRetries.Value);
        _showErrorDetails = showErrorDetails;
        _defaultSnmpPort = defaultSnmpPort;
        _probePorts = (probePorts != null && probePorts.Count > 0 ? probePorts : new List<int>{ defaultSnmpPort })
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        if (!_probePorts.Contains(_defaultSnmpPort)) _probePorts.Insert(0, _defaultSnmpPort);
        _probeLogEnabled = probeLogEnabled;
        _probeLogFile = probeLogFile ?? Path.Combine(AppContext.BaseDirectory, "snmp-probe-log.txt");
    }

    public IReadOnlyList<InterfaceSnapshot> GetHistory(string switchIp, int ifIndex)
    {
        var key = HistoryKey(switchIp, ifIndex);
        lock(_historyLock)
        {
            if (_history.TryGetValue(key, out var list)) return list.ToList();
        }
        return Array.Empty<InterfaceSnapshot>();
    }

    private static string HistoryKey(string ip, int idx) => ip + "|" + idx.ToString();
    private void AddHistory(InterfaceSnapshot snap)
    {
        if (snap.IfIndex <= 0) return;
        var key = HistoryKey(snap.SwitchIp, snap.IfIndex);
        lock(_historyLock)
        {
            if (!_history.TryGetValue(key, out var list))
            {
                list = new List<InterfaceSnapshot>(256);
                _history[key] = list;
            }
            list.Add(snap);
            if (list.Count > MaxHistoryPerInterface)
                list.RemoveRange(0, list.Count - MaxHistoryPerInterface);
        }
        // Persist to disk (fire & forget)
        try
        {
            _historyStore.Append(snap.SwitchIp, snap.IfIndex, snap.Timestamp, snap.InBps, snap.OutBps, snap.UtilInPercent, snap.UtilOutPercent, snap.SpeedLabel);
        }
        catch { }
    }

    public List<InterfaceSnapshot> LoadHistoryFromDisk(string switchIp, int ifIndex, DateTime fromUtc)
    {
        var list = new List<InterfaceSnapshot>();
        try
        {
            foreach (var rec in _historyStore.ReadRange(switchIp, ifIndex, fromUtc))
            {
                list.Add(new InterfaceSnapshot
                {
                    SwitchIp = switchIp,
                    SwitchName = _switches.FirstOrDefault(s=>s.IPAddress==switchIp)?.Name ?? switchIp,
                    IfIndex = ifIndex,
                    Timestamp = rec.ts,
                    InBps = rec.inBps,
                    OutBps = rec.outBps,
                    UtilInPercent = rec.ui,
                    UtilOutPercent = rec.uo,
                    SpeedLabel = rec.speed,
                    Status = "OK"
                });
            }
        }
        catch { }
        return list.OrderBy(x=>x.Timestamp).ToList();
    }

    public void SetV3Users(IEnumerable<SnmpV3User> users)
    {
        _v3Users.Clear();
        foreach (var u in users)
            if (!string.IsNullOrWhiteSpace(u.Name)) _v3Users[u.Name] = u;
    }

    private async Task<string> GetSingleV3Async(IPEndPoint ep, SnmpV3User user, string oid)
    {
    // Placeholder: full SNMPv3 not supported in this build (SharpSnmpLib v3 configuration requires more setup)
        await Task.Delay(1);
    throw new NotSupportedException("SNMPv3 support is not yet implemented");
    }

    // Multi-community test: attempts a sysName query with candidate communities (temporary mechanism)
    // Returns list of result lines and a summary note
    public async Task<(List<string> rows,string note)> TestCommunitiesAsync(string ip)
    {
        var rows = new List<string>();
        var swInfo = _switches.FirstOrDefault(s => s.IPAddress == ip);
        if (swInfo == null)
            return (rows, "Unknown IP");
        // Temporary candidate list (will later be parsed from config)
        var candidates = _communityTestCache ?? new List<string>{ swInfo.Community };
        if (candidates.Count == 0) candidates.Add(swInfo.Community);
        IPEndPoint? ep = _resolvedPortCache.ContainsKey(ip) ? EndpointFor(swInfo) : await ProbePortAsync(swInfo);
        if (ep == null)
        {
            rows.Add("No port responded (port probe failed)");
            return (rows, "Port probe failed");
        }
        foreach (var comm in candidates.Distinct())
        {
            var swr = Stopwatch.StartNew();
            try
            {
                var val = await GetSingleAsync(ep, comm, "1.3.6.1.2.1.1.5.0");
                swr.Stop();
                rows.Add($"OK community='{comm}' rtt={swr.ElapsedMilliseconds}ms sysName='{Truncate(val,40)}'");
            }
            catch (Exception ex)
            {
                swr.Stop();
                var cls = ClassifyException(ex);
                rows.Add($"FAIL community='{comm}' class={cls} rtt~{swr.ElapsedMilliseconds}ms msg='{Truncate(ex.Message,60)}'");
            }
        }
    return (rows, "Test completed");
    }

    // Temporary cache for communities (until config parsing implemented)
    private static List<string>? _communityTestCache;
    public static void SetCommunitiesToTest(List<string> comms)
    {
        _communityTestCache = comms;
    }

    private void ProbeLog(string line)
    {
        if (!_probeLogEnabled) return;
        try
        {
            File.AppendAllText(_probeLogFile, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {line}\n");
        }
        catch { /* swallow */ }
    }

    private IPEndPoint EndpointFor(SwitchInfo sw)
    {
        if (_resolvedPortCache.TryGetValue(sw.IPAddress, out var cached))
            return new(IPAddress.Parse(sw.IPAddress), cached);
        if (sw.SnmpPort.HasValue)
            return new(IPAddress.Parse(sw.IPAddress), sw.SnmpPort.Value);
        return new(IPAddress.Parse(sw.IPAddress), sw.SnmpPort ?? _defaultSnmpPort);
    }

    private async Task<IPEndPoint?> ProbePortAsync(SwitchInfo sw)
    {
    // Already resolved?
        if (_resolvedPortCache.TryGetValue(sw.IPAddress, out var cachedEp))
            return new IPEndPoint(IPAddress.Parse(sw.IPAddress), cachedEp);

        var ip = IPAddress.Parse(sw.IPAddress);
        var attempts = new List<string>();
        var candidates = new List<int>();
    if (sw.SnmpPort.HasValue) candidates.Add(sw.SnmpPort.Value); // explicit first
        foreach (var p in _probePorts)
            if (!candidates.Contains(p)) candidates.Add(p);

        foreach (var port in candidates)
        {
            try
            {
                var ep = new IPEndPoint(ip, port);
                var swr = Stopwatch.StartNew();
                var val = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.1.5.0", sw);
                swr.Stop();
                if (!string.IsNullOrEmpty(val))
                {
                    _resolvedPortCache[sw.IPAddress] = port;
                    _lastProbeFailureSummary.Remove(sw.IPAddress);
                    AppLogger.Info($"SNMP port probe success {sw.IPAddress} port={port} sysName={val}");
                    ProbeLog($"probe-success ip={sw.IPAddress} port={port} rttMs={swr.ElapsedMilliseconds} sysName=\"{Truncate(val,40)}\"");
                    return ep;
                }
            }
            catch (Exception ex)
            {
                var cls = ClassifyException(ex);
                attempts.Add($"{port}:{cls}");
                AppLogger.Warn($"Port probe failed {sw.IPAddress}:{port} -> {cls}: {ex.Message}");
                ProbeLog($"probe-fail ip={sw.IPAddress} port={port} class={cls} msg=\"{Truncate(ex.Message,80)}\"");
            }
        }
        var summary = string.Join("|", attempts);
        _lastProbeFailureSummary[sw.IPAddress] = summary;
    AppLogger.Warn($"SNMP port probing failed {sw.IPAddress} attempts=[{summary}]");
        ProbeLog($"probe-summary ip={sw.IPAddress} attempts=\"{summary}\"");
        return null;
    }

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
                return (true, $"SNMP OK (Ping={(ping?"OK":"FAIL")}) – sysName={sysName}, ifCount={ifCount}");
            }
            catch (Exception ex)
            {
                return (false, $"Ping={(ping?"OK":"FAIL")} SNMP error: {ex.Message}");
            }
        }

        public async Task<string> TestDiagnosticAsync(string ip, string community, int port = 161)
        {
            var report = new List<string>();
            bool ping = PingHost(ip);
            report.Add($"Ping: {(ping?"OK":"FAIL")}");
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
                    report.Add($"{label} ({oid}): FAIL -> {ex.Message}");
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
            AppLogger.Info("Starting poll round");
            var list = new List<InterfaceSnapshot>();
            foreach (var sw in _switches)
            {
                AppLogger.Info($"Polling switch {sw.IPAddress} ({sw.Name})");
                IPEndPoint? ep;
                if (_resolvedPortCache.ContainsKey(sw.IPAddress))
                {
                    ep = EndpointFor(sw);
                }
                else
                {
                    // Run (or re-run) probing if not cached
                    ep = await ProbePortAsync(sw);
                    if (ep == null)
                    {
                        list.Add(new InterfaceSnapshot
                        {
                            SwitchName = sw.Name,
                            SwitchIp = sw.IPAddress,
                            IfIndex = 0,
                            IfName = _showErrorDetails && _lastProbeFailureSummary.TryGetValue(sw.IPAddress, out var sum)
                                ? $"(probe failed:{Truncate(sum,40)})"
                                : "(no SNMP data)",
                            Status = _showErrorDetails ? "TIMEOUT" : "ERR",
                            SpeedLabel = "-",
                            Timestamp = DateTime.UtcNow,
                            ResolvedPort = null
                        });
                        continue; // next switch
                    }
                }
                try
                {
                    bool pingOk = PingHost(sw.IPAddress);
                    if (!pingOk)
                    {
                        Console.WriteLine($"Warning: Ping failed for {sw.IPAddress}, attempting SNMP anyway.");
                        AppLogger.Warn($"Ping failed {sw.IPAddress}");
                    }
                    try { _ = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.1.5.0", sw); }
                    catch (Exception probeEx)
                    {
                        var cls = ClassifyException(probeEx);
                        AppLogger.Warn($"sysName verification failed {sw.IPAddress}:{ep.Port} -> {cls}: {probeEx.Message}");
                        // If port suddenly failed (stale cache) attempt re-probing once
                        _resolvedPortCache.Remove(sw.IPAddress);
                        var reprobe = await ProbePortAsync(sw);
                        if (reprobe == null)
                        {
                            if (_showErrorDetails && _lastProbeFailureSummary.TryGetValue(sw.IPAddress, out var sum))
                            {
                                    list.Add(new InterfaceSnapshot
                                {
                                    SwitchName = sw.Name,
                                    SwitchIp = sw.IPAddress,
                                    IfIndex = 0,
                                    IfName = $"(probe failed:{Truncate(sum,40)})",
                                    Status = cls.ToUpperInvariant(),
                                    SpeedLabel = "-",
                                        Timestamp = DateTime.UtcNow,
                                        ResolvedPort = null
                                });
                            }
                            else
                            {
                                    list.Add(new InterfaceSnapshot
                                {
                                    SwitchName = sw.Name,
                                    SwitchIp = sw.IPAddress,
                                    IfIndex = 0,
                                    IfName = "(no SNMP data)",
                                    Status = "ERR",
                                    SpeedLabel = "-",
                                        Timestamp = DateTime.UtcNow,
                                        ResolvedPort = null
                                });
                            }
                            continue;
                        }
                        ep = reprobe; // continue with newly probed endpoint
                    }
                    var ifCountStr = await GetSingleAsync(ep, sw.Community, "1.3.6.1.2.1.2.1.0", sw);
                    if (!int.TryParse(ifCountStr, out var ifCount)) ifCount = 0;
                    var max = Math.Min(ifCount, _maxInterfaces);
                    if (!_lastSamples.ContainsKey(sw.IPAddress))
                        _lastSamples[sw.IPAddress] = new();
                    if (sw.IncludeIfIndices != null && sw.IncludeIfIndices.Count > 0)
                    {
                        foreach (var idx in sw.IncludeIfIndices)
                        {
                            if (idx <= 0) continue;
                            if (idx > ifCount && ifCount > 0) continue; // skip outside valid range when count known
                            var snap = await CollectSnapshotAsync(ep, sw, idx);
                            if (snap != null) list.Add(snap);
                        }
                    }
                    else
                    {
                        for (int i = 1; i <= max; i++)
                        {
                            var snap = await CollectSnapshotAsync(ep, sw, i);
                            if (snap != null) list.Add(snap);
                        }
                    }
                    if (!list.Any(x => x.SwitchIp == sw.IPAddress))
                    {
                        list.Add(new InterfaceSnapshot
                        {
                            SwitchName = sw.Name,
                            SwitchIp = sw.IPAddress,
                            IfIndex = 0,
                            IfName = $"(no SNMP data:{ep.Port})",
                            Status = _showErrorDetails ? "ERR" : "ERR",
                            SpeedLabel = "-",
                            Timestamp = DateTime.UtcNow,
                            ResolvedPort = ep.Port
                        });
                    }
                }
                catch (Exception ex)
                {
                    var cls = ClassifyException(ex);
                    AppLogger.Exception($"Polling error {sw.IPAddress}:{EndpointFor(sw).Port} classification={cls}", ex);
                    list.Add(new InterfaceSnapshot
                    {
                        SwitchName = sw.Name,
                        SwitchIp = sw.IPAddress,
                        IfIndex = 0,
                        IfName = _showErrorDetails ? $"({cls}:{(sw.SnmpPort ?? (_resolvedPortCache.ContainsKey(sw.IPAddress)? _resolvedPortCache[sw.IPAddress] : _defaultSnmpPort))})" : $"(no data:{(sw.SnmpPort ?? (_resolvedPortCache.ContainsKey(sw.IPAddress)? _resolvedPortCache[sw.IPAddress] : _defaultSnmpPort))})",
                        Status = _showErrorDetails ? cls.ToUpperInvariant() : "ERR",
                        SpeedLabel = "-",
                        Timestamp = DateTime.UtcNow,
                        ResolvedPort = sw.SnmpPort ?? (_resolvedPortCache.ContainsKey(sw.IPAddress)? _resolvedPortCache[sw.IPAddress] : _defaultSnmpPort)
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
                string descr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.2.{ifIndex}", sw);
                string oper = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.8.{ifIndex}", sw);
                string speedStr = await GetSingleAsync(ep, sw.Community, $"1.3.6.1.2.1.2.2.1.5.{ifIndex}", sw);
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
                        bool hc = _hcSupported.TryGetValue(sw.IPAddress, out var set) && set.Contains(ifIndex);
                        bool wrapIn=false, resetIn=false, wrapOut=false, resetOut=false;
                        ulong deltaIn = ComputeDelta(prev.InOctets, nowSample.InOctets, hc, out wrapIn, out resetIn);
                        ulong deltaOut = ComputeDelta(prev.OutOctets, nowSample.OutOctets, hc, out wrapOut, out resetOut);

                        // If a reset was detected (counter reset), skip rate this round to avoid spikes
                        if (!resetIn && !resetOut)
                        {
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
                        if (_wrapDebug && (wrapIn || wrapOut || resetIn || resetOut))
                        {
                            var flags = new List<string>();
                            if (wrapIn) flags.Add("wrapIn");
                            if (wrapOut) flags.Add("wrapOut");
                            if (resetIn) flags.Add("resetIn");
                            if (resetOut) flags.Add("resetOut");
                            AppLogger.Info($"Counter event {sw.IPAddress} if={ifIndex} flags=[{string.Join(',',flags)}] prevIn={prev.InOctets} nowIn={nowSample.InOctets} prevOut={prev.OutOctets} nowOut={nowSample.OutOctets} hc={(hc?1:0)}");
                        }
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
                    Timestamp = nowSample.Timestamp,
                    ResolvedPort = ep.Port
                };
                if (rate != null)
                {
                    snap.InBps = rate.InBps;
                    snap.OutBps = rate.OutBps;
                    snap.UtilInPercent = rate.UtilizationInPercent;
                    snap.UtilOutPercent = rate.UtilizationOutPercent;
                }
                    AddHistory(snap);
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
            // Try high capacity counters first if not already determined
            bool knownHc = _hcSupported.TryGetValue(sw.IPAddress, out var set) && set.Contains(ifIndex);
            if (!knownHc)
            {
                string hcOid = inbound
                    ? $"1.3.6.1.2.1.31.1.1.1.6.{ifIndex}"      // ifHCInOctets
                    : $"1.3.6.1.2.1.31.1.1.1.10.{ifIndex}";    // ifHCOutOctets
                try
                {
                    var hcVal = await GetSingleAsync(ep, sw.Community, hcOid, sw);
                    if (ulong.TryParse(hcVal, out var hv))
                    {
                        if (!_hcSupported.ContainsKey(sw.IPAddress)) _hcSupported[sw.IPAddress] = new();
                        _hcSupported[sw.IPAddress].Add(ifIndex);
                        return hv;
                    }
                }
                catch (Exception ex)
                {
                    var cls = ClassifyException(ex);
                    if (_wrapDebug && cls == "nosuch")
                        AppLogger.Info($"HC counter not supported {sw.IPAddress} if={ifIndex} ({(inbound?"In":"Out")})");
                    // fall through to 32-bit
                }
            }
            else
            {
                // We know HC is supported – use it directly
                string hcOid = inbound
                    ? $"1.3.6.1.2.1.31.1.1.1.6.{ifIndex}"
                    : $"1.3.6.1.2.1.31.1.1.1.10.{ifIndex}";
                try
                {
                    var hcVal = await GetSingleAsync(ep, sw.Community, hcOid, sw);
                    if (ulong.TryParse(hcVal, out var hv))
                        return hv;
                }
                catch (Exception ex)
                {
                    // If HC suddenly fails, log and fall back (might be temporary)
                    if (_wrapDebug)
                        AppLogger.Warn($"HC read feilet {sw.IPAddress} if={ifIndex} {(inbound?"In":"Out")} -> {ex.Message}");
                }
            }
            string legacyOid = inbound
                ? $"1.3.6.1.2.1.2.2.1.10.{ifIndex}"  // ifInOctets
                : $"1.3.6.1.2.1.2.2.1.16.{ifIndex}"; // ifOutOctets
            try
            {
                var val = await GetSingleAsync(ep, sw.Community, legacyOid, sw);
                return ulong.TryParse(val, out var v) ? v : 0UL;
            }
            catch (Exception ex2)
            {
                if (_wrapDebug)
                    AppLogger.Warn($"Legacy counter read failed {sw.IPAddress} if={ifIndex} {(inbound?"In":"Out")} -> {ex2.Message}");
                return 0UL;
            }
        }
    // Advanced delta with wrap/reset detection. Returns 0 on reset (rate is skipped)
        private ulong ComputeDelta(ulong prev, ulong current, bool highCapacity, out bool wrapped, out bool reset)
        {
            wrapped = false; reset = false;
            if (current >= prev)
                return current - prev; // normal
            // current < prev: wrap or reset
            if (highCapacity)
            {
                // Heuristic: if previous value near the top -> wrap else reset
                // "Near top" defined as last 10% of the range
                // Can't use 2^64 directly; use ulong.MaxValue as top value (2^64-1)
                var nearTop = ulong.MaxValue - (ulong)(ulong.MaxValue * 0.10); // 90% threshold
                if (prev >= nearTop)
                {
                    wrapped = true;
                    // delta = (max - prev + 1) + current
                    return (ulong.MaxValue - prev) + 1 + current;
                }
                reset = true;
                return 0;
            }
            else
            {
                const ulong max32 = uint.MaxValue; // 2^32-1
                var nearTop = max32 - (uint)(max32 * 0.10); // last 10%
                if (prev >= nearTop)
                {
                    wrapped = true;
                    return (max32 - prev) + 1 + current;
                }
                reset = true;
                return 0;
            }
        }

        private async Task<string> GetSingleAsync(IPEndPoint ep, string community, string oid, SwitchInfo? swInfo = null)
        {
            Exception? last = null;
            // If switch has SNMPv3 user configured and present in dictionary, attempt v3 first
            if (swInfo != null && !string.IsNullOrWhiteSpace(swInfo.SnmpV3User) && _v3Users.TryGetValue(swInfo.SnmpV3User, out var v3u))
            {
                try
                {
                    var swv3 = Stopwatch.StartNew();
                    var v3Val = await GetSingleV3Async(ep, v3u, oid);
                    swv3.Stop();
                    ProbeLog($"get v=3 result=success ip={ep.Address} port={ep.Port} oid={oid} rttMs={swv3.ElapsedMilliseconds}");
                    return v3Val;
                }
                catch (Exception exv3)
                {
                    ProbeLog($"get v=3 result=fail ip={ep.Address} port={ep.Port} oid={oid} class={ClassifyException(exv3)} msg=\"{Truncate(exv3.Message,80)}\"");
                    // fallback to v2 logic
                    last = exv3;
                }
            }
            for (int attempt = 1; attempt <= _snmpRetries; attempt++)
            {
                var varbinds = new List<Variable> { new(new ObjectIdentifier(oid)) };
                int id = Interlocked.Increment(ref _requestId);
                var msg = new GetRequestMessage(id, VersionCode.V2, new OctetString(community), varbinds);
                var swAttempt = Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(_snmpTimeoutMs);
                    var response = await msg.GetResponseAsync(ep, cts.Token).ConfigureAwait(false);
                    var pdu = response.Pdu();
                    if (pdu.ErrorStatus.ToInt32() != 0)
                        throw new Exception("SNMP error " + pdu.ErrorStatus);
                    var val = pdu.Variables[0].Data.ToString();
                    if (attempt > 1) AppLogger.Info($"SNMP v2 retry success {ep.Address} oid={oid} attempt={attempt}");
                    swAttempt.Stop();
                    ProbeLog($"get v=2 result=success ip={ep.Address} port={ep.Port} oid={oid} attempt={attempt} rttMs={swAttempt.ElapsedMilliseconds} len={val?.Length ?? 0}");
                    return val ?? string.Empty;
                }
                catch (Exception ex)
                {
                    last = ex;
                    swAttempt.Stop();
                    var cls = ClassifyException(ex);
                    ProbeLog($"get v=2 result=fail ip={ep.Address} port={ep.Port} oid={oid} attempt={attempt} class={cls} rttMs={swAttempt.ElapsedMilliseconds} msg=\"{Truncate(ex.Message,80)}\"");
                    if (attempt == 1)
                        AppLogger.Warn($"SNMP v2 first attempt failed {ep.Address}:{ep.Port} oid={oid} : {ex.Message}");
                    if (attempt == _snmpRetries)
                        AppLogger.Warn($"SNMP v2 all {_snmpRetries} attempts failed {ep.Address}:{ep.Port} oid={oid} : {ex.Message}");
                    if (attempt < _snmpRetries)
                    {
                        int backoff = 150 * attempt;
                        await Task.Delay(backoff);
                    }
                }
            }
            try
            {
                AppLogger.Info($"SNMP v2 failed for oid={oid} {ep.Address}:{ep.Port} – trying fallback v1");
                var varbinds = new List<Variable> { new(new ObjectIdentifier(oid)) };
                int id = Interlocked.Increment(ref _requestId);
                var msg = new GetRequestMessage(id, VersionCode.V1, new OctetString(community), varbinds);
                using var cts = new CancellationTokenSource(_snmpTimeoutMs);
                var swFb = Stopwatch.StartNew();
                var response = await msg.GetResponseAsync(ep, cts.Token).ConfigureAwait(false);
                var pdu = response.Pdu();
                if (pdu.ErrorStatus.ToInt32() != 0)
                    throw new Exception("SNMP v1 error " + pdu.ErrorStatus);
                var val = pdu.Variables[0].Data.ToString();
                AppLogger.Info($"SNMP v1 fallback success {ep.Address}:{ep.Port} oid={oid}");
                swFb.Stop();
                ProbeLog($"get v=1 result=success ip={ep.Address} port={ep.Port} oid={oid} rttMs={swFb.ElapsedMilliseconds} len={val?.Length ?? 0}");
                return val ?? string.Empty;
            }
            catch (Exception ex2)
            {
                AppLogger.Exception($"SNMP v1 fallback failed {ep.Address}:{ep.Port} oid={oid}", ex2);
                ProbeLog($"get v=1 result=fail ip={ep.Address} port={ep.Port} oid={oid} class={ClassifyException(ex2)} msg=\"{Truncate(ex2.Message,80)}\"\n");
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

        // Enkel RTT test mot sysName for valgt IP – forsøker evt. probing først
        public async Task<(bool success,string message)> SnmpPingAsync(string ip)
        {
            var swInfo = _switches.FirstOrDefault(s => s.IPAddress == ip);
            if (swInfo == null)
                return (false, $"Ukjent switch {ip}");
            IPEndPoint? ep;
            if (_resolvedPortCache.ContainsKey(ip))
            {
                ep = EndpointFor(swInfo);
            }
            else
            {
                ep = await ProbePortAsync(swInfo);
                if (ep == null)
                {
                    if (_lastProbeFailureSummary.TryGetValue(ip, out var sum))
                        return (false, $"Probe feilet: {sum}");
                    return (false, "Probe feilet (ingen porter svarte)");
                }
            }
            var sw = Stopwatch.StartNew();
            try
            {
                var val = await GetSingleAsync(ep, swInfo.Community, "1.3.6.1.2.1.1.5.0", swInfo);
                sw.Stop();
                return (true, $"OK port={ep.Port} rtt={sw.ElapsedMilliseconds}ms sysName={Truncate(val,60)}");
            }
            catch (Exception ex2)
            {
                sw.Stop();
                var cls = ClassifyException(ex2);
                return (false, $"FAIL port={ep.Port} rtt~{sw.ElapsedMilliseconds}ms klass={cls} msg={ex2.Message}");
            }
        }

        // Hent en enkel liste over grensesnitt (ifIndex -> ifDescr) opptil maxCount
        public async Task<(bool success, List<(int index,string name)> interfaces, string? error)> ListInterfacesAsync(string ip, int maxCount = 128)
        {
            var swInfo = _switches.FirstOrDefault(s => s.IPAddress == ip);
            if (swInfo == null)
                return (false, new(), "Ukjent switch");
            IPEndPoint? ep;
            if (_resolvedPortCache.ContainsKey(ip)) ep = EndpointFor(swInfo); else ep = await ProbePortAsync(swInfo);
            if (ep == null)
                return (false, new(), "Ingen SNMP port tilgjengelig");
            var result = new List<(int,string)>();
            try
            {
                // Hent ifNumber
                int count = 0;
                try
                {
                    var ifCountStr = await GetSingleAsync(ep, swInfo.Community, "1.3.6.1.2.1.2.1.0", swInfo);
                    if (!int.TryParse(ifCountStr, out count)) count = 0;
                }
                catch { }
                if (count <= 0) count = maxCount; // fallback
                int limit = Math.Min(count, maxCount);
                for (int i=1; i<=limit; i++)
                {
                    try
                    {
                        var descr = await GetSingleAsync(ep, swInfo.Community, $"1.3.6.1.2.1.2.2.1.2.{i}", swInfo);
                        result.Add((i, descr));
                    }
                    catch (Exception ex)
                    {
                        // Stopp hvis tydelig timeout tidlig; ellers hopp over hull
                        var cls = ClassifyException(ex);
                        if (cls == "timeout" && i == 1)
                            return (false, result, "Timeout på første interface – SNMP utilgjengelig");
                    }
                }
                return (true, result, null);
            }
            catch (Exception ex2)
            {
                return (false, result, ex2.Message);
            }
        }
    }
