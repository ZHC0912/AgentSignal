using System;
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
/// Scale/opacity/lock/orientation come from config and re-apply live. A clean (non-drag) click on the
/// dots reveals the settings gear. When the widget sits near a screen edge, the attached pills flip to
/// open inward, and the window is clamped fully on-screen.
/// </summary>
public partial class WidgetWindow : Window
{
    private const double DragThreshold = 4; // device px of movement before it counts as a drag
    private const double EdgeMargin = 56;    // DIPs from the far edge at which the pills flip inward

    private bool _pressed;
    private bool _dragging;
    private bool _locked;
    private PixelPoint _startWindowPos;
    private PixelPoint _startPointerScreen;

    private bool _positioned;
    private bool _lastVertical;

    public WidgetWindow()
    {
        InitializeComponent();
        _lastVertical = IsVerticalConfig();
        // Position + settle after the first layout pass so SizeToContent has produced real Bounds (in
        // OnOpened they can still be 0, which would misplace the pill or mis-compute the edge flip).
        Loaded += (_, _) => { ApplyStartupPosition(); SettleLayout(); };

        ApplyConfig();
        ConfigService.Instance.Changed += ApplyConfig;
    }

    private static bool IsVerticalConfig() =>
        string.Equals(ConfigService.Instance.Current.Orientation, "Vertical", StringComparison.OrdinalIgnoreCase);

    private void ApplyConfig()
    {
        var cfg = ConfigService.Instance.Current;
        _locked = cfg.LockPosition;
        // Scale the whole widget via a layout transform so SizeToContent grows/shrinks with it,
        // and fade the content (not the window) so opacity works regardless of the transparent host.
        ScaleHost.LayoutTransform = new ScaleTransform(cfg.Scale, cfg.Scale);
        ScaleHost.Opacity = cfg.Opacity;

        bool vertical = IsVerticalConfig();
        if (DataContext is WidgetViewModel vm)
            vm.IsVertical = vertical;

        if (vertical != _lastVertical)
        {
            _lastVertical = vertical;
            // Orientation changes the widget's whole size — re-settle (flip + clamp) once the new
            // layout pass has produced up-to-date Bounds.
            SettleAfterLayout();
        }
    }

    private void SettleAfterLayout()
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            LayoutUpdated -= handler;
            SettleLayout();
        };
        LayoutUpdated += handler;
        InvalidateMeasure();
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

    /// <summary>
    /// Decide the smart pill direction from where the widget sits, then clamp the whole window fully
    /// on-screen so nothing (dots or pills) can clip. Runs at startup, after a drag, and after an
    /// orientation change.
    /// </summary>
    private void SettleLayout()
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.Primary;
        if (screen is null) return;

        PixelRect wa = screen.WorkingArea;
        double scaling = screen.Scaling;
        int w = (int)Math.Ceiling(Bounds.Width * scaling);
        int h = (int)Math.Ceiling(Bounds.Height * scaling);
        if (w <= 0 || h <= 0) return;

        int margin = (int)(EdgeMargin * scaling);
        if (DataContext is WidgetViewModel vm)
        {
            // Evaluate before clamping so a widget dragged partly past the edge still resolves to the
            // correct inward direction. Vertical → flip when near the RIGHT edge; horizontal → when
            // near the BOTTOM edge (the far edge the pills would otherwise open toward).
            vm.AttachFlip = vm.IsVertical
                ? Position.X + w > wa.Right - margin
                : Position.Y + h > wa.Bottom - margin;
        }

        int x = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.X + wa.Width - w));
        int y = Math.Clamp(Position.Y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);

        SavePosition();
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
            SettleLayout();  // re-evaluate the smart flip + clamp + persist the new position
        else
            ToggleGear();    // a clean click on the dots reveals (or hides) the settings gear
    }

    private void ToggleGear()
    {
        if (DataContext is WidgetViewModel vm)
            vm.IsGearVisible = !vm.IsGearVisible;
    }

    private void SavePosition()
    {
        // Position is incidental state — persist it without raising Changed (no live re-apply needed).
        ConfigService.Instance.Current.PosX = Position.X;
        ConfigService.Instance.Current.PosY = Position.Y;
        ConfigService.Instance.Save();
    }
}
