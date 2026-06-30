using AgentSignal.App.Models;
using AgentSignal.App.Services;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentSignal.App.ViewModels;

/// <summary>
/// Backs the settings window. Each property mirrors a field in <see cref="AppConfig"/>; changing one
/// mutates <see cref="ConfigService"/> (which persists + raises Changed → the live widget re-applies).
/// A loading guard suppresses writes while the initial values are pushed in.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ConfigService _cfg = ConfigService.Instance;
    private readonly AlertService? _alerts;
    private readonly bool _loading;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(GreenSwatch))] private string _greenColor = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(YellowSwatch))] private string _yellowColor = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(RedSwatch))] private string _redColor = "";

    [ObservableProperty] private bool _alertOnRed;
    [ObservableProperty] private bool _alertOnGreen;

    [ObservableProperty] private double _scale;
    [ObservableProperty] private double _opacity;

    [ObservableProperty] private bool _lockPosition;
    [ObservableProperty] private bool _launchOnStartup;

    public IBrush GreenSwatch => Swatch(GreenColor);
    public IBrush YellowSwatch => Swatch(YellowColor);
    public IBrush RedSwatch => Swatch(RedColor);

    public SettingsViewModel(AlertService? alerts = null)
    {
        _alerts = alerts;

        _loading = true;
        AppConfig c = _cfg.Current;
        GreenColor = c.GreenColor;
        YellowColor = c.YellowColor;
        RedColor = c.RedColor;
        AlertOnRed = c.AlertOnRed;
        AlertOnGreen = c.AlertOnGreen;
        Scale = c.Scale;
        Opacity = c.Opacity;
        LockPosition = c.LockPosition;
        LaunchOnStartup = StartupManager.Current.IsEnabled(); // reflect the real OS state
        _loading = false;
    }

    partial void OnGreenColorChanged(string value) => Push(c => c.GreenColor = value);
    partial void OnYellowColorChanged(string value) => Push(c => c.YellowColor = value);
    partial void OnRedColorChanged(string value) => Push(c => c.RedColor = value);
    partial void OnAlertOnRedChanged(bool value) => Push(c => c.AlertOnRed = value);
    partial void OnAlertOnGreenChanged(bool value) => Push(c => c.AlertOnGreen = value);
    partial void OnScaleChanged(double value) => Push(c => c.Scale = Math.Round(value, 2));
    partial void OnOpacityChanged(double value) => Push(c => c.Opacity = Math.Round(value, 2));
    partial void OnLockPositionChanged(bool value) => Push(c => c.LockPosition = value);

    partial void OnLaunchOnStartupChanged(bool value)
    {
        if (_loading) return;
        StartupManager.Current.SetEnabled(value);
        Push(c => c.LaunchOnStartup = value);
    }

    [RelayCommand]
    private void TestAlert() => _alerts?.Test();

    private void Push(Action<AppConfig> mutate)
    {
        if (_loading) return;
        _cfg.Update(mutate);
    }

    private static IBrush Swatch(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex.Trim())); }
        catch { return Brushes.Transparent; }
    }
}
