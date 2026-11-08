using System.Windows.Controls;

namespace WindowsDynamicIsland.UI.Views;

/// <summary>Island content view. DataContext: <see cref="IslandViewModel"/> (set explicitly by IslandWindow).</summary>
public partial class CompactTimerView : UserControl
{
    public CompactTimerView()
    {
        InitializeComponent();
    }
}
