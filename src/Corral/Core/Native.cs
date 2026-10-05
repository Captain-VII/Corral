using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>Identifie un processus de façon unique (le PID seul peut être réutilisé).</summary>
public readonly record struct ProcKey(int Pid, string Name, long StartTicks);

internal static class Native
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    public static int GetForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return -1;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }
}

/// <summary>CPU système global en %, calculé entre deux appels.</summary>
public sealed class SystemCpuSampler
{
    long prevIdle, prevKernel, prevUser;
    bool hasPrevious;

    public double Sample()
    {
        if (!Native.GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;
        double result = 0;
        if (hasPrevious)
        {
            // Le temps noyau inclut le temps inactif.
            long total = (kernel - prevKernel) + (user - prevUser);
            long idleDelta = idle - prevIdle;
            if (total > 0)
                result = Math.Clamp((1.0 - (double)idleDelta / total) * 100, 0, 100);
        }
        (prevIdle, prevKernel, prevUser, hasPrevious) = (idle, kernel, user, true);
        return result;
    }
}
