using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

// slip is Windows-only (SetThreadExecutionState, powercfg, UAC) - say so once, for the analyzers.
[assembly: SupportedOSPlatform("windows")]

namespace Slip;

/// <summary>
/// "slip requests": shows what is currently holding the PC (or the monitor) awake, via
/// "powercfg /requests". That needs admin, so from a normal terminal slip relaunches itself
/// elevated (one UAC prompt), the elevated copy writes the report to a temp file, and this
/// process prints it - no need to go find an admin terminal.
/// </summary>
internal static class PowerRequests
{
    public const string ElevatedFlag = "--requests-elevated";

    private const int ErrorCancelled = 1223; // user said "No" on the UAC prompt

    private const uint Utf8CodePage = 65001;

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    public static int Show()
    {
        if (IsElevated())
        {
            Console.WriteLine(Report());
            return 0;
        }

        var outFile = Path.Combine(Path.GetTempPath(), $"slip-requests-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo
        {
            FileName = Process.GetCurrentProcess().MainModule!.FileName,
            Arguments = $"{ElevatedFlag} \"{outFile}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            Console.WriteLine("slip: powercfg needs admin - confirm the UAC prompt...");
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            Console.Error.WriteLine("slip: UAC prompt declined - can't read power requests without admin.");
            return 1;
        }

        try
        {
            Console.WriteLine(File.ReadAllText(outFile));
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("slip: the elevated helper didn't produce a report.");
            return 1;
        }
        finally
        {
            try { File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    /// <summary>Runs inside the elevated copy: "slip --requests-elevated &lt;file&gt;".</summary>
    public static int WriteReport(string outFile)
    {
        File.WriteAllText(outFile, Report());
        return 0;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>A one-line verdict on slip itself, followed by powercfg's full listing.</summary>
    private static string Report()
    {
        var raw = RunPowercfg();

        // Section headers ("SYSTEM:", "DISPLAY:", ...) aren't localized; entries under each
        // are lines like "[PROCESS] \Device\HarddiskVolume3\...\slip.exe".
        var held = new List<string>();
        string? section = null;
        foreach (var line in raw.Split('\n').Select(l => l.Trim()))
        {
            if (line.EndsWith(':') && line.All(c => char.IsUpper(c) || c == ':')) section = line.TrimEnd(':');
            else if (section is not null && line.EndsWith("\\slip.exe", StringComparison.OrdinalIgnoreCase)) held.Add(section);
        }

        var verdict = held.Count == 0
            ? "slip: slip isn't holding anything right now."
            : $"slip: slip is holding {string.Join(" + ", held.Distinct())}" +
              (held.Contains("DISPLAY") ? " (monitor kept on)." : " (monitor follows its own timeout).");

        return $"{verdict}\n\n{raw.TrimEnd()}";
    }

    private static string RunPowercfg()
    {
        // powercfg encodes its output with the console code page. The OEM default (866 on
        // Russian-locale Windows) can't represent e.g. Ukrainian "і" in driver descriptions,
        // so switch our console to UTF-8 for the call - powercfg shares it - then switch back.
        var previousCp = GetConsoleOutputCP();
        var utf8 = previousCp != 0 && SetConsoleOutputCP(Utf8CodePage);

        Encoding encoding;
        if (utf8) encoding = new UTF8Encoding(false);
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { encoding = Encoding.GetEncoding((int)GetOEMCP()); }
            catch { encoding = Encoding.UTF8; }
        }

        try
        {
            var psi = new ProcessStartInfo("powercfg", "/requests")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        finally
        {
            if (utf8) SetConsoleOutputCP(previousCp);
        }
    }
}
