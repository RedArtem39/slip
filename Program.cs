namespace Slip;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("-telega", StringComparison.OrdinalIgnoreCase))
        {
            return await TelegramControl.RunSetupCommand(args);
        }

        if (args.Length > 0 && args[0].Equals("--telegram-daemon", StringComparison.OrdinalIgnoreCase))
        {
            await TelegramControl.RunDaemon();
            return 0;
        }

        if (args.Length > 1 && args[0].Equals(PowerRequests.ElevatedFlag, StringComparison.OrdinalIgnoreCase))
        {
            return PowerRequests.WriteReport(args[1]);
        }

        if (args.Length > 0 && args[0].Equals("--daemon", StringComparison.OrdinalIgnoreCase))
        {
            var run = args.Length < 2 ? null : SleepControl.DecodeDaemonArg(args[1]);
            if (run is null)
            {
                Console.Error.WriteLine("slip: bad daemon args");
                return 1;
            }
            SleepControl.RunDaemon(run);
            return 0;
        }

        // Self-heal: if Telegram is linked but its daemon died (e.g. a PC reboot), bring it
        // back on the next ordinary 'slip' call instead of leaving the bot silently dead.
        TelegramControl.EnsureDaemonRunning();

        if (args.Length == 0)
        {
            Console.WriteLine(SleepControl.Status());
            return 0;
        }

        // CLI-only (not in CommandParser): it may pop a UAC prompt, which makes no sense from Telegram.
        if (args.Length == 1 && args[0].Equals("requests", StringComparison.OrdinalIgnoreCase))
        {
            return PowerRequests.Show();
        }

        var (ok, message) = CommandParser.Execute(args);
        if (ok) Console.WriteLine(message);
        else Console.Error.WriteLine(message);
        return ok ? 0 : 1;
    }
}
