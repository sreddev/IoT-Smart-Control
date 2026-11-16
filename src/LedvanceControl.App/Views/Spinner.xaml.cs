using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LedvanceControl.App.Views;

/// <summary>
/// Loading circle. Spins only while visible (no hidden animations running), and turns more slowly when
/// Windows' "Animation effects" are off.
/// </summary>
public partial class Spinner : UserControl
{
    public Spinner()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateArc();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Loaded += (_, _) => { UpdateArc(); UpdateAnimation(); };
        Unloaded += (_, _) => Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>Dash pattern for a quarter arc, in units of the stroke thickness.</summary>
    private void UpdateArc()
    {
        var d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        var t = Arc.StrokeThickness;
        var circumference = Math.PI * (d - t) / t;
        Arc.StrokeDashArray = [circumference * 0.27, circumference];
    }

    private void UpdateAnimation()
    {
        if (!IsVisible)
        {
            Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        var seconds = SystemParameters.ClientAreaAnimation ? 0.8 : 2.4;
        Rotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
