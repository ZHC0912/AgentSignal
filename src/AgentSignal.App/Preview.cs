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
