namespace Slip;

/// <summary>
/// Parses "4", "-d 4", "off", "status", "help" -shaped input into a response
/// message. Shared verbatim by the CLI argument path and by incoming
/// Telegram messages from the bound admin, so the bot has exactly the same
/// capabilities as the CLI - never a second, drifting implementation.
/// </summary>
internal static class CommandParser
{
    public static (bool Ok, string Message) Execute(string[] args)
    {
        if (args.Length == 0) return (true, SleepControl.Help());

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

            case "-d":
                if (args.Length < 2 || !double.TryParse(args[1], out var days))
                    return (false, "slip: -d requires a number of days, e.g. -d 4");
                return (true, SleepControl.Start(TimeSpan.FromDays(days)));

            default:
                if (double.TryParse(args[0], out var hours))
                    return (true, SleepControl.Start(TimeSpan.FromHours(hours)));
                return (false, $"slip: unrecognized command '{args[0]}'. Try 'help'.");
        }
    }
}
