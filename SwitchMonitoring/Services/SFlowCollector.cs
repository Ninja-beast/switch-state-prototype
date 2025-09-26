using System.Net;
using System.Net.Sockets;
using SwitchMonitoring.Models;

namespace SwitchMonitoring.Services;

public class SFlowCollector
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private readonly Dictionary<string, Dictionary<int, InterfaceSample>> _lastSamples = new();
    private readonly Dictionary<string, long> _datagramCounts = new();
    private readonly Dictionary<string, DateTime> _lastDatagram = new();
    private readonly HashSet<string> _flowOnlyAgents = new();
    private readonly object _lock = new();
    private readonly bool _debug;
    private readonly int _dumpFirstN;
    private int _dumped;
    private long _rawDatagrams;
    private long _invalidVersion;
    private long _parseErrors;

    public int Port { get; }
    public long RawDatagramCount => Interlocked.Read(ref _rawDatagrams);
    public DateTime? LastAnyDatagram
    {
        get
        {
            lock(_lock)
            {
                if (_lastDatagram.Count == 0) return null;
                return _lastDatagram.Values.Max();
            }
        }
    }

    public SFlowCollector(int port = 6343, bool debug = false, int dumpFirstN = 0)
    {
        Port = port;
        _debug = debug;
        _dumpFirstN = dumpFirstN;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        _loopTask = Task.Run(ReceiveLoopAsync);
    }

    public SFlowCollector(int port, bool debug, string bindIp, int dumpFirstN = 0)
    {
        Port = port;
        _debug = debug;
        _dumpFirstN = dumpFirstN;
        var ip = IPAddress.Parse(bindIp);
        _udp = new UdpClient(new IPEndPoint(ip, port));
        _loopTask = Task.Run(ReceiveLoopAsync);
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _udp.Close(); } catch { }
    }

    // Return snapshots similar to SNMP polling
    public List<InterfaceSnapshot> GetSnapshots()
    {
        var list = new List<InterfaceSnapshot>();
        lock (_lock)
        {
            foreach (var agent in _lastSamples)
            {
                foreach (var kv in agent.Value)
                {
                    var sample = kv.Value;
                    list.Add(new InterfaceSnapshot
                    {
                        SwitchName = agent.Key,
                        SwitchIp = agent.Key,
                        IfIndex = sample.IfIndex,
                        IfName = sample.Name ?? ($"if{sample.IfIndex}"),
                        Status = "UP", // sFlow counter sample gir ikke oper status direkte (kun mulig via ifOperStatus separate MIB; leave UP)
                        SpeedLabel = sample.SpeedLabel ?? "-",
                        InBps = sample.RateInBps,
                        OutBps = sample.RateOutBps,
                        UtilInPercent = 0, // uten speed info
                        UtilOutPercent = 0,
                        Timestamp = sample.Timestamp
                    });
                }
            }
        }
        return list;
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                ParseDatagram(result.Buffer, result.RemoteEndPoint.Address);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AppLogger.Warn("sFlow mottak feil: " + ex.Message);
                await Task.Delay(500);
            }
        }
    }

    private void ParseDatagram(byte[] buf, IPAddress agentAddress)
    {
        try
        {
            Interlocked.Increment(ref _rawDatagrams);
            var span = buf.AsSpan();
            if (span.Length < 4) return;
            int version = ReadInt(span, 0);
            if (version != 5)
            {
                Interlocked.Increment(ref _invalidVersion);
                return; // bare v5 for nå
            }
            lock (_lock)
            {
                var key = agentAddress.ToString();
                if (!_datagramCounts.ContainsKey(key)) _datagramCounts[key] = 0;
                _datagramCounts[key]++;
                _lastDatagram[key] = DateTime.UtcNow;
            }
            if (_debug)
            {
                var hex = BitConverter.ToString(buf, 0, Math.Min(32, buf.Length));
                AppLogger.Info($"sFlow datagram fra {agentAddress} len={buf.Length} first={hex}");
            }
            if (_dumpFirstN > 0 && _dumped < _dumpFirstN)
            {
                try
                {
                    var path = Path.Combine(AppContext.BaseDirectory, $"sflow-dump-{_dumped+1}.bin");
                    File.WriteAllBytes(path, buf);
                    Interlocked.Increment(ref _dumped);
                }
                catch (Exception ex)
                {
                    if (_debug) AppLogger.Warn("Kunne ikke skrive dump: " + ex.Message);
                }
            }
            int offset = 4;
            // Agent address type + value
            if (span.Length < offset + 4) return;
            int agentAddrType = ReadInt(span, offset); offset += 4; // 1=IPv4
            if (agentAddrType == 1)
            {
                if (span.Length < offset + 4) return;
                offset += 4; // vi ignorerer agent IP (bruker RemoteEndPoint)
            }
            else if (agentAddrType == 2)
            {
                offset += 16; // IPv6, ignorer
            }
            else return;

            if (span.Length < offset + 12) return;
            offset += 4; // subId
            offset += 4; // sequence
            offset += 4; // sysUpTime
            if (span.Length < offset + 4) return;
            int samples = ReadInt(span, offset); offset += 4;

            for (int i = 0; i < samples; i++)
            {
                if (span.Length < offset + 8) return;
                int sampleTypeEnterpriseFormat = ReadInt(span, offset); offset += 4;
                int sampleLength = ReadInt(span, offset); offset += 4;
                if (span.Length < offset + sampleLength) return;
                int enterprise = sampleTypeEnterpriseFormat >> 12; // top 20 bits
                int format = sampleTypeEnterpriseFormat & 0xFFF;   // low 12 bits
                var sampleSpan = span.Slice(offset, sampleLength);
                offset += sampleLength;

                if (enterprise == 0 && (format == 2 || format == 4)) // counter sample (2=standard,4=expanded)
                {
                    ParseCounterSample(sampleSpan, agentAddress, format == 4);
                }
                else
                {
                    // mark agent as flow-only if we never saw counters
                    lock(_lock) { _flowOnlyAgents.Add(agentAddress.ToString()); }
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _parseErrors);
            if (_debug)
                AppLogger.Warn("sFlow parse feil: " + ex.Message);
        }
    }

    private void ParseCounterSample(Span<byte> span, IPAddress agentAddress, bool expanded)
    {
        int offset = 0;
        if (span.Length < 4) return;
        int records = ReadInt(span, offset); offset += 4; // number of counter records

        for (int r = 0; r < records; r++)
        {
            if (span.Length < offset + 8) return;
            int recType = ReadInt(span, offset); offset += 4;
            int recLen = ReadInt(span, offset); offset += 4;
            if (span.Length < offset + recLen) return;
            var recSpan = span.Slice(offset, recLen);
            offset += recLen;
            int enterprise = recType >> 12;
            int format = recType & 0xFFF;
            if (enterprise == 0 && format == 1)
            {
                ParseGenericInterfaceCounters(recSpan, agentAddress);
            }
            else if (enterprise == 0 && format == 1001) // expanded generic if counters
            {
                ParseExpandedGenericInterfaceCounters(recSpan, agentAddress);
            }
        }
    }

    private void ParseGenericInterfaceCounters(Span<byte> span, IPAddress agentAddress)
    {
        int o = 0;
        if (span.Length < 88) return; // generic if counters record fixed length
        int ifIndex = ReadInt(span, o); o += 4;
        o += 4; // ifType
        o += 4; // ifSpeed high (skip)
        o += 4; // ifSpeed low (skip) => could combine to 64-bit
        o += 4; // ifDirection
        o += 4; // ifStatus
        ulong ifInOctets = ReadULong(span, o); o += 8;
        int inUcastPktsHigh = ReadInt(span, o); o += 4;
        int inUcastPktsLow = ReadInt(span, o); o += 4;
        o += 4; // inMulticast
        o += 4; // inBroadcast
        o += 4; // inDiscards
        o += 4; // inErrors
        o += 4; // inUnknownProto
        ulong ifOutOctets = ReadULong(span, o); o += 8;
        int outUcastPktsHigh = ReadInt(span, o); o += 4;
        int outUcastPktsLow = ReadInt(span, o); o += 4;
        // rest skip

        double? inBps = null;
        double? outBps = null;
        lock (_lock)
        {
            var key = agentAddress.ToString();
            if (!_lastSamples.ContainsKey(key)) _lastSamples[key] = new();
            if (!_lastSamples[key].TryGetValue(ifIndex, out var prev))
            {
                _lastSamples[key][ifIndex] = new InterfaceSample
                {
                    IfIndex = ifIndex,
                    InOctets = ifInOctets,
                    OutOctets = ifOutOctets,
                    Timestamp = DateTime.UtcNow
                };
                return; // need previous to calc bps
            }
            var now = DateTime.UtcNow;
            var deltaSec = (now - prev.Timestamp).TotalSeconds;
            if (deltaSec <= 0) return;
            ulong dIn = CounterDelta(prev.InOctets, ifInOctets);
            ulong dOut = CounterDelta(prev.OutOctets, ifOutOctets);
            inBps = dIn * 8 / deltaSec;
            outBps = dOut * 8 / deltaSec;
            prev.InOctets = ifInOctets;
            prev.OutOctets = ifOutOctets;
            prev.Timestamp = now;
            prev.RateInBps = inBps.Value;
            prev.RateOutBps = outBps.Value;
        }
    }

    // Expanded generic interface counters (format 1001) parsing (simplified: only octets + ifIndex)
    private void ParseExpandedGenericInterfaceCounters(Span<byte> span, IPAddress agentAddress)
    {
        // Spec: first 4 bytes ifIndex, then same structure but may include additional fields; we focus on octets.
        int o = 0;
        if (span.Length < 16) return; // minimum for index + some data
        int ifIndex = ReadInt(span, o); o += 4;
        // Skip to InOctets / OutOctets: This is a simplification; real layout differs but we attempt fallback
        // If we cannot guarantee layout we abort early.
        // For safety require at least 40 bytes
        if (span.Length < 40) return;
        // Heuristically scan for two 8-byte counters near end
        // We'll take the last 16 bytes as OutOctets preceded by InOctets
        var inOctSpan = span.Slice(span.Length - 16, 8);
        var outOctSpan = span.Slice(span.Length - 8, 8);
        ulong inOct = ReadULong(inOctSpan, 0);
        ulong outOct = ReadULong(outOctSpan, 0);
        lock (_lock)
        {
            var key = agentAddress.ToString();
            if (!_lastSamples.ContainsKey(key)) _lastSamples[key] = new();
            if (!_lastSamples[key].TryGetValue(ifIndex, out var prev))
            {
                _lastSamples[key][ifIndex] = new InterfaceSample
                {
                    IfIndex = ifIndex,
                    InOctets = inOct,
                    OutOctets = outOct,
                    Timestamp = DateTime.UtcNow
                };
                return;
            }
            var now = DateTime.UtcNow;
            var deltaSec = (now - prev.Timestamp).TotalSeconds;
            if (deltaSec <= 0) return;
            ulong dIn = CounterDelta(prev.InOctets, inOct);
            ulong dOut = CounterDelta(prev.OutOctets, outOct);
            prev.InOctets = inOct;
            prev.OutOctets = outOct;
            prev.Timestamp = now;
            prev.RateInBps = dIn * 8 / deltaSec;
            prev.RateOutBps = dOut * 8 / deltaSec;
        }
    }

    private static int ReadInt(Span<byte> span, int offset) => IPAddress.NetworkToHostOrder(BitConverter.ToInt32(span.Slice(offset,4)));
    private static ulong ReadULong(Span<byte> span, int offset)
    {
        var high = (uint)ReadInt(span, offset);
        var low = (uint)ReadInt(span, offset+4);
        return ((ulong)high << 32) | low;
    }

    private static ulong CounterDelta(ulong prev, ulong current)
    {
        if (current >= prev) return current - prev;
        // 64-bit wrap
        return (ulong.MaxValue - prev) + current;
    }

    public (long total, Dictionary<string,long> perAgent, long raw, long invalidVersion, long parseErrors, Dictionary<string,DateTime> last, HashSet<string> flowOnly) GetDatagramStats()
    {
        lock (_lock)
        {
            var clone = _datagramCounts.ToDictionary(k=>k.Key,v=>v.Value);
            long total = clone.Values.Sum();
            var last = _lastDatagram.ToDictionary(k=>k.Key,v=>v.Value);
            var flow = new HashSet<string>(_flowOnlyAgents);
            return (total, clone, _rawDatagrams, _invalidVersion, _parseErrors, last, flow);
        }
    }

    // Synthetic test injection: creates fake one-interface counter update for a pseudo agent
    public void InjectTestSample(string agentIp = "127.254.0.1", int ifIndex = 1, ulong inOctets = 10_000_000, ulong outOctets = 5_000_000)
    {
        lock (_lock)
        {
            if (!_lastSamples.ContainsKey(agentIp)) _lastSamples[agentIp] = new();
            if (!_datagramCounts.ContainsKey(agentIp)) _datagramCounts[agentIp] = 0;
            _datagramCounts[agentIp]++;
            if (!_lastSamples[agentIp].TryGetValue(ifIndex, out var prev))
            {
                _lastSamples[agentIp][ifIndex] = new InterfaceSample
                {
                    IfIndex = ifIndex,
                    Name = "TEST-IF",
                    InOctets = inOctets,
                    OutOctets = outOctets,
                    Timestamp = DateTime.UtcNow
                };
            }
            else
            {
                var now = DateTime.UtcNow;
                var deltaSec = (now - prev.Timestamp).TotalSeconds;
                if (deltaSec <= 0) deltaSec = 1;
                ulong dIn = CounterDelta(prev.InOctets, inOctets);
                ulong dOut = CounterDelta(prev.OutOctets, outOctets);
                prev.InOctets = inOctets;
                prev.OutOctets = outOctets;
                prev.Timestamp = now;
                prev.RateInBps = dIn * 8 / deltaSec;
                prev.RateOutBps = dOut * 8 / deltaSec;
            }
        }
    }
}
