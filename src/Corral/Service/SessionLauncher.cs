using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Corral.Service;

/// <summary>Lance un programme dans la session d'un utilisateur, depuis le service (compte SYSTEM).</summary>
static class SessionLauncher
{
    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>Lève une exception si la session n'a plus d'utilisateur connecté.</summary>
    public static void Launch(string exe, string arguments, int sessionId)
    {
        if (!WTSQueryUserToken((uint)sessionId, out var token))
            throw new Win32Exception();
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
                environment = IntPtr.Zero;
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var command = new StringBuilder($"\"{exe}\" {arguments}");
            if (!CreateProcessAsUser(token, exe, command, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT,
                    environment, Path.GetDirectoryName(exe), ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }
}
