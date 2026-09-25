using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Slip;

/// <summary>What to do once a run finishes on its own (timer ran out / watched process exited).</summary>
internal enum ThenAction { None, Sleep, Hibernate, Shutdown }

/// <summary>
/// Everything that describes one awake run. Persisted as state.json and handed to the
/// background daemon verbatim, so the CLI, the daemon and the Telegram bot all agree on it.
/// </summary>
internal sealed record RunState
{
    public int Pid { get; init; }

    /// <summary>Null = no time limit ("forever", or "while" without a cap).</summary>
    public DateTime? EndUtc { get; init; }

    public bool KeepDisplay { get; init; }

    /// <summary>Process name (or PID) to stay awake for; the run ends once it's gone.</summary>
    public string? WhileProcess { get; init; }

    public ThenAction Then { get; init; }

    /// <summary>Set during the grace period between the run finishing and <see cref="Then"/> firing.</summary>
    public DateTime? ActionAtUtc { get; init; }
}

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
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    /// <summary>How long before the end the Telegram "ending soon" heads-up goes out.</summary>
    private static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(10);

    /// <summary>Delay between a run finishing and its -then action, so it can still be cancelled.</summary>
    private static readonly TimeSpan ActionGrace = TimeSpan.FromSeconds(60);

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
          slip <hours>          Stay awake for N hours   (e.g. slip 4, slip 90m, slip 2d)
          slip -d <days>        Stay awake for N days     (e.g. slip -d 4)
          slip until <HH:mm>    Stay awake until a clock time (e.g. slip until 23:30)
          slip while <process>  Stay awake while a process runs (e.g. slip while ffmpeg)
                                Add a duration to cap it: slip while ffmpeg 6
          slip forever          Stay awake until 'slip off'
          slip +<hours>         Extend the current run (e.g. slip +2, slip +30m)
          slip off              Cancel - restore normal sleep behavior
          slip status           Show whether it's currently active
          slip requests         Show what's keeping the PC/monitor awake (powercfg /requests);
                                asks for UAC if this terminal isn't admin

        Options (combine with any of the above):
          -m                    Also keep the monitor on and block the idle lock screen
          -then <action>        When the run finishes: sleep, hibernate or shutdown
                                (60 s grace period - 'slip off' cancels it)
                                e.g. slip while ffmpeg -then shutdown

        Telegram:
          slip -telega "token"  Link a Telegram bot for remote control
          slip -telega status   Show whether the bot is linked and running
          slip -telega start    Restart the bot daemon if it died
          slip -telega reset    Unlink the Telegram bot
          slip help             Show this help
        """;

    /// <summary>The active run, or null if nothing is running.</summary>
    public static RunState? Current()
    {
        var state = ReadState();
        if (state is null || !IsSlipProcess(state.Pid))
        {
            TryDeleteState();
            return null;
        }

        return state;
    }

    public static string Status()
    {
        var run = Current();
        if (run is null) return "slip: not active. Normal sleep behavior in effect.";
        if (run.ActionAtUtc is not null) return "slip: " + Describe(run);

        return $"slip: active, sleep prevented {Describe(run)}";
    }

    public static string Start(RunState run)
    {
        if (run.EndUtc is { } end && end <= DateTime.UtcNow)
            return "slip: duration must be greater than zero.";

        if (run.WhileProcess is { } name && !IsProcessAlive(name))
            return $"slip: no running process matches '{name}'. Check the name in Task Manager (Details tab).";

        StopQuiet(); // replace any run already in progress

        run = run with { Pid = 0, ActionAtUtc = null };
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(run)));

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--daemon {payload}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start slip daemon");
        run = run with { Pid = proc.Id };
        WriteState(run);

        return $"slip: awake mode active {Describe(run)} Send/run 'off' to cancel early.";
    }

    /// <summary>Pushes the current run's end time out by <paramref name="by"/>, keeping all its options.</summary>
    public static string Extend(TimeSpan by)
    {
        if (by <= TimeSpan.Zero) return "slip: extension must be greater than zero.";

        var run = Current();
        if (run is null) return "slip: nothing active to extend. Start one with 'slip <hours>'.";
        if (run.EndUtc is null)
            return "slip: the current run has no time limit, so there's nothing to extend.";

        // In the -then grace period the end is already in the past: extend from now instead.
        var from = run.EndUtc.Value > DateTime.UtcNow ? run.EndUtc.Value : DateTime.UtcNow;
        return Start(run with { EndUtc = from + by });
    }

    public static string Stop()
    {
        var run = Current();
        if (run is null) return "slip: nothing active.";

        StopQuiet();
        return run.ActionAtUtc is null
            ? "slip: cancelled, normal sleep behavior restored."
            : $"slip: cancelled - the pending {ActionNoun(run.Then)} won't happen. Normal sleep behavior restored.";
    }

    /// <summary>Same as Stop(), but silent - used internally before starting a new run.</summary>
    private static void StopQuiet()
    {
        var state = ReadState();
        if (state is null) return;

        if (IsSlipProcess(state.Pid))
        {
            try { Process.GetProcessById(state.Pid).Kill(); }
            catch { /* already gone */ }
        }

        TryDeleteState();
    }

    /// <summary>
    /// Human-readable description of a run, e.g. "until 2026-09-25 18:00 (3 h left). Monitor timing
    /// is unaffected. Then: shut down." Shared by the CLI and the Telegram dashboard.
    /// </summary>
    public static string Describe(RunState run)
    {
        var now = DateTime.UtcNow;

        if (run.ActionAtUtc is { } at)
        {
            var secs = Math.Max(0, (int)(at - now).TotalSeconds);
            return $"run finished - {ActionVerb(run.Then)} in {secs} s. Send/run 'off' to cancel.";
        }

        var until = run.EndUtc is { } end
            ? $"{end.ToLocalTime():yyyy-MM-dd HH:mm} ({FormatSpan(end - now)} left)"
            : null;

        var when = (run.WhileProcess, until) switch
        {
            (null, null) => "with no time limit",
            (null, _) => $"until {until}",
            (_, null) => $"while '{run.WhileProcess}' is running",
            _ => $"while '{run.WhileProcess}' is running, but no later than {until}",
        };

        var monitor = run.KeepDisplay ? "Monitor is kept on too." : "Monitor timing is unaffected.";
        var then = run.Then == ThenAction.None ? "" : $" Then: {ActionNoun(run.Then)}.";

        return $"{when}. {monitor}{then}";
    }

    public static string ActionNoun(ThenAction a) => a switch
    {
        ThenAction.Sleep => "sleep",
        ThenAction.Hibernate => "hibernate",
        ThenAction.Shutdown => "shut down",
        _ => "nothing",
    };

    private static string ActionVerb(ThenAction a) => a switch
    {
        ThenAction.Sleep => "going to sleep",
        ThenAction.Hibernate => "hibernating",
        ThenAction.Shutdown => "shutting down",
        _ => "finishing",
    };

    /// <summary>Decodes the "--daemon &lt;payload&gt;" argument written by <see cref="Start"/>.</summary>
    public static RunState? DecodeDaemonArg(string payload)
    {
        try { return JsonSerializer.Deserialize<RunState>(Encoding.UTF8.GetString(Convert.FromBase64String(payload))); }
        catch { return null; }
    }

    /// <summary>The actual background loop that blocks sleep - runs inside "slip --daemon &lt;payload&gt;".</summary>
    public static void RunDaemon(RunState run)
    {
        var flags = ExecutionState.Continuous | ExecutionState.SystemRequired;
        if (run.KeepDisplay) flags |= ExecutionState.DisplayRequired;

        // Runs shorter than the warning window never get a heads-up - it'd arrive right away.
        var warned = run.EndUtc is not { } firstEnd || firstEnd - DateTime.UtcNow <= WarnBefore;

        try
        {
            string reason;
            while (true)
            {
                SetThreadExecutionState(flags);
                var now = DateTime.UtcNow;

                if (run.EndUtc is { } end && now >= end) { reason = "time's up"; break; }
                if (run.WhileProcess is { } name && !IsProcessAlive(name)) { reason = $"'{name}' finished"; break; }

                if (!warned && run.EndUtc is { } soon && soon - now <= WarnBefore)
                {
                    warned = true;
                    var then = run.Then == ThenAction.None ? "" : $", then it'll {ActionNoun(run.Then)}";
                    TelegramControl.Notify(
                        $"⏳ <b>slip ends in {FormatSpan(soon - now)}</b>{then}.\nExtend?",
                        TelegramControl.ExtendKeyboard);
                }

                Thread.Sleep(NextNap(run.EndUtc, now));
            }

            if (run.Then == ThenAction.None)
            {
                TelegramControl.Notify($"🏁 <b>slip finished</b> ({System.Net.WebUtility.HtmlEncode(reason)}).\nNormal sleep behavior restored.");
                return;
            }

            var actionAt = DateTime.UtcNow + ActionGrace;
            WriteState(run with { Pid = Environment.ProcessId, ActionAtUtc = actionAt });
            TelegramControl.Notify(
                $"🏁 <b>slip finished</b> ({System.Net.WebUtility.HtmlEncode(reason)}).\n" +
                $"⚠️ PC will {ActionNoun(run.Then)} in {(int)ActionGrace.TotalSeconds} s.",
                TelegramControl.CancelActionKeyboard);

            // Keep holding the system up during the grace period, so it can't doze off early.
            while (DateTime.UtcNow < actionAt)
            {
                SetThreadExecutionState(flags);
                Thread.Sleep(NextNap(actionAt, DateTime.UtcNow));
            }

            SetThreadExecutionState(ExecutionState.Continuous);
            TryDeleteState();
            PowerActions.Run(run.Then);
        }
        finally
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            TryDeleteState();
        }
    }

    private static TimeSpan NextNap(DateTime? endUtc, DateTime now)
    {
        var nap = TimeSpan.FromSeconds(15);
        if (endUtc is { } end && end - now < nap) nap = end - now;
        return nap > TimeSpan.Zero ? nap : TimeSpan.Zero;
    }

    /// <summary>True if a process with this name (".exe" optional) or this numeric PID is running.</summary>
    private static bool IsProcessAlive(string nameOrPid)
    {
        if (int.TryParse(nameOrPid, out var pid))
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch { return false; }
        }

        var name = nameOrPid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? nameOrPid[..^4] : nameOrPid;
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private static void WriteState(RunState data)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(data));
    }

    private static RunState? ReadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(StateFile));
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

    /// <summary>Alive and actually a slip process - guards against a recycled PID.</summary>
    private static bool IsSlipProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName.Equals("slip", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string FormatSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.TotalDays:0.#} d";
        if (ts.TotalHours >= 1) return $"{ts.TotalHours:0.#} h";
        return $"{Math.Max(1, ts.TotalMinutes):0} min";
    }
}
