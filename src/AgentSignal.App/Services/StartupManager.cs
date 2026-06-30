using System.Diagnostics;

namespace AgentSignal.App.Services;

/// <summary>Launch-on-startup, one interface with a per-OS implementation (CLAUDE.md §10/§13).</summary>
public interface IStartupManager
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

public static class StartupManager
{
    /// <summary>The implementation for the current OS. Windows is implemented; others are no-ops for now.</summary>
    public static IStartupManager Current { get; } =
        OperatingSystem.IsWindows() ? new WindowsStartupManager() : new NoopStartupManager();
}

/// <summary>Non-Windows placeholder: Linux (~/.config/autostart) and macOS (LaunchAgent) come later.</summary>
internal sealed class NoopStartupManager : IStartupManager
{
    public bool IsEnabled() => false;
    public void SetEnabled(bool enabled) { }
}

/// <summary>
/// Windows: a HKCU\…\Run value via reg.exe (no extra package, no admin). The launch command runs the
/// app through the user-local dotnet host so it works even though .NET isn't on PATH and the app is
/// framework-dependent; a self-contained build (Phase 7) can register its own exe instead.
/// </summary>
internal sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentSignal";

    public bool IsEnabled() => Reg("query", $"\"{RunKey}\" /v {ValueName}") == 0;

    public void SetEnabled(bool enabled)
    {
        if (enabled)
            Reg("add", $"\"{RunKey}\" /v {ValueName} /t REG_SZ /d \"{LaunchCommand()}\" /f");
        else
            Reg("delete", $"\"{RunKey}\" /v {ValueName} /f");
    }

    private static string LaunchCommand()
    {
        // Prefer launching <dll> through the dotnet host so no DOTNET_ROOT is required at logon.
        string dll = Path.Combine(AppContext.BaseDirectory, "AgentSignal.App.dll");
        string host = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet.exe");
        if (File.Exists(host) && File.Exists(dll))
            return $"\\\"{host}\\\" \\\"{dll}\\\"";

        // Fallback: the apphost exe (needs the runtime resolvable, e.g. a self-contained build).
        string exe = Path.Combine(AppContext.BaseDirectory, "AgentSignal.App.exe");
        return $"\\\"{exe}\\\"";
    }

    private static int Reg(string verb, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe", $"{verb} {args}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
