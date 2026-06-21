using System.Windows;
using System.Windows.Media.Animation;

namespace DreamWin.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => StartAnimations();
    }

    private void StartAnimations()
    {
        // Gentle pulse on the logo dot
        var pulse = new DoubleAnimation
        {
            From = 1.0,
            To = 1.15,
            Duration = TimeSpan.FromSeconds(0.9),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pulse);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pulse);

        // Indeterminate progress bar: a short pill sliding left-to-right, looping
        var slide = new DoubleAnimation
        {
            From = -70,
            To = 280,
            Duration = TimeSpan.FromSeconds(1.3),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ProgressTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    /// <summary>Updates the status line. Safe to call from any thread.</summary>
    public void SetStatus(string text)
    {
        if (Dispatcher.CheckAccess())
            StatusText.Text = text;
        else
            Dispatcher.Invoke(() => StatusText.Text = text);
    }
}
