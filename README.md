# SwitchMonitoring

Windows (.NET 8) WinForms SNMP monitoring application that polls interface traffic (bits per second) and utilization (%), with live and historical visualization.

## Features
- Dark themed WinForms UI
- Configurable polling interval & interface limit
- Dynamic preferred 64‑bit counters (ifHCIn/OutOctets) with fallback to 32‑bit
- Counter wrap & reset detection (32-bit + 64-bit heuristics)
- Live per-port graph with smoothing, headroom scaling, min/max hysteresis
- Historical graph with interpolation (linear/step), easing, delta & percent change, trend arrows, threshold highlighting, future time axis padding (+50 years toggle)
- In-memory + on-disk JSONL history (daily segments, automatic merge)
- Clipboard export (single point / visible window) and CSV file export
- User preferences persistence (window length, smoothing, future padding, interpolation mode, delta threshold, easing factor, show total area)
- Multi-community test probing & port probe auto-detection
- Structured logging with secret masking and rotation
- SNMP diagnostic probe (system & interface OIDs)

## Configuration (`appsettings.json`)
```json
{
  "PollIntervalSeconds": 30,
  "Switches": [
    { "Name": "Core Switch", "IPAddress": "192.168.1.10", "Community": "public" },
    { "Name": "Access Switch", "IPAddress": "192.168.1.20", "Community": "public" }
  ],
  "MaxInterfaces": 10,
  "UseIfXTable": true
}
```
Fields:
- `PollIntervalSeconds`: How often to poll counters
- `Switches`: List of devices (community currently v2c)
- `MaxInterfaces`: Limit number of interfaces collected (performance/clarity)
- `UseIfXTable`: true = attempt 64-bit ifHC* counters first

## Switch Requirements
- SNMP v2c enabled
- Community string matches configuration
- Access to MIB-2 system, interfaces, and ifXTable branches

## Build & Run
From repository root:
```powershell
cd "SwitchMonitoring"
dotnet restore
dotnet run --project SwitchMonitoring/SwitchMonitoring.csproj
```

## Sample Output (textual log style)
```
Switch: Core Switch (192.168.1.10)
  System Name: CORE-SW1
  Uptime: 12d 4h 33m
  Interfaces (ifTable): 48
     1: Gi1/0/1   UP 1G  In:12.34Mbps Out:1.22Mbps Util: 1.2% / 0.1%
     2: Gi1/0/2   UP 1G  (collecting first sample)
-------------------------------------------------------------
```

## Troubleshooting
| Problem | Explanation / Fix |
|---------|--------------------|
| Timeout / no response | Check VLAN/ACL/firewall – UDP 161 must be open both ways |
| Ping fails but SNMP works | Device blocks ICMP – only a warning, safe to ignore |
| All rows show ERR | Wrong community or SNMP disabled on device |
| Always 0 bps / 0% | Need two samples; wait one interval or link is idle |
| Utilization > 100% | Reported ifSpeed lower than actual (negotiation mismatch) |
| Missing higher ports | Increase `MaxInterfaces` or inspect ifNumber via Diagnose |

### Diagnose Interpretation Example
```
Ping: FAIL
sysDescr (...): OK -> Aruba JL123A ...
sysName (...): OK -> CORE-SW1
ifNumber (...): OK -> 52
ifDescr.1 (...): FAIL -> NoSuchInstance
```
Notes:
- Ping FAIL but SNMP OK: ICMP blocked.
- ifDescr.1 fail: interface 1 may not exist (stacked base index offset) – ignore.

### Common OIDs
| Description | OID |
|-------------|-----|
| sysDescr | 1.3.6.1.2.1.1.1.0 |
| sysObjectID | 1.3.6.1.2.1.1.2.0 |
| sysUpTime | 1.3.6.1.2.1.1.3.0 |
| sysContact | 1.3.6.1.2.1.1.4.0 |
| sysName | 1.3.6.1.2.1.1.5.0 |
| sysLocation | 1.3.6.1.2.1.1.6.0 |
| ifNumber | 1.3.6.1.2.1.2.1.0 |
| ifDescr (idx N) | 1.3.6.1.2.1.2.2.1.2.N |
| ifType (idx N) | 1.3.6.1.2.1.2.2.1.3.N |
| ifMtu (idx N) | 1.3.6.1.2.1.2.2.1.4.N |
| ifSpeed (idx N) | 1.3.6.1.2.1.2.2.1.5.N |
| ifPhysAddress (idx N) | 1.3.6.1.2.1.2.2.1.6.N |
| ifAdminStatus (idx N) | 1.3.6.1.2.1.2.2.1.7.N |
| ifOperStatus (idx N) | 1.3.6.1.2.1.2.2.1.8.N |
| ifInOctets (idx N) | 1.3.6.1.2.1.2.2.1.10.N |
| ifOutOctets (idx N) | 1.3.6.1.2.1.2.2.1.16.N |
| ifHCInOctets (idx N) | 1.3.6.1.2.1.31.1.1.1.6.N |
| ifHCOutOctets (idx N) | 1.3.6.1.2.1.31.1.1.1.10.N |
| Custom47196 (example enterprise) | 1.3.6.1.4.1.47196.4.1.1.3.17 |

## Roadmap Ideas
- SNMPv3 (auth/privacy) full support
- Alert rules (color/notification over thresholds)
- Pan/zoom & multi-series overlay exports
- Prometheus / InfluxDB exporter
- Annotation markers for wraps/resets
- Pluggable output (file / REST)

## License
MIT
