using System.Windows.Controls;

namespace WindowsDynamicIsland.UI.Views;

/// <summary>
/// Clipboard history inside the expanded island. DataContext: <see cref="IslandViewModel"/>.
/// Click a row → it is pasted into the app you are typing in (the island never takes keyboard focus).
/// </summary>
public partial class ClipboardView : UserControl
{
    public ClipboardView()
    {
        InitializeComponent();
    }
}
