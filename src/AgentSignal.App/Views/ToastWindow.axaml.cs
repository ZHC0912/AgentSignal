using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentSignal.App.Views;

/// <summary>
/// A small, borderless, top-most notification that appears at the top-right and dismisses itself
/// after a few seconds. Portable (pure Avalonia) — the <see cref="Services.INotifier"/> seam lets a
/// native OS toast replace it later without touching callers.
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);

    public ToastWindow() => InitializeComponent();

    public ToastWindow(string title, string message) : this()
    {
        TitleText.Text = title;
        BodyText.Text = message;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        PlaceTopRight();
        Services.AlertService.DebugLog($"toast shown: {TitleText.Text} — {BodyText.Text}");

        var timer = new DispatcherTimer { Interval = Lifetime };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    private void PlaceTopRight()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null) return;

        PixelRect wa = screen.WorkingArea;
        const int margin = 16;
        int w = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        // Sit a little below the top edge so it doesn't collide with the widget itself.
        Position = new PixelPoint(wa.X + wa.Width - w - margin, wa.Y + margin + 70);
    }
}
