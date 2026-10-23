using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Player.App.Views;

// Keep the native picker, keyboard behavior and accessible channel controls.
// A few backgrounds in the pinned WinUI template do not inherit its radius.
internal static class RoundedColorPicker
{
    internal static void Attach(ColorPicker picker)
    {
        picker.Loaded += (_, _) => RoundParts(picker);
    }

    private static void RoundParts(DependencyObject root)
    {
        if (root is Rectangle rectangle && rectangle.Name is "ThirdDimensionBackgroundRectangle" or "AlphaSliderBackgroundRectangle"
            or "AlphaSliderCheckeredBackgroundRectangle" or "ColorPreviewRectangle" or "PreviousColorRectangle" or "BorderRectangle")
            rectangle.RadiusX = rectangle.RadiusY = 6;
        if (root is Control input && input.Name is "RedTextBox" or "GreenTextBox" or "BlueTextBox" or "HueTextBox" or "SaturationTextBox"
            or "ValueTextBox" or "AlphaTextBox" or "HexTextBox" or "ColorRepresentationComboBox")
            input.CornerRadius = new CornerRadius(4);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) RoundParts(VisualTreeHelper.GetChild(root, index));
    }
}
