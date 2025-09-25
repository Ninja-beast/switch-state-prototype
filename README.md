# SwitchMonitoring

WinForms SNMP overvåkning i C# (.NET 8) som henter trafikk (bps) og utnyttelse (%) per interface.

## Funksjoner
- WinForms GUI med mørkt tema
- Manuell og automatisk oppdatering
- Konfigurasjonsdialog (poll intervall, maks porter, ifX toggle, community)
- Diagnose: Tester flere OIDs (sysDescr, sysName, ifNumber, ifDescr.1) og ping
- Logger diagnostikk til `snmp-log.txt`
- Bruker 64‑bit tellere (ifHCIn/OutOctets) når aktivert – fallback til 32‑bit
- Beregner båndbredde (bps) og utnyttelse (%) mellom prøver (delta)
- Håndterer counter wrap (32/64-bit)

## Konfigurasjon (`appsettings.json`)
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
Felter:
- `PollIntervalSeconds`: Hvor ofte målinger tas
- `Switches`: Liste over enheter
- `MaxInterfaces`: Begrens antall porter som vises (ytelse / oversikt)
- `UseIfXTable`: true = forsøk å hente 64-bit ifHC* tellere

## Krav på switch
- SNMP v2c aktivert
- Community string samsvarer med filen
- Tillatelse til å lese MIB-2 (interfaces, system, ifXTable)

## Bygg og kjør
I rotmappen (der `.sln` ligger) kjør:

```powershell
# Gå inn i prosjektkatalog
cd "SwitchMonitoring"

# (valgfritt) Opprett løsning og legg til prosjekt
# dotnet new sln -n SwitchMonitoring
# dotnet sln add SwitchMonitoring.csproj

# Gjenopprett pakker
dotnet restore

# Kjør
dotnet run --project SwitchMonitoring/SwitchMonitoring.csproj
```

## Utdata eksempel
```
Switch: Core Switch (192.168.1.10)
  System Name: CORE-SW1
  Uptime: 12d 4h 33m
  Antall interfaces (ifTable): 48
     1: Gi1/0/1                  UP    1G   In:12.34Mbps Out: 1.22Mbps Util:  1.2%/  0.1%
     2: Gi1/0/2                  UP    1G   (samler første prøve)
--------------------------------------------------------------
```

## Feilsøking (Generelt)
| Problem | Løsning |
|---------|---------|
| Tidsavbrudd / ingen svar | Sjekk VLAN / ACL / brannmur – UDP 161 må være åpen begge veier |
| Ping feiler men SNMP virker | Noen enheter blokkerer ICMP – dette er kun en advarsel i GUI |
| Kun ERR i tabellen | Feil community eller SNMP deaktivert på switchen |
| 0 bps / alltid 0 % | Vent til andre måling (trenger to samples) eller interface er idle |
| Util > 100 % | Interface speed rapporteres lavere enn reell (f.eks. auto-neg mismatch) |
| Mangler høye porter | Øk `MaxInterfaces` eller sjekk ifNumber i Diagnose |

### Aruba / HP (ProCurve / ArubaOS-Switch) SNMP sjekkliste
1. Aktiver SNMP v2c (CLI eksempel):
  ```
  configure terminal
  snmp-server community public ro
  write memory
  ```
2. Hvis du bruker annen community – endre i Konfigurasjon -> Endre…
3. Kontroller at ACL ikke blokkerer klienten: `show snmp` / `show access-list`
4. Bekreft at UDP 161 er åpen (fra PC: `Test SNMP` / `Diagnose` i GUI)
5. Hvis bare sysDescr feiler: enhet kan ha rate-limit; vent og prøv igjen
6. ifHC* (64-bit) finnes ikke på veldig gamle modeller – skru av ifX i konfig hvis alle porter viser 0 bps

### Tolk Diagnose-resultat
Eksempel:
```
Ping: FEIL
sysDescr (1.3.6.1.2.1.1.1.0): OK -> Aruba JL123A ...
sysName (1.3.6.1.2.1.1.5.0): OK -> CORE-SW1
ifNumber (1.3.6.1.2.1.2.1.0): OK -> 52
ifDescr.1 (1.3.6.1.2.1.2.2.1.2.1): FEIL -> NoSuchInstance
```
Forklaring:
- Ping FEIL men SNMP OK: ICMP blokkert – ufarlig.
- ifDescr.1 feilet: Port 1 finnes ikke (kan starte på 49 for stack) – ignorer.

### Vanlige Aruba OIDs
| Beskrivelse | OID |
|-------------|-----|
| sysName | 1.3.6.1.2.1.1.5.0 |
| sysDescr | 1.3.6.1.2.1.1.1.0 |
| ifNumber | 1.3.6.1.2.1.2.1.0 |
| ifInOctets (idx N) | 1.3.6.1.2.1.2.2.1.10.N |
| ifHCInOctets (idx N) | 1.3.6.1.2.1.31.1.1.1.6.N |
| ifDescr (idx N) | 1.3.6.1.2.1.2.2.1.2.N |

## Videre forbedringer (idéer)
- Multi-switch visning i samme grid
- Historikk / graf (rolling in-memory eller SQLite)
- Eksport (Prometheus / InfluxDB)
- SNMP v3 (auth/priv)
- Alarmregler (farge ved > 80 %)
- CSV / JSON eksport av siste måling

## Lisens
MIT (kan tilpasses etter behov)
