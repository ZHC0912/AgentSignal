using AgentSignal.App.Models;
using Avalonia;
using Avalonia.Media;

namespace AgentSignal.App.Services;

/// <summary>
/// Publishes the per-state dot colours into the <see cref="Application"/> resource dictionary as
/// brushes and matching glow shadows. The dot styles reference these via <c>DynamicResource</c>, so
/// updating them here recolours the live widget instantly. Bad hex is ignored (keeps the last good
/// colour), which lets the settings text box be edited a character at a time without flicker/crash.
/// </summary>
public static class ThemeService
{
    public const string GreenBrush = "GreenBrush", YellowBrush = "YellowBrush", RedBrush = "RedBrush";
    public const string GreenGlow = "GreenGlow", YellowGlow = "YellowGlow", RedGlow = "RedGlow";

    public static void Apply(AppConfig cfg)
    {
        if (Application.Current is null) return;
        SetColor(GreenBrush, GreenGlow, cfg.GreenColor);
        SetColor(YellowBrush, YellowGlow, cfg.YellowColor);
        SetColor(RedBrush, RedGlow, cfg.RedColor);
    }

    private static void SetColor(string brushKey, string glowKey, string hex)
    {
        if (!TryParse(hex, out Color c)) return;

        var res = Application.Current!.Resources;
        res[brushKey] = new SolidColorBrush(c);
        // A soft glow in the same hue at ~50% alpha: "blurRadius spread offset offset color".
        var glow = new Color(0x80, c.R, c.G, c.B);
        res[glowKey] = new BoxShadows(new BoxShadow { Blur = 14, Spread = 1, Color = glow });
    }

    private static bool TryParse(string hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = Color.Parse(hex.Trim()); return true; }
        catch { return false; }
    }
}
