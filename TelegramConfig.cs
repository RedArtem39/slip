using System.Text.Json;

namespace Slip;

internal static class TelegramConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "slip");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "telegram.json");
    private static readonly string DaemonStateFile = Path.Combine(ConfigDir, "telegram-daemon.json");

    public readonly record struct Config(string BotToken, long AdminId);
    public readonly record struct DaemonState(int Pid);

    public static Config? Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return null;
            return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigFile));
        }
        catch { return null; }
    }

    public static void Save(Config c)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(c));
    }

    public static void DeleteConfig()
    {
        try { File.Delete(ConfigFile); } catch { /* ignore */ }
    }

    public static DaemonState? LoadDaemonState()
    {
        try
        {
            if (!File.Exists(DaemonStateFile)) return null;
            return JsonSerializer.Deserialize<DaemonState>(File.ReadAllText(DaemonStateFile));
        }
        catch { return null; }
    }

    public static void SaveDaemonState(DaemonState s)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(DaemonStateFile, JsonSerializer.Serialize(s));
    }

    public static void DeleteDaemonState()
    {
        try { File.Delete(DaemonStateFile); } catch { /* ignore */ }
    }
}
