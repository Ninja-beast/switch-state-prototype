using System.Text;

namespace SwitchMonitoring.Services;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "monitor-log.txt");
    private const long MaxBytesBeforeRotate = 5_000_000; // 5MB
    private const int KeepRotateFiles = 5;

    private static string MaskSecrets(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return msg;
        // Mask simple community/password tokens (very naive)
        string MaskToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token;
            if (token.Length <= 4) return new string('*', token.Length);
            return token.Substring(0,2) + new string('*', token.Length-4) + token[^2..];
        }
        try
        {
            // community=rsec010817 => community=rsec****17
            msg = System.Text.RegularExpressions.Regex.Replace(msg, @"(community=)([^\s;,:]+)", m => m.Groups[1].Value + MaskToken(m.Groups[2].Value), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            msg = System.Text.RegularExpressions.Regex.Replace(msg, @"(password=)([^\s;,:]+)", m => m.Groups[1].Value + MaskToken(m.Groups[2].Value), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch { }
        return msg;
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var fi = new FileInfo(LogPath);
            if (fi.Length < MaxBytesBeforeRotate) return;
            for (int i=KeepRotateFiles; i>=1; i--)
            {
                var src = i==1 ? LogPath : LogPath + $".{i-1}";
                var dst = LogPath + $".{i}";
                if (File.Exists(dst)) File.Delete(dst);
                if (File.Exists(src)) File.Move(src, dst);
            }
            File.WriteAllText(LogPath, string.Empty);
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string context, Exception ex)
        => Write("EXC", context + " | " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string msg)
    {
        try
        {
            RotateIfNeeded();
            msg = MaskSecrets(msg);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {msg}\n";
            lock (_lock)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch { /* ignore logging errors */ }
    }
}
