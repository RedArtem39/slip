using System.Diagnostics;
using System.Net;

namespace Slip;

internal static class TelegramControl
{
    private static readonly IReadOnlyList<Button>[] DurationRows =
    {
        new[] { new Button("⏰ 1h", "start:h:1"), new Button("⏰ 4h", "start:h:4"), new Button("⏰ 8h", "start:h:8") },
        new[] { new Button("📅 1d", "start:d:1"), new Button("📅 4d", "start:d:4"), new Button("📅 7d", "start:d:7") },
    };

    private static readonly IReadOnlyList<Button> ControlRow =
        new[] { new Button("⏹ Off", "off"), new Button("🔄 Refresh", "status") };

    /// <summary>Handles "slip -telega ...": "reset", "status", "start", or "&lt;token&gt;" (claim flow).</summary>
    public static async Task<int> RunSetupCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "slip: usage - slip -telega \"bot_token\"   or   slip -telega reset   " +
                "or   slip -telega status   or   slip -telega start");
            return 1;
        }

        if (args[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            return 0;
        }

        if (args[1].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus();
            return 0;
        }

        if (args[1].Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return EnsureDaemonRunning(announce: true) ? 0 : 1;
        }

        return await Claim(args[1]);
    }

    private static void ShowStatus()
    {
        var config = TelegramConfig.Load();
        if (config is null)
        {
            Console.WriteLine("slip: Telegram not linked. Run 'slip -telega \"token\"' to link.");
            return;
        }

        Console.WriteLine(IsDaemonRunning()
            ? $"slip: Telegram linked to admin ID {config.Value.AdminId}, daemon running."
            : $"slip: Telegram linked to admin ID {config.Value.AdminId}, but the daemon is NOT running. " +
              "It'll auto-restart on your next 'slip' command, or run 'slip -telega start' to do it now.");
    }

    /// <summary>
    /// If Telegram is linked but its background daemon isn't running (e.g. it died on a PC reboot),
    /// silently starts it again. Called on every normal CLI invocation so the bot self-heals without
    /// the user having to notice or run 'slip -telega reset'. No-op if Telegram was never linked.
    /// </summary>
    public static bool EnsureDaemonRunning(bool announce = false)
    {
        var config = TelegramConfig.Load();
        if (config is null) return false;

        if (IsDaemonRunning())
        {
            if (announce) Console.WriteLine("slip: Telegram daemon already running.");
            return true;
        }

        StartDaemon();
        if (announce) Console.WriteLine("slip: Telegram daemon restarted.");
        return true;
    }

    private static bool IsDaemonRunning()
    {
        var state = TelegramConfig.LoadDaemonState();
        if (state is null) return false;

        try { Process.GetProcessById(state.Value.Pid); return true; }
        catch { return false; }
    }

    private static void Reset()
    {
        var daemonState = TelegramConfig.LoadDaemonState();
        if (daemonState is not null)
        {
            try { Process.GetProcessById(daemonState.Value.Pid).Kill(); }
            catch { /* already gone */ }
        }

        TelegramConfig.DeleteDaemonState();
        TelegramConfig.DeleteConfig();
        Console.WriteLine("slip: Telegram bot unlinked. Back to CLI-only. Run 'slip -telega \"token\"' again to reconnect.");
    }

    private static async Task<int> Claim(string token)
    {
        var existing = TelegramConfig.Load();
        if (existing is not null)
        {
            if (IsDaemonRunning())
            {
                Console.WriteLine(
                    $"slip: already linked to Telegram admin ID {existing.Value.AdminId}, daemon running. " +
                    "Run 'slip -telega reset' first if you want to relink.");
                return 1;
            }

            // Config is intact but the daemon died (e.g. PC reboot) - heal instead of demanding a full reset+relink.
            Console.WriteLine(
                $"slip: already linked to Telegram admin ID {existing.Value.AdminId}; " +
                "daemon wasn't running - restarting it now.");
            StartDaemon();
            return 0;
        }

        var client = new TelegramClient(token);
        Console.WriteLine("slip: checking bot token...");
        if (!await client.ValidateAsync())
        {
            Console.Error.WriteLine("slip: that token doesn't look valid (getMe failed). Nothing was saved.");
            return 1;
        }

        Console.WriteLine("slip: token OK. Message your bot now (anything) - waiting...");

        using var cts = new CancellationTokenSource();
        await foreach (var evt in client.PollUpdatesAsync(cts.Token))
        {
            if (evt is not TextMessage msg) continue; // buttons can't exist before linking

            var who = msg.Username is null ? $"ID {msg.UserId}" : $"@{msg.Username} (ID {msg.UserId})";
            Console.Write($"slip: {who} just messaged the bot. Is this you? [y/n]: ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (answer == "y" || answer == "yes")
            {
                TelegramConfig.Save(new TelegramConfig.Config(token, msg.UserId));
                var (text, keyboard) = BuildDashboard("✅ <b>Linked to slip.</b>");
                await client.SendMessageAsync(msg.ChatId, text, keyboard);
                Console.WriteLine($"slip: linked. Only Telegram ID {msg.UserId} can control slip now.");
                StartDaemon();
                return 0;
            }

            Console.WriteLine("slip: rejected, still waiting for the right person...");
        }

        return 1;
    }

    private static void StartDaemon()
    {
        var exePath = Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--telegram-daemon",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start telegram daemon");
        TelegramConfig.SaveDaemonState(new TelegramConfig.DaemonState(proc.Id));
    }

    /// <summary>The persistent background listener - runs inside "slip --telegram-daemon".</summary>
    public static async Task RunDaemon()
    {
        var config = TelegramConfig.Load();
        if (config is null) return;

        var client = new TelegramClient(config.Value.BotToken);
        using var cts = new CancellationTokenSource();

        await foreach (var evt in client.PollUpdatesAsync(cts.Token))
        {
            switch (evt)
            {
                case TextMessage msg when msg.UserId == config.Value.AdminId:
                    await HandleText(client, msg);
                    break;

                case ButtonPress press when press.UserId == config.Value.AdminId:
                    await HandleButton(client, press);
                    break;

                default:
                    // not the bound admin - respond to nothing, reveal nothing
                    break;
            }
        }
    }

    private static async Task HandleText(TelegramClient client, TextMessage msg)
    {
        var parts = msg.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? header = null;
        if (parts.Length > 0)
        {
            // Typed commands work exactly like the CLI; we render our own dashboard, but surface errors.
            var (ok, message) = CommandParser.Execute(parts);
            if (!ok) header = "⚠️ " + WebUtility.HtmlEncode(message);
        }

        var (text, keyboard) = BuildDashboard(header);
        await client.SendMessageAsync(msg.ChatId, text, keyboard);
    }

    private static async Task HandleButton(TelegramClient client, ButtonPress press)
    {
        await client.AnswerCallbackQueryAsync(press.CallbackId);

        switch (press.Data)
        {
            case "off":
                SleepControl.Stop();
                break;
            case "status":
                break; // just refresh the dashboard below
            case "screen":
                // Flip -m on the current run, keeping everything else about it.
                var run = SleepControl.Current();
                if (run is not null) SleepControl.Start(run with { KeepDisplay = !run.KeepDisplay });
                break;
            default:
                if (TryParseSpan(press.Data, "start:", out var span))
                    SleepControl.Start(new RunState { EndUtc = DateTime.UtcNow + span });
                else if (TryParseSpan(press.Data, "ext:", out var by))
                    SleepControl.Extend(by);
                break;
        }

        var (text, keyboard) = BuildDashboard();
        await client.EditMessageTextAsync(press.ChatId, press.MessageId, text, keyboard);
    }

    /// <summary>Parses button data like "start:h:4" / "ext:d:1" into a span.</summary>
    private static bool TryParseSpan(string data, string prefix, out TimeSpan span)
    {
        span = default;
        if (!data.StartsWith(prefix)) return false;

        var rest = data.AsSpan(prefix.Length);
        if (rest.Length < 3 || rest[1] != ':' || !double.TryParse(rest[2..], out var n)) return false;

        span = rest[0] == 'd' ? TimeSpan.FromDays(n) : TimeSpan.FromHours(n);
        return true;
    }

    private static (string Text, IReadOnlyList<IReadOnlyList<Button>> Keyboard) BuildDashboard(string? header = null)
    {
        var run = SleepControl.Current();

        var status = run switch
        {
            null => "⚪ <b>Not active</b>\nNormal sleep behavior in effect.\n" +
                    "Type e.g. <code>-m 4</code>, <code>until 23:30</code>, " +
                    "<code>while ffmpeg -then shutdown</code>.",
            { ActionAtUtc: not null } => "🟠 <b>Finishing</b>\n" + WebUtility.HtmlEncode(SleepControl.Describe(run)),
            _ => "🟢 <b>Awake mode active</b>\n" + WebUtility.HtmlEncode(SleepControl.Describe(run)),
        };

        var prefix = header is null ? "" : header + "\n\n";
        var text = $"{prefix}🖥 <b>slip</b>\n\n{status}\n\nPick a duration, or turn it off:";

        var keyboard = new List<IReadOnlyList<Button>>(DurationRows);
        if (run is not null)
        {
            if (run.EndUtc is not null) keyboard.Add(ExtendRow);
            if (run.ActionAtUtc is null)
                keyboard.Add(new[] { new Button(run.KeepDisplay ? "🌙 Let monitor sleep" : "💡 Keep monitor on", "screen") });
        }
        keyboard.Add(ControlRow);

        return (text, keyboard);
    }

    private static readonly IReadOnlyList<Button> ExtendRow =
        new[] { new Button("➕ 1h", "ext:h:1"), new Button("➕ 4h", "ext:h:4"), new Button("➕ 1d", "ext:d:1") };

    /// <summary>Buttons under the "ending soon" heads-up.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<Button>> ExtendKeyboard = new[]
    {
        ExtendRow,
        new[] { new Button("⏹ Off now", "off"), new Button("🔄 Status", "status") },
    };

    /// <summary>Buttons under the "PC will shut down in 60 s" warning.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<Button>> CancelActionKeyboard = new[]
    {
        new[] { new Button("✋ Cancel", "off") },
        ExtendRow,
    };

    /// <summary>
    /// Best-effort one-off message to the bound admin, used by the awake daemon for "ending soon" /
    /// "finished" notices. Silently does nothing if Telegram isn't linked or the network is down.
    /// Button presses on it are handled by the Telegram daemon like any other dashboard button.
    /// </summary>
    public static void Notify(string html, IReadOnlyList<IReadOnlyList<Button>>? keyboard = null)
    {
        var config = TelegramConfig.Load();
        if (config is null) return;

        try
        {
            // In a private chat the chat ID is the user's ID.
            new TelegramClient(config.Value.BotToken)
                .SendMessageAsync(config.Value.AdminId, html, keyboard)
                .Wait(TimeSpan.FromSeconds(20));
        }
        catch { /* best effort */ }
    }
}
