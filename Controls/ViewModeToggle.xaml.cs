using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MultiAI.Controls;

/// <summary>
/// Pill-shaped Tiles/Tabs switch with a sliding thumb, like a segmented toggle.
/// Raises <see cref="ViewModeChanged"/> with true when "Tabs" is selected.
/// </summary>
public partial class ViewModeToggle : UserControl
{
    private bool _isTabs;

    /// <summary>Raised when the user switches the view mode. true = Tabs, false = Tiles.</summary>
    public event Action<bool>? ViewModeChanged;

    public bool IsTabs => _isTabs;

    private static readonly Brush SelectedTextBrush = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly Brush UnselectedTextBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

    // The inner grid is 144 wide (152 control - 2*1 border - 2*3 padding), so the
    // 72-wide thumb must slide 72 to cover the right half.
    private const double ThumbSlideDistance = 72;

    public ViewModeToggle()
    {
        InitializeComponent();
    }

    private void TilesSide_Click(object sender, MouseButtonEventArgs e)
    {
        // Mark handled so the click doesn't bubble to the toolbar's DragMove.
        e.Handled = true;
        SetMode(false, animate: true);
    }

    private void TabsSide_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetMode(true, animate: true);
    }

    private void SetMode(bool tabs, bool animate)
    {
        if (_isTabs == tabs) return;
        _isTabs = tabs;

        var slide = new DoubleAnimation(tabs ? ThumbSlideDistance : 0,
            TimeSpan.FromMilliseconds(animate ? 160 : 0))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ThumbTransform.BeginAnimation(TranslateTransform.XProperty, slide);

        // Dark text on the teal thumb, gray text on the unselected side.
        TilesLabel.Foreground = tabs ? UnselectedTextBrush : SelectedTextBrush;
        TabsLabel.Foreground = tabs ? SelectedTextBrush : UnselectedTextBrush;

        ViewModeChanged?.Invoke(tabs);
    }
}
