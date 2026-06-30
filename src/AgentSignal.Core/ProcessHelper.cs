using System.Runtime.InteropServices;

namespace AgentSignal.Core;

/// <summary>
/// All per-OS process code lives here: walking up the process ancestry to find the agent PID, and
/// checking whether a PID is still alive. Centralised because getting parent-process info differs per
/// OS (Windows toolhelp snapshot, Linux /proc, macOS ps). Liveness rule used by the widget:
/// SessionEnd OR process-dead → off. No inactivity timeout.
/// </summary>
public static class ProcessHelper
{
    private const int MaxHops = 64;

    /// <summary>True if a process with this id is currently running.</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            if (OperatingSystem.IsWindows()) return IsAliveWindows(pid);
            if (OperatingSystem.IsLinux()) return IsAliveLinux(pid);
            if (OperatingSystem.IsMacOS()) return IsAliveMac(pid);
        }
        catch { /* fall through */ }

        // Generic fallback.
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Walk up this process's ancestry and return the pid of the nearest ancestor whose executable
    /// name (or, on Linux, command line) contains <paramref name="nameHint"/> (case-insensitive) —
    /// i.e. the agent process that spawned the hook. Returns 0 if none is found. Because the hook is
    /// a descendant of the agent, the nearest match up the chain is the agent itself; unrelated
    /// processes that merely share the name are never in this chain.
    /// </summary>
    public static int FindAgentPid(string nameHint)
    {
        if (string.IsNullOrWhiteSpace(nameHint)) nameHint = "claude";
        int start = Environment.ProcessId;
        try
        {
            if (OperatingSystem.IsWindows()) return WalkWindows(start, nameHint);
            if (OperatingSystem.IsLinux()) return WalkLinux(start, nameHint);
            if (OperatingSystem.IsMacOS()) return WalkMac(start, nameHint);
        }
        catch { /* never let PID capture break the writer */ }
        return 0;
    }

    // ----------------------------------------------------------------- Windows

    private const int TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(int dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    private static bool IsAliveWindows(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == STILL_ACTIVE; }
        finally { CloseHandle(h); }
    }

    private static int WalkWindows(int startPid, string nameHint)
    {
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var parent = new Dictionary<int, int>();
            var name = new Dictionary<int, string>();
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do
                {
                    parent[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                    name[(int)e.th32ProcessID] = e.szExeFile ?? "";
                } while (Process32NextW(snap, ref e));
            }

            int pid = startPid;
            for (int hops = 0; hops < MaxHops && pid > 0; hops++)
            {
                if (name.TryGetValue(pid, out string? exe) &&
                    exe.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                    return pid;
                if (!parent.TryGetValue(pid, out int pp) || pp == pid) break;
                pid = pp;
            }
        }
        finally { CloseHandle(snap); }
        return 0;
    }

    // ------------------------------------------------------------------- Linux

    private static bool IsAliveLinux(int pid)
    {
        // A live, non-zombie process has a /proc/<pid> directory.
        if (!Directory.Exists($"/proc/{pid}")) return false;
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int rp = stat.LastIndexOf(')');
            if (rp >= 0 && rp + 2 < stat.Length && stat[rp + 2] == 'Z') return false; // zombie
        }
        catch { /* if we can't read state, the directory's existence is good enough */ }
        return true;
    }

    private static int WalkLinux(int startPid, string nameHint)
    {
        int pid = startPid;
        for (int hops = 0; hops < MaxHops && pid > 1; hops++)
        {
            if (LinuxNameContains(pid, nameHint)) return pid;
            int ppid = LinuxPpid(pid);
            if (ppid <= 0 || ppid == pid) break;
            pid = ppid;
        }
        return 0;
    }

    private static bool LinuxNameContains(int pid, string hint)
    {
        try
        {
            string comm = File.ReadAllText($"/proc/{pid}/comm").Trim();
            if (comm.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        try
        {
            // The agent may be e.g. 'node' running a 'claude' CLI script — check the full cmdline.
            string cmd = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (cmd.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private static int LinuxPpid(int pid)
    {
        // /proc/<pid>/stat: "pid (comm) state ppid ...". comm may contain spaces/parens, so parse
        // everything after the last ')'.
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int rp = stat.LastIndexOf(')');
            if (rp < 0) return -1;
            string[] parts = stat[(rp + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // parts[0] = state, parts[1] = ppid
            return parts.Length >= 2 && int.TryParse(parts[1], out int ppid) ? ppid : -1;
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------- macOS

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int sig);
    private const int EPERM = 1;

    private static bool IsAliveMac(int pid)
    {
        // kill(pid, 0) probes existence without sending a signal: 0 = alive; EPERM = alive but owned
        // by another user; ESRCH = gone.
        if (Kill(pid, 0) == 0) return true;
        return Marshal.GetLastPInvokeError() == EPERM;
    }

    private static int WalkMac(int startPid, string nameHint)
    {
        var parent = new Dictionary<int, int>();
        var name = new Dictionary<int, string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ps", "-ax -o pid=,ppid=,comm=")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return 0;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);

            foreach (string line in output.Split('\n'))
            {
                string t = line.TrimStart();
                if (t.Length == 0) continue;
                int s1 = t.IndexOf(' ');
                if (s1 < 0 || !int.TryParse(t[..s1], out int pid)) continue;
                string rest = t[s1..].TrimStart();
                int s2 = rest.IndexOf(' ');
                if (s2 < 0 || !int.TryParse(rest[..s2], out int ppid)) continue;
                parent[pid] = ppid;
                name[pid] = rest[s2..].Trim();
            }
        }
        catch { return 0; }

        int cur = startPid;
        for (int hops = 0; hops < MaxHops && cur > 1; hops++)
        {
            if (name.TryGetValue(cur, out string? c) &&
                c.Contains(nameHint, StringComparison.OrdinalIgnoreCase))
                return cur;
            if (!parent.TryGetValue(cur, out int pp) || pp == cur) break;
            cur = pp;
        }
        return 0;
    }
}
