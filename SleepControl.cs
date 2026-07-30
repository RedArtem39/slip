using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Slip;

/// <summary>
/// Core "keep the PC awake" logic, as pure functions that return the message
/// to show - used identically by the CLI and by the Telegram bot, so both
/// surfaces always behave exactly the same way.
/// </summary>
internal static class SleepControl
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "slip");

    private static readonly string StateFile = Path.Combine(StateDir, "state.json");

    public static string Help() => """
        slip - keep this PC awake without keeping the monitor on.

        Prevents Windows from sleeping/standby so background work (downloads,
        renders, scripts, servers) keeps running. The monitor is NOT affected -
        it still turns off on whatever schedule is set in Windows display
        settings; this only blocks system sleep/standby.

        Usage:
          slip <hours>          Stay awake for N hours   (e.g. slip 4)
          slip -d <days>        Stay awake for N days     (e.g. slip -d 4)
          slip off              Cancel - restore normal sleep behavior
          slip status           Show whether it's currently active
          slip -telega "token"  Link a Telegram bot for remote control
          slip -telega reset    Unlink the Telegram bot
          slip help             Show this help
        """;

    public readonly record struct StatusInfo(bool Active, DateTime? EndUtc, TimeSpan? Remaining);

    /// <summary>Structured status - used by the Telegram UI to render its own dashboard.</summary>
    public static StatusInfo GetStatusInfo()
    {
        var state = ReadState();
        if (state is null || state.Value.EndUtc <= DateTime.UtcNow || !IsRunning(state.Value.Pid))
        {
            TryDeleteState();
            return new StatusInfo(false, null, null);
        }

        return new StatusInfo(true, state.Value.EndUtc, state.Value.EndUtc - DateTime.UtcNow);
    }

    public static string Status()
    {
        var info = GetStatusInfo();
        if (!info.Active) return "slip: not active. Normal sleep behavior in effect.";

        return
            $"slip: active, sleep prevented until {info.EndUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm} " +
            $"({FormatSpan(info.Remaining!.Value)} left). Monitor timing is unaffected.";
    }

    public static string Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "slip: duration must be greater than zero.";

        StopQuiet(); // replace any run already in progress

        var endUtc = DateTime.UtcNow + duration;
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--daemon {endUtc.Ticks}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start slip daemon");
        WriteState(new StateData(proc.Id, endUtc));

        return
            $"slip: awake mode active until {endUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({FormatSpan(duration)}). " +
            "Monitor still follows its own Windows timeout. Send/run 'off' to cancel early.";
    }

    public static string Stop()
    {
        var state = ReadState();
        if (state is null) return "slip: nothing active.";

        if (IsRunning(state.Value.Pid))
        {
            try { Process.GetProcessById(state.Value.Pid).Kill(); }
            catch { /* already gone */ }
        }

        TryDeleteState();
        return "slip: cancelled, normal sleep behavior restored.";
    }

    /// <summary>Same as Stop(), but silent - used internally before starting a new run.</summary>
    private static void StopQuiet()
    {
        var state = ReadState();
        if (state is null) return;

        if (IsRunning(state.Value.Pid))
        {
            try { Process.GetProcessById(state.Value.Pid).Kill(); }
            catch { /* already gone */ }
        }

        TryDeleteState();
    }

    /// <summary>The actual background loop that blocks sleep - runs inside "slip --daemon &lt;ticks&gt;".</summary>
    public static void RunDaemon(DateTime endUtc)
    {
        try
        {
            while (DateTime.UtcNow < endUtc)
            {
                SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
                var remaining = endUtc - DateTime.UtcNow;
                var nap = remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30);
                if (nap > TimeSpan.Zero) Thread.Sleep(nap);
            }
        }
        finally
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            TryDeleteState();
        }
    }

    private readonly record struct StateData(int Pid, DateTime EndUtc);

    private static void WriteState(StateData data)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(data));
    }

    private static StateData? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<StateData>(File.ReadAllText(StateFile));
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteState()
    {
        try { File.Delete(StateFile); } catch { /* ignore */ }
    }

    private static bool IsRunning(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    public static string FormatSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.TotalDays:0.#} d";
        if (ts.TotalHours >= 1) return $"{ts.TotalHours:0.#} h";
        return $"{Math.Max(1, ts.TotalMinutes):0} min";
    }
}
