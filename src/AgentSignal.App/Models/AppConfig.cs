namespace AgentSignal.App.Models;

/// <summary>
/// Persisted user settings at ~/.agentsignal/config.json. Defaults below are applied whenever the
/// file is missing or a field is absent, so older config files keep working as the schema grows.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Last widget position in device pixels (null = not yet placed → default top-right).</summary>
    public double? PosX { get; set; }
    public double? PosY { get; set; }

    // Per-state dot colours (hex #RRGGBB).
    public string GreenColor { get; set; } = "#22C55E";
    public string YellowColor { get; set; } = "#F59E0B";
    public string RedColor { get; set; } = "#EF4444";

    // Alerts.
    public bool AlertOnRed { get; set; } = true;   // sound + notification when a session needs permission
    public bool AlertOnGreen { get; set; }         // optional: when a run finishes

    // Appearance.
    public double Scale { get; set; } = 1.0;       // whole-widget scale (0.6–3.0)
    public double Opacity { get; set; } = 1.0;     // whole-widget translucency (0.2–1.0)

    // Visual feedback.
    public double BlinkOnGreenSeconds { get; set; } = 2.0; // pulse the green dot this long on entering green (0 = off, 0–5s)

    // Behaviour.
    public bool LockPosition { get; set; }         // when true, ignore drags
    public bool LaunchOnStartup { get; set; }      // start with the OS (Windows: Run key)
}
