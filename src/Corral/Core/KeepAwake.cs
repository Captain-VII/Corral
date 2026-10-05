using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>
/// Empêche la mise en veille automatique via une « demande d'alimentation » Windows
/// (visible avec « powercfg /requests »). Contrairement à SetThreadExecutionState,
/// elle n'est pas liée à un thread : fiable depuis le timer du moteur.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    const int PowerRequestSystemRequired = 1;
    const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    IntPtr request = IntPtr.Zero;
    string? currentReason;

    public bool Active => currentReason != null;

    /// <summary>Active (avec la raison affichée par Windows) ou relâche la demande. Idempotent.</summary>
    public void Set(string? reason)
    {
        if (reason == currentReason)
            return;
        Release();
        if (reason == null)
            return;
        var context = new REASON_CONTEXT
        {
            Version = POWER_REQUEST_CONTEXT_VERSION,
            Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = reason,
        };
        request = PowerCreateRequest(ref context);
        if (request == IntPtr.Zero || request == new IntPtr(-1))
        {
            request = IntPtr.Zero;
            throw new Win32Exception();
        }
        if (!PowerSetRequest(request, PowerRequestSystemRequired))
        {
            var error = new Win32Exception();
            Release();
            throw error;
        }
        currentReason = reason;
    }

    void Release()
    {
        if (request != IntPtr.Zero)
        {
            if (currentReason != null)
                PowerClearRequest(request, PowerRequestSystemRequired);
            CloseHandle(request);
            request = IntPtr.Zero;
        }
        currentReason = null;
    }

    public void Dispose() => Release();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
