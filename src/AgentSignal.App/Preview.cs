using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AgentSignal.App.ViewModels;
using AgentSignal.App.Views;

namespace AgentSignal.App;

/// <summary>
/// Renders a static preview PNG of the widget in all four states, off-screen via the headless Skia
/// backend (works without an interactive desktop). Invoked with: AgentSignal.App --screenshot &lt;path&gt;.
/// The content is hosted in a real headless Window so that styles (dot size/colour/glow, FluentTheme)
/// are applied exactly as they are in the live widget.
/// </summary>
internal static class Preview
{
    /// <summary>
    /// Focused render of just the orientation / smart-direction / collapsible-timer matrix, laid out in
    /// a grid so every case is fully visible. Invoked with: AgentSignal.App --layout &lt;path&gt;.
    /// </summary>
    public static int RenderLayouts(string outPath)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();
        Services.ThemeService.Apply(Services.ConfigService.Instance.Current);

        static WidgetViewModel Vm(bool vertical, bool flip, bool collapsed, bool gear)
        {
            var vm = new WidgetViewModel(live: false) { State = AggregateState.Yellow, TimerText = "1:10", IsGearVisible = gear };
            vm.IsVertical = vertical;
            vm.AttachFlip = flip;
            vm.IsTimerCollapsed = collapsed;
            return vm;
        }

        (WidgetViewModel vm, string label)[] cases =
        {
            (Vm(false, false, false, true),  "horizontal · gear revealed\ntimer below-left, gear below-right"),
            (Vm(false, true,  false, true),  "horizontal near BOTTOM\npills flip ABOVE the dots"),
            (Vm(false, false, true,  true),  "horizontal · timer collapsed\nchevron in timer slot, gear kept"),
            (Vm(true,  false, false, true),  "vertical · gear revealed\ndots column, pills to the RIGHT"),
            (Vm(true,  true,  false, true),  "vertical near RIGHT edge\npills flip to the LEFT"),
            (Vm(true,  false, true,  true),  "vertical · timer collapsed\nchevron beside the column"),
        };

        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(4) };
        foreach ((WidgetViewModel vm, string label) in cases)
        {
            var cell = new StackPanel { Spacing = 10, Margin = new Thickness(14), VerticalAlignment = VerticalAlignment.Top };
            cell.Children.Add(new Border
            {
                Height = 180,
                Background = new SolidColorBrush(Color.Parse("#12141A")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = new PillView { DataContext = vm, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            cell.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Opacity = 0.8, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(cell);
        }

        var content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "Orientation · smart pill direction · collapsible timer", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeight.SemiBold },
                grid,
            },
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Background = new SolidColorBrush(Color.Parse("#1B1D23")),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        if (frame is null) { Console.Error.WriteLine("layout preview: no frame captured"); return 1; }
        string full = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (FileStream fs = File.Create(full)) frame.Save(fs);
        Console.WriteLine($"saved layout preview {full} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        return 0;
    }

    public static int Render(string outPath)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        // OnFrameworkInitializationCompleted doesn't run for a static render, so seed the dot colours.
        Services.ThemeService.Apply(Services.ConfigService.Instance.Current);

        (AggregateState state, string timer, string label)[] items =
        {
            (AggregateState.Off,    "",     "off — no agent running"),
            (AggregateState.Green,  "2:34", "green — idle (last run's time, frozen)"),
            (AggregateState.Yellow, "1:10", "yellow — working (timer running)"),
            (AggregateState.Red,    "0:48", "red — waiting on a permission prompt (timer paused)"),
        };

        var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 16 };
        foreach ((AggregateState state, string timer, string label) in items)
        {
            var vm = new WidgetViewModel(live: false) { State = state, TimerText = timer };
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 18,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(new PillView { DataContext = vm, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                Opacity = 0.85,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            });
            rows.Children.Add(row);
        }

        // Expanded view: click the pill and it grows to one dots pill per live session (dots-only,
        // consistent with the collapsed pill); the gear pill is revealed and the timer pill shows the
        // driving session's time.
        var expandedVm = new WidgetViewModel(live: false) { IsExpanded = true, TimerText = "0:48" };
        expandedVm.Sessions.Add(new SessionRowViewModel("claude", "alpha") { State = AggregateState.Red, TimerText = "0:48" });
        expandedVm.Sessions.Add(new SessionRowViewModel("claude", "bravo") { State = AggregateState.Yellow, TimerText = "2:03" });
        var expandedRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        expandedRow.Children.Add(new PillView { DataContext = expandedVm, VerticalAlignment = VerticalAlignment.Center });
        expandedRow.Children.Add(new TextBlock
        {
            Text = "expanded — two sessions (per-session dots pills) + gear pill on click",
            Foreground = Brushes.White,
            Opacity = 0.85,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        });
        rows.Children.Add(expandedRow);

        // Max scale (3×): the whole widget scaled by the same layout transform the live window uses, to
        // confirm the dots/glow/timer stay crisp (vector-scaled) and each dot's glow is a round halo
        // (no square clip) at max scale.
        var scaledVm = new WidgetViewModel(live: false) { IsExpanded = true, TimerText = "0:48" };
        scaledVm.Sessions.Add(new SessionRowViewModel("claude", "alpha") { State = AggregateState.Green, TimerText = "0:48" });
        scaledVm.Sessions.Add(new SessionRowViewModel("claude", "bravo") { State = AggregateState.Red, TimerText = "2:03" });
        var scaledRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        scaledRow.Children.Add(new LayoutTransformControl
        {
            LayoutTransform = new ScaleTransform(3.0, 3.0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new PillView { DataContext = scaledVm },
        });
        scaledRow.Children.Add(new TextBlock
        {
            Text = "3× scale — round glow, timer pinned left + gear pinned right, aligned to pill edges",
            Foreground = Brushes.White,
            Opacity = 0.85,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        });
        rows.Children.Add(scaledRow);

        // Orientation + smart pill-direction + collapsible-timer matrix (this change set). Each pill is
        // configured directly (the live widget derives the same flags from config + screen position).
        static WidgetViewModel Layout(bool vertical, bool flip, bool collapsed)
        {
            var vm = new WidgetViewModel(live: false)
            {
                State = AggregateState.Yellow,
                TimerText = "1:10",
                IsGearVisible = true,
            };
            vm.IsVertical = vertical;
            vm.AttachFlip = flip;
            vm.IsTimerCollapsed = collapsed;
            return vm;
        }

        (WidgetViewModel vm, string label)[] layouts =
        {
            (Layout(false, false, false), "horizontal — timer below-left, gear below-right"),
            (Layout(false, true,  false), "horizontal near BOTTOM edge — pills flip ABOVE"),
            (Layout(false, false, true),  "horizontal — timer collapsed to a chevron (gear kept)"),
            (Layout(true,  false, false), "vertical — dots in a column, pills to the RIGHT"),
            (Layout(true,  true,  false), "vertical near RIGHT edge — pills flip LEFT"),
            (Layout(true,  false, true),  "vertical — timer collapsed to a chevron"),
        };
        rows.Children.Add(new TextBlock
        {
            Text = "Orientation · smart direction · collapsible timer",
            Foreground = Brushes.White, Opacity = 0.6, FontSize = 13, Margin = new Thickness(0, 8, 0, 0),
        });
        foreach ((WidgetViewModel vm, string label) in layouts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Border
            {
                Width = 120, // fixed cell so the differing widget shapes line their labels up
                Child = new PillView { DataContext = vm, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            row.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Opacity = 0.85, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            rows.Children.Add(row);
        }

        var content = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(28),
            Children =
            {
                new TextBlock
                {
                    Text = "AgentSignal widget",
                    Foreground = Brushes.White,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                },
                rows,
            },
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Background = new SolidColorBrush(Color.Parse("#1B1D23")),
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            Content = content,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();              // run layout/binding/style passes
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); // force a render
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.Error.WriteLine("preview: no frame captured");
            return 1;
        }

        string full = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (FileStream fs = File.Create(full))
            frame.Save(fs);

        Console.WriteLine($"saved preview {full} ({frame.PixelSize.Width}x{frame.PixelSize.Height})");
        return 0;
    }
}
