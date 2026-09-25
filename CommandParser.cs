using System.Globalization;

namespace Slip;

/// <summary>
/// Parses "4", "-d 4", "until 23:30", "while ffmpeg", "forever", "+2", "off", "status", "help"
/// -shaped input (plus the "-m" and "-then" options) into a response message. Shared verbatim by
/// the CLI argument path and by incoming Telegram messages from the bound admin, so the bot has
/// exactly the same capabilities as the CLI - never a second, drifting implementation.
/// </summary>
internal static class CommandParser
{
    private const string NeedsDuration =
        "slip: that needs a duration - e.g. 'slip -m 4', 'slip until 23:30 -then sleep', " +
        "'slip while ffmpeg -then shutdown'.";

    public static (bool Ok, string Message) Execute(string[] args)
    {
        if (args.Length == 0) return (true, SleepControl.Help());

        if (args.Length == 1)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "help":
                case "-h":
                case "--help":
                    return (true, SleepControl.Help());

                case "status":
                    return (true, SleepControl.Status());

                case "off":
                    return (true, SleepControl.Stop());
            }

            if (args[0].StartsWith('+'))
            {
                return TryParseDuration(args[0][1..], out var by)
                    ? (true, SleepControl.Extend(by))
                    : (false, $"slip: can't read '{args[0]}' - try +2, +30m or +1d.");
            }
        }

        var keepDisplay = false;
        var then = ThenAction.None;
        string? whileProcess = null;
        DateTime? endUtc = null;
        var limits = 0; // how many of: duration / -d / until / forever were given

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (arg.ToLowerInvariant())
            {
                case "-m":
                    keepDisplay = true;
                    break;

                case "-then":
                case "--then":
                    if (next is null || !TryParseAction(next, out then))
                        return (false, "slip: -then needs one of: sleep, hibernate, shutdown.");
                    i++;
                    break;

                case "-d":
                    if (next is null || !double.TryParse(next, CultureInfo.InvariantCulture, out var days))
                        return (false, "slip: -d requires a number of days, e.g. -d 4");
                    endUtc = DateTime.UtcNow + TimeSpan.FromDays(days);
                    limits++;
                    i++;
                    break;

                case "until":
                    if (next is null || !TryParseClock(next, out var untilUtc))
                        return (false, "slip: until needs a clock time, e.g. until 23:30");
                    endUtc = untilUtc;
                    limits++;
                    i++;
                    break;

                case "while":
                    if (next is null)
                        return (false, "slip: while needs a process name or PID, e.g. while ffmpeg");
                    whileProcess = next;
                    i++;
                    break;

                case "forever":
                case "inf":
                    endUtc = null;
                    limits++;
                    break;

                default:
                    if (!TryParseDuration(arg, out var span))
                        return (false, $"slip: unrecognized command '{arg}'. Try 'help'.");
                    endUtc = DateTime.UtcNow + span;
                    limits++;
                    break;
            }
        }

        if (limits > 1)
            return (false, "slip: pick just one of: a duration, -d, until, forever.");
        if (limits == 0 && whileProcess is null)
            return (false, NeedsDuration);

        return (true, SleepControl.Start(new RunState
        {
            EndUtc = endUtc,
            KeepDisplay = keepDisplay,
            WhileProcess = whileProcess,
            Then = then,
        }));
    }

    /// <summary>"4" / "4h" = hours, "30m" = minutes, "2d" = days.</summary>
    private static bool TryParseDuration(string s, out TimeSpan span)
    {
        span = default;
        if (s.Length == 0) return false;

        var unit = char.ToLowerInvariant(s[^1]);
        var number = char.IsLetter(unit) ? s[..^1] : s;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return false;

        switch (unit)
        {
            case 'm': span = TimeSpan.FromMinutes(n); return true;
            case 'd': span = TimeSpan.FromDays(n); return true;
            case 'h':
            case var c when char.IsDigit(c) || c == '.':
                span = TimeSpan.FromHours(n);
                return true;
            default:
                return false;
        }
    }

    /// <summary>"23:30" or "8" = the next time the local clock reads that (today, or tomorrow if already past).</summary>
    private static bool TryParseClock(string s, out DateTime endUtc)
    {
        endUtc = default;
        if (!TimeOnly.TryParseExact(s, new[] { "H:mm", "HH:mm", "%H" }, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var clock))
            return false;

        var local = DateTime.Today + clock.ToTimeSpan();
        if (local <= DateTime.Now) local = local.AddDays(1);
        endUtc = local.ToUniversalTime();
        return true;
    }

    private static bool TryParseAction(string s, out ThenAction action)
    {
        action = s.ToLowerInvariant() switch
        {
            "sleep" => ThenAction.Sleep,
            "hibernate" or "hib" => ThenAction.Hibernate,
            "shutdown" or "off" => ThenAction.Shutdown,
            _ => ThenAction.None,
        };
        return action != ThenAction.None;
    }
}
