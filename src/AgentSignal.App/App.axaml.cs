using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AgentSignal.App.Services;
using AgentSignal.App.ViewModels;
using AgentSignal.App.Views;

namespace AgentSignal.App;

public partial class App : Application
{
    private AlertService? _alerts;
    private Window? _settings;
    private WidgetViewModel? _widgetVm;
    private IGlobalHotkey? _hotkey;

    /// <summary>True while the settings window is open — the widget goes inert (modal settings).</summary>
    public bool IsSettingsOpen => _settings is not null;

    /// <summary>Raised with true/false as the settings window opens/closes (drives the widget's shield).</summary>
    public event Action<bool>? SettingsOpenChanged;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Publish the configured colours before any window renders, then keep them in sync with edits.
        ThemeService.Apply(ConfigService.Instance.Current);
        ConfigService.Instance.Changed += () => ThemeService.Apply(ConfigService.Instance.Current);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Startup sweep: delete every session file whose agent is gone BEFORE the widget first
            // renders, so ghosts left by previous boots never appear as pills (the 250ms poll keeps
            // sweeping after that).
            new SessionReader().SweepDead();

            _alerts = new AlertService(new SystemSoundPlayer(), new ToastNotifier());
            _widgetVm = new WidgetViewModel(_alerts);
            var widget = new WidgetWindow { DataContext = _widgetVm };
            desktop.MainWindow = widget;
            // Global Ctrl+Alt+R → manual reset. Registered once the window (and so its HWND) exists;
            // suspended while settings is open (ShowSettings) — the in-window Reset button covers that.
            widget.Opened += (_, _) =>
            {
                _hotkey ??= GlobalHotkey.Create(widget, ResetAllSessions);
                if (!IsSettingsOpen) _hotkey.Register();
            };
            // The widget is borderless with no close button; quitting is via the tray menu.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Open (or focus) the settings window. Called from the gear and the tray menu.</summary>
    public void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }
        // The widget VM is passed in so Settings → Session Tracking can list the live sessions and
        // drive their show/hide directly off the same models the pills use.
        _settings = new SettingsWindow { DataContext = new SettingsViewModel(_alerts, ResetAllSessions, _widgetVm) };
        _settings.Closed += (_, _) =>
        {
            _settings = null;
            _hotkey?.Register();          // Ctrl+Alt+R works again once settings is gone
            SettingsOpenChanged?.Invoke(false);
        };
        _hotkey?.Unregister();            // hotkey is disabled for as long as settings is open
        _settings.Show();
        SettingsOpenChanged?.Invoke(true);
    }

    /// <summary>Force all current sessions green (manual reset) — the Settings button and Ctrl+Alt+R.</summary>
    private void ResetAllSessions() => _widgetVm?.ResetAllSessions();

    // NOTE (2026-07-04): there is deliberately NO attention cue when the inert widget is clicked.
    // A flash/ding nag (FlashWindowEx + MessageBeep, three variants) was tried and did nothing on
    // the owner's machine, so it was removed — clicking the shielded widget is silently ignored
    // (confirmed live). The cue is PARKED: don't reattempt it unless the owner asks.

    private void TraySettingsClick(object? sender, EventArgs e) => ShowSettings();

    private void TrayQuitClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
