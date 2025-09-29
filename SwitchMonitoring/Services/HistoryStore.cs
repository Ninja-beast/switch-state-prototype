using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwitchMonitoring.Services;

public class HistoryStore
{
    private readonly string _baseDir;
    private readonly int _maxDays;
    private static readonly JsonWriterOptions _writerOptions = new(){SkipValidation=true,Indented=false};
    private static readonly JsonSerializerOptions _serOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    public HistoryStore(string baseDir, int maxDays = 30)
    {
        _baseDir = baseDir;
        _maxDays = Math.Max(1, maxDays);
        Directory.CreateDirectory(_baseDir);
    }

    private string DirFor(string switchIp, int ifIndex)
    {
        var d = Path.Combine(_baseDir, Sanitize(switchIp), $"if{ifIndex}");
        Directory.CreateDirectory(d);
        return d;
    }
    private static string Sanitize(string ip) => ip.Replace(':','_').Replace('/','_');

    private string FileFor(string switchIp, int ifIndex, DateTime utcTs)
        => Path.Combine(DirFor(switchIp, ifIndex), utcTs.ToString("yyyy-MM-dd") + ".jsonl");

    public void Append(string switchIp, int ifIndex, DateTime tsUtc, double inBps, double outBps, double utilIn, double utilOut, string speedLabel)
    {
        try
        {
            var path = FileFor(switchIp, ifIndex, tsUtc);
            var lineObj = new
            {
                ts = tsUtc.ToString("o"),
                i = inBps,
                o = outBps,
                ui = utilIn,
                uo = utilOut,
                sp = speedLabel
            };
            var json = JsonSerializer.Serialize(lineObj, _serOptions);
            File.AppendAllText(path, json + "\n");
        }
        catch { /* swallow */ }
    }

    public IEnumerable<(DateTime ts,double inBps,double outBps,double ui,double uo,string speed)> ReadRange(string switchIp, int ifIndex, DateTime fromUtc)
    {
        var dir = DirFor(switchIp, ifIndex);
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.jsonl").OrderBy(f=>f))
        {
            // Skip filer som åpenbart er nyere enn nå (ingen), eller for gamle
            var name = Path.GetFileNameWithoutExtension(file);
            if (!DateTime.TryParse(name, out var day)) { continue; }
            // day = local parse; tolker det som UTC midnatt
            var dayUtc = DateTime.SpecifyKind(day, DateTimeKind.Utc);
            if (dayUtc < fromUtc.Date.AddDays(-_maxDays)) continue; // utenfor retention
            if (dayUtc > DateTime.UtcNow.Date.AddDays(1)) continue; // fremtid
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument? doc = null;
                try { doc = JsonDocument.Parse(line); } catch { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var tsStr = root.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind==JsonValueKind.String ? tsEl.GetString() : null;
                    if (tsStr == null) continue;
                    if (!DateTime.TryParse(tsStr, out var parsed)) continue;
                    var tsUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                    if (tsUtc < fromUtc) continue;
                    double inBps = root.TryGetProperty("i", out var vi) && vi.ValueKind==JsonValueKind.Number ? vi.GetDouble() : 0;
                    double outBps = root.TryGetProperty("o", out var vo) && vo.ValueKind==JsonValueKind.Number ? vo.GetDouble() : 0;
                    double ui = root.TryGetProperty("ui", out var vui) && vui.ValueKind==JsonValueKind.Number ? vui.GetDouble() : 0;
                    double uo = root.TryGetProperty("uo", out var vuo) && vuo.ValueKind==JsonValueKind.Number ? vuo.GetDouble() : 0;
                    string speed = root.TryGetProperty("sp", out var vsp) && vsp.ValueKind==JsonValueKind.String ? vsp.GetString() ?? "" : "";
                    yield return (tsUtc,inBps,outBps,ui,uo,speed);
                }
            }
        }
    }

    public void CleanupOld()
    {
        try
        {
            foreach (var switchDir in Directory.GetDirectories(_baseDir))
            {
                foreach (var ifDir in Directory.GetDirectories(switchDir))
                {
                    foreach (var file in Directory.GetFiles(ifDir, "*.jsonl"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (DateTime.TryParse(name, out var day))
                        {
                            var dayUtc = DateTime.SpecifyKind(day, DateTimeKind.Utc);
                            if (dayUtc < DateTime.UtcNow.Date.AddDays(-_maxDays))
                            {
                                File.Delete(file);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}
