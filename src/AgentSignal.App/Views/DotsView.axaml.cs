using Avalonia.Controls;

namespace AgentSignal.App.Views;

/// <summary>The dots-only cell (three signal dots). Bound either to the aggregate VM or one session row VM;
/// the timer is rendered separately by <see cref="PillView"/>.</summary>
public partial class DotsView : UserControl
{
    public DotsView() => InitializeComponent();
}
