using AgentSignal.App.Views;
using AgentSignal.Core;
using Avalonia.Threading;

namespace AgentSignal.App.Services;

/// <summary>Plays a short alert sound. Per-OS behind this seam; Windows is implemented.</summary>
public interface ISoundPlayer
{
    void PlayAlert();
}

/// <summary>Shows a desktop notification. Per-OS behind this seam; current impl is a portable toast.</summary>
public interface INotifier
{
    void Notify(string title, string message);
}

/// <summary>
/// Fires the red/green alerts (sound + notification) honouring the config toggles. Created once and
/// handed to the widget VM, which calls <see cref="OnRed"/>/<see cref="OnGreen"/> on aggregate edges.
/// </summary>
public sealed class AlertService
{
    private readonly ISoundPlayer _sound;
    private readonly INotifier _notifier;

    public AlertService(ISoundPlayer sound, INotifier notifier)
    {
        _sound = sound;
        _notifier = notifier;
    }

    /// <param name="source">Optional session key, recorded in the debug trace so per-session red
    /// alerts (and their debounce) are attributable; it isn't shown in the notification.</param>
    public void OnRed(string? source = null)
    {
        if (!ConfigService.Instance.Current.AlertOnRed) return;
        Fire("Agent needs permission", source);
    }

    public void OnGreen(string? source = null)
    {
        if (!ConfigService.Instance.Current.AlertOnGreen) return;
        Fire("Run finished", source);
    }

    /// <summary>Fire both channels regardless of toggles — used by the settings "Test" button.</summary>
    public void Test() => Fire("Test alert", null);

    private void Fire(string message, string? source)
    {
        _sound.PlayAlert();
        _notifier.Notify("AgentSignal", message);
        DebugLog($"alert dispatched: \"{message}\"{(source is null ? "" : $" [{source}]")} (sound + notification)");
    }

    /// <summary>
    /// Opt-in trace of the alert path to ~/.agentsignal/alerts.log, gated by AGENTSIGNAL_DEBUG=1 (the
    /// same switch the writer uses). Off by default so it never touches disk in normal use; when on it
    /// lets a live red/green be confirmed without an audio/screen capture. Also called by the toast on
    /// open, so the log shows the notification actually surfaced, not merely that it was requested.
    /// </summary>
    internal static void DebugLog(string line)
    {
        if (Environment.GetEnvironmentVariable("AGENTSIGNAL_DEBUG") != "1") return;
        try
        {
            AgentPaths.EnsureRoot();
            File.AppendAllText(Path.Combine(AgentPaths.Root, "alerts.log"),
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { /* diagnostics must never affect the app */ }
    }
}

/// <summary>Windows: a short two-tone beep on a background thread so it never blocks the UI poll.</summary>
public sealed class SystemSoundPlayer : ISoundPlayer
{
    public void PlayAlert() => Task.Run(() =>
    {
        if (!OperatingSystem.IsWindows()) return; // macOS/Linux (afplay/paplay) is a later seam
        try { Console.Beep(880, 150); Console.Beep(660, 150); }
        catch { /* no audio device / not supported */ }
    });
}

/// <summary>Shows a small auto-dismissing toast window (portable; swappable for native toasts later).</summary>
public sealed class ToastNotifier : INotifier
{
    public void Notify(string title, string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Show(title, message);
        else
            Dispatcher.UIThread.Post(() => Show(title, message));
    }

    private static void Show(string title, string message) => new ToastWindow(title, message).Show();
}
