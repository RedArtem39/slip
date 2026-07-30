using System.Diagnostics;

namespace Slip;

internal static class TelegramControl
{
    private static readonly IReadOnlyList<IReadOnlyList<Button>> Keyboard = new List<IReadOnlyList<Button>>
    {
        new[] { new Button("⏰ 1h", "start:h:1"), new Button("⏰ 4h", "start:h:4"), new Button("⏰ 8h", "start:h:8") },
        new[] { new Button("📅 1d", "start:d:1"), new Button("📅 4d", "start:d:4"), new Button("📅 7d", "start:d:7") },
        new[] { new Button("⏹ Off", "off"), new Button("🔄 Refresh", "status") },
    };

    /// <summary>Handles "slip -telega ...": either "reset" or "&lt;token&gt;" (claim flow).</summary>
    public static async Task<int> RunSetupCommand(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("slip: usage - slip -telega \"bot_token\"   or   slip -telega reset");
            return 1;
        }

        if (args[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Reset();
            return 0;
        }

        return await Claim(args[1]);
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
            Console.WriteLine(
                $"slip: already linked to Telegram admin ID {existing.Value.AdminId}. " +
                "Run 'slip -telega reset' first if you want to relink.");
            return 1;
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
        if (parts.Length > 0) CommandParser.Execute(parts); // typed shortcuts still work; we render our own dashboard

        var (text, keyboard) = BuildDashboard();
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
            default:
                if (press.Data.StartsWith("start:h:") && double.TryParse(press.Data.AsSpan(8), out var hours))
                    SleepControl.Start(TimeSpan.FromHours(hours));
                else if (press.Data.StartsWith("start:d:") && double.TryParse(press.Data.AsSpan(8), out var days))
                    SleepControl.Start(TimeSpan.FromDays(days));
                break;
        }

        var (text, keyboard) = BuildDashboard();
        await client.EditMessageTextAsync(press.ChatId, press.MessageId, text, keyboard);
    }

    private static (string Text, IReadOnlyList<IReadOnlyList<Button>> Keyboard) BuildDashboard(string? header = null)
    {
        var info = SleepControl.GetStatusInfo();

        var status = info.Active
            ? $"🟢 <b>Awake mode active</b>\n" +
              $"Until {info.EndUtc!.Value.ToLocalTime():HH:mm, dd MMM} ({SleepControl.FormatSpan(info.Remaining!.Value)} left)\n" +
              "Monitor still follows its own Windows sleep timeout."
            : "⚪ <b>Not active</b>\nNormal sleep behavior in effect.";

        var prefix = header is null ? "" : header + "\n\n";
        var text = $"{prefix}🖥 <b>slip</b>\n\n{status}\n\nPick a duration, or turn it off:";

        return (text, Keyboard);
    }
}
