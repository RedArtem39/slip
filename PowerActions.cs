using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Slip;

/// <summary>Carries out a run's "-then" action once its grace period is over.</summary>
internal static class PowerActions
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;
    private const int SePrivilegeEnabled = 0x2;

    public static void Run(ThenAction action)
    {
        switch (action)
        {
            case ThenAction.Sleep:
                EnableShutdownPrivilege();
                SetSuspendState(false, false, false);
                break;

            case ThenAction.Hibernate:
                EnableShutdownPrivilege();
                // Fails when hibernation is disabled (powercfg /h off) - sleeping beats staying up.
                if (!SetSuspendState(true, false, false)) SetSuspendState(false, false, false);
                break;

            case ThenAction.Shutdown:
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.Dispose();
                break;
        }
    }

    /// <summary>Suspend/hibernate require SeShutdownPrivilege, which users hold but is disabled by default.</summary>
    private static void EnableShutdownPrivilege()
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAdjustPrivileges | TokenQuery, out var token))
            return;

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
