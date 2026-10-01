using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;
using MyProxy.ViewModels;
namespace MyProxy.Views;

public partial class MainView : System.Windows.Controls.UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    private void OnStatusTargetUpdated(object sender, DataTransferEventArgs e)
    {
        var status = (FrameworkElement)sender;
        status.BeginAnimation(OpacityProperty, null);
        if (!IsVisible || DataContext is not MainViewModel { CanAnimate: true }) return;

        var fade = new DoubleAnimation(0d, 1d, (Duration)FindResource("Motion.Duration.Base"))
        {
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => status.BeginAnimation(OpacityProperty, null);
        status.BeginAnimation(OpacityProperty, fade);
    }
}
