using System.Text;

namespace SwitchMonitoring.Services;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static string LogPath => Path.Combine(AppContext.BaseDirectory, "monitor-log.txt");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    public static void Exception(string context, Exception ex)
        => Write("EXC", context + " | " + ex.GetType().Name + ": " + ex.Message);

    private static void Write(string level, string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {msg}\n";
            lock (_lock)
            {
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch { /* ignore logging errors */ }
    }
}
