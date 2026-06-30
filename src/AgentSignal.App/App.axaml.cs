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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Publish the configured colours before any window renders, then keep them in sync with edits.
        ThemeService.Apply(ConfigService.Instance.Current);
        ConfigService.Instance.Changed += () => ThemeService.Apply(ConfigService.Instance.Current);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _alerts = new AlertService(new SystemSoundPlayer(), new ToastNotifier());
            desktop.MainWindow = new WidgetWindow { DataContext = new WidgetViewModel(_alerts) };
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
        _settings = new SettingsWindow { DataContext = new SettingsViewModel(_alerts) };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void TraySettingsClick(object? sender, EventArgs e) => ShowSettings();

    private void TrayQuitClick(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
