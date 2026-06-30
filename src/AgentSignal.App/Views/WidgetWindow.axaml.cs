using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AgentSignal.App.Services;
using AgentSignal.App.ViewModels;

namespace AgentSignal.App.Views;

/// <summary>
/// Frameless, translucent, always-on-top pill window. Drag to move (manual click-vs-drag via a
/// movement threshold so the pill still receives clicks); position is remembered across launches.
/// Scale/opacity/lock come from config and re-apply live when settings change.
/// </summary>
public partial class WidgetWindow : Window
{
    private const double DragThreshold = 4; // device px of movement before it counts as a drag

    private bool _pressed;
    private bool _dragging;
    private bool _locked;
    private PixelPoint _startWindowPos;
    private PixelPoint _startPointerScreen;

    private bool _positioned;

    public WidgetWindow()
    {
        InitializeComponent();
        // Position after the first layout pass so SizeToContent has produced real Bounds (in OnOpened
        // they can still be 0, which would shove the pill off the right edge).
        Loaded += (_, _) => ApplyStartupPosition();

        ApplyConfig();
        ConfigService.Instance.Changed += ApplyConfig;
    }

    private void ApplyConfig()
    {
        var cfg = ConfigService.Instance.Current;
        _locked = cfg.LockPosition;
        // Scale the whole widget via a layout transform so SizeToContent grows/shrinks with it,
        // and fade the content (not the window) so opacity works regardless of the transparent host.
        ScaleHost.LayoutTransform = new ScaleTransform(cfg.Scale, cfg.Scale);
        ScaleHost.Opacity = cfg.Opacity;
    }

    private void ApplyStartupPosition()
    {
        if (_positioned) return;
        _positioned = true;

        var cfg = ConfigService.Instance.Current;
        if (cfg.PosX is double x && cfg.PosY is double y)
            Position = new PixelPoint((int)x, (int)y);
        else
            MoveTopRight();
    }

    private void MoveTopRight()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null) return;

        PixelRect wa = screen.WorkingArea;
        const int margin = 20;
        double width = Bounds.Width > 0 ? Bounds.Width : 120;
        int pixelWidth = (int)Math.Ceiling(width * screen.Scaling);
        Position = new PixelPoint(wa.X + wa.Width - pixelWidth - margin, wa.Y + margin);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _pressed = true;
        _dragging = false;
        _startWindowPos = Position;
        _startPointerScreen = this.PointToScreen(e.GetPosition(this));
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_pressed || _locked) return; // lock position: a press still counts as a click, but never drags

        // PointToScreen of the current cursor is absolute regardless of how the window has moved,
        // so deltas stay correct even as we reposition the window mid-drag.
        PixelPoint cursor = this.PointToScreen(e.GetPosition(this));
        int dx = cursor.X - _startPointerScreen.X;
        int dy = cursor.Y - _startPointerScreen.Y;

        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) >= DragThreshold)
            _dragging = true;

        if (_dragging)
            Position = new PixelPoint(_startWindowPos.X + dx, _startWindowPos.Y + dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);

        if (_dragging)
            SavePosition();
        else
            ToggleExpand(); // a clean (non-drag) click toggles the expanded per-session view
    }

    private void ToggleExpand()
    {
        // Only meaningful when at least one session is live; an empty pill has nothing to expand.
        if (DataContext is WidgetViewModel vm && vm.SessionCount > 0)
            vm.IsExpanded = !vm.IsExpanded;
    }

    private void SavePosition()
    {
        // Position is incidental state — persist it without raising Changed (no live re-apply needed).
        ConfigService.Instance.Current.PosX = Position.X;
        ConfigService.Instance.Current.PosY = Position.Y;
        ConfigService.Instance.Save();
    }
}
