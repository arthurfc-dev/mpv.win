using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Player.App.Views;

public sealed partial class StartupView : UserControl
{
    public StartupView() => InitializeComponent();

    internal void SetLoading(bool loading)
    {
        LoadingIndicator.IsActive = loading;
        Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetAppearance(bool highContrast, bool effectsEnabled)
    {
        bool shadow = effectsEnabled && !highContrast;
        if (shadow)
        {
            var themeShadow = new ThemeShadow();
            themeShadow.Receivers.Add(ShadowReceiver);
            IconWell.Shadow = themeShadow;
        }
        else IconWell.Shadow = null;
        IconWell.Translation = shadow ? new Vector3(0, 0, 24) : Vector3.Zero;
    }
}
