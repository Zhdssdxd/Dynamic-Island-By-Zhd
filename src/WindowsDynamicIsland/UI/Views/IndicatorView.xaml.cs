using System.Windows.Controls;

namespace WindowsDynamicIsland.UI.Views;

/// <summary>Island content view. DataContext: <see cref="IslandViewModel"/> (set explicitly by IslandWindow).</summary>
public partial class IndicatorView : UserControl
{
    public IndicatorView()
    {
        InitializeComponent();
    }
}
