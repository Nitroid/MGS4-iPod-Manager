using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace iPodManager;

public partial class MainWindow
{
    private bool _startupLoadingActive;
    private StartupProgress? _startupProgress;

    private void StartStartupLoadingState()
    {
        if (_startupLoadingActive)
            return;

        _startupLoadingActive = true;
        StartupLoadingPercentageText.Text = "0%";
        _startupProgress = new StartupProgress(UpdateStartupLoadingPercentage);
        CategoryPanelSoftBorder.Visibility = Visibility.Hidden;
        CategoryPanelCrispBorder.Visibility = Visibility.Hidden;
        CategoryPanelContent.Visibility = Visibility.Hidden;
        SettingsButton.Visibility = Visibility.Hidden;
        TrackPanelSoftBorder.Visibility = Visibility.Hidden;
        TrackPanelCrispBorder.Visibility = Visibility.Hidden;
        TrackListPanel.Visibility = Visibility.Hidden;
        StartupLoadingIndicator.Visibility = Visibility.Visible;
        StartupLoadingIconRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void StopStartupLoadingState()
    {
        if (!_startupLoadingActive)
            return;

        _startupLoadingActive = false;
        StartupLoadingIconRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        StartupLoadingIconRotation.Angle = 0;
        StartupLoadingIndicator.Visibility = Visibility.Collapsed;
        CategoryPanelSoftBorder.Visibility = Visibility.Visible;
        CategoryPanelCrispBorder.Visibility = Visibility.Visible;
        CategoryPanelContent.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Visible;
        TrackPanelSoftBorder.Visibility = Visibility.Visible;
        TrackPanelCrispBorder.Visibility = Visibility.Visible;
        TrackListPanel.Visibility = Visibility.Visible;
    }

    private void UpdateStartupLoadingPercentage(int value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateStartupLoadingPercentage(value));
            return;
        }

        if (_startupLoadingActive)
            StartupLoadingPercentageText.Text = $"{value}%";
    }
}
