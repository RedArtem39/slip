namespace Slip;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(SleepControl.Status());
            return 0;
        }

        if (args[0].Equals("-telega", StringComparison.OrdinalIgnoreCase))
        {
            return await TelegramControl.RunSetupCommand(args);
        }

        if (args[0].Equals("--telegram-daemon", StringComparison.OrdinalIgnoreCase))
        {
            await TelegramControl.RunDaemon();
            return 0;
        }

        if (args[0].Equals("--daemon", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || !long.TryParse(args[1], out var endTicks))
            {
                Console.Error.WriteLine("slip: bad daemon args");
                return 1;
            }
            SleepControl.RunDaemon(new DateTime(endTicks, DateTimeKind.Utc));
            return 0;
        }

        var (ok, message) = CommandParser.Execute(args);
        if (ok) Console.WriteLine(message);
        else Console.Error.WriteLine(message);
        return ok ? 0 : 1;
    }
}
