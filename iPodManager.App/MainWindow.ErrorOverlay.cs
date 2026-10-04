using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace iPodManager
{
    public partial class MainWindow
    {
        private bool _errorOverlayClosing;
        private FrameworkElement? _errorOverlayAnimationOrigin;
        private bool _errorOverlayUsesBuzzer;
        private bool _shutdownAfterErrorDismissal;
        private Color _errorHighlightColor = Color.FromRgb(0xFF, 0x8C, 0x8C);

        internal void ShowError(string message)
        {
            ErrorMessageShadowText.Text = message;
            ErrorMessageText.Text = message;
            ErrorMessageShadowTranslation.Y = -7;
            ErrorMessageTranslation.Y = -9;
            ErrorIcon.Width = 128;
            ErrorIcon.Height = 128;
            ErrorIcon.Fill = new SolidColorBrush(Color.FromRgb(0xFC, 0x67, 0x47));
            _errorHighlightColor = Color.FromRgb(0xFF, 0x8C, 0x8C);
            SetErrorIcon("icon_exclamation_point.png");
            _errorOverlayUsesBuzzer = false;
            OpenErrorOverlayAnimated(null);
        }

        internal void ShowFatalError(string message)
        {
            _shutdownAfterErrorDismissal = true;
            ShowError(message);
        }

        private void ShowNoTracksSelected()
        {
            const string message =
                "No tracks selected. Please select at least one track and try again.";
            ErrorMessageShadowText.Text = message;
            ErrorMessageText.Text = message;
            ErrorMessageShadowTranslation.Y = 2;
            ErrorMessageTranslation.Y = 0;
            ErrorIcon.Width = 64;
            ErrorIcon.Height = 64;
            ErrorIcon.Fill = new SolidColorBrush(Color.FromRgb(0x62, 0xCE, 0xE6));
            _errorHighlightColor = Color.FromRgb(0x62, 0xCE, 0xE6);
            SetErrorIcon("icon_question_mark.png");
            _errorOverlayUsesBuzzer = true;
            OpenErrorOverlayAnimated(TransferButton);
        }

        private void SetErrorIcon(string fileName)
        {
            ErrorIconMaskBrush.ImageSource = new BitmapImage(
                new Uri($"pack://application:,,,/Assets/Images/{fileName}", UriKind.Absolute));
        }

        private void OpenErrorOverlayAnimated(FrameworkElement? animationOrigin)
        {
            if (ErrorOverlay.Visibility == Visibility.Visible)
            {
                if (_errorOverlayClosing)
                {
                    _errorOverlayClosing = false;
                    ClearErrorOverlayAnimations();
                    SetErrorOverlayRestingVisuals();
                    StartErrorBorderFlash();
                    ErrorInputShield.Focus();
                }
                return;
            }

            if (_errorOverlayUsesBuzzer)
                PlayBuzzerSound();
            else
                PlayAlertSound();
            Duration duration = new(TimeSpan.FromMilliseconds(175));
            var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

            const double finalWidth = 384;
            const double finalHeight = 248;
            double finalLeft = (MainLayoutRoot.ActualWidth - finalWidth) / 2;
            double finalTop = (MainLayoutRoot.ActualHeight - finalHeight) / 2;
            double originWidth = animationOrigin?.ActualWidth ?? 24;
            double originHeight = animationOrigin?.ActualHeight ?? 24;
            double startScaleX = originWidth / finalWidth;
            double startScaleY = originHeight / finalHeight;
            double startX = 0;
            double startY = 0;
            if (animationOrigin is not null)
            {
                Point originPosition = animationOrigin.TranslatePoint(new Point(), MainLayoutRoot);
                startX = originPosition.X + originWidth / 2 - (finalLeft + finalWidth / 2);
                startY = originPosition.Y + originHeight / 2 - (finalTop + finalHeight / 2);
            }

            ErrorOverlay.Margin = new Thickness(finalLeft, finalTop, 0, 0);
            _errorOverlayAnimationOrigin = animationOrigin;
            SetErrorOverlayRestingVisuals();
            ErrorOverlay.Visibility = Visibility.Visible;
            _errorOverlayClosing = false;

            DoubleAnimation scaleX = CreateSettingsTransformAnimation(startScaleX, 1, duration, easing);
            DoubleAnimation scaleY = CreateSettingsTransformAnimation(startScaleY, 1, duration, easing);
            DoubleAnimation translateX = CreateSettingsTransformAnimation(startX, 0, duration, easing);
            DoubleAnimation translateY = CreateSettingsTransformAnimation(startY, 0, duration, easing);
            ThicknessAnimation border = CreateTransferBorderAnimation(1, 2, duration, easing);
            ColorAnimation color = CreateTransferBorderColorAnimation(
                Color.FromArgb(0xCC, 0, 0, 0),
                Color.FromRgb(0x94, 0xA4, 0x8A), duration, easing);
            DoubleAnimation glow = CreateTransferGlowAnimation(0, 1, duration, easing);

            scaleX.Completed += (_, _) =>
            {
                if (ErrorOverlay.Visibility == Visibility.Visible)
                {
                    ErrorInputShield.Focus();
                    StartErrorBorderFlash();
                }
            };
            BeginErrorOverlayAnimations(scaleX, scaleY, translateX, translateY, border, color, glow);
        }

        private void ErrorInputShield_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseErrorOverlay_Click(object sender, RoutedEventArgs e)
        {
            CloseErrorOverlay();
            e.Handled = true;
        }

        private void CloseErrorOverlay()
        {
            if (ErrorOverlay.Visibility != Visibility.Visible || _errorOverlayClosing)
                return;

            _errorOverlayClosing = true;
            StopErrorBorderFlash();
            PlayMenuCloseSound();
            Duration duration = new(TimeSpan.FromMilliseconds(175));
            var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
            FrameworkElement? animationOrigin = _errorOverlayAnimationOrigin;
            double originWidth = animationOrigin?.ActualWidth ?? 24;
            double originHeight = animationOrigin?.ActualHeight ?? 24;
            double targetScaleX = originWidth / ErrorOverlay.Width;
            double targetScaleY = originHeight / ErrorOverlay.Height;
            double targetX = 0;
            double targetY = 0;
            if (animationOrigin is not null)
            {
                Point originPosition = animationOrigin.TranslatePoint(new Point(), MainLayoutRoot);
                targetX = originPosition.X + originWidth / 2 -
                    (ErrorOverlay.Margin.Left + ErrorOverlay.Width / 2);
                targetY = originPosition.Y + originHeight / 2 -
                    (ErrorOverlay.Margin.Top + ErrorOverlay.Height / 2);
            }

            DoubleAnimation scaleX = CreateSettingsTransformAnimation(1, targetScaleX, duration, easing);
            DoubleAnimation scaleY = CreateSettingsTransformAnimation(1, targetScaleY, duration, easing);
            DoubleAnimation translateX = CreateSettingsTransformAnimation(0, targetX, duration, easing);
            DoubleAnimation translateY = CreateSettingsTransformAnimation(0, targetY, duration, easing);
            ThicknessAnimation border = CreateTransferBorderAnimation(2, 1, duration, easing);
            ColorAnimation color = CreateTransferBorderColorAnimation(
                Color.FromRgb(0x94, 0xA4, 0x8A),
                Color.FromArgb(0xCC, 0, 0, 0), duration, easing);
            DoubleAnimation glow = CreateTransferGlowAnimation(1, 0, duration, easing);

            scaleX.Completed += (_, _) =>
            {
                if (!_errorOverlayClosing)
                    return;
                ErrorOverlay.Visibility = Visibility.Collapsed;
                Keyboard.ClearFocus();
                ClearErrorOverlayAnimations();
                SetErrorOverlayRestingVisuals();
                _errorOverlayAnimationOrigin = null;
                _errorOverlayUsesBuzzer = false;
                _errorOverlayClosing = false;
                if (_shutdownAfterErrorDismissal)
                    Application.Current.Shutdown(-1);
            };
            BeginErrorOverlayAnimations(scaleX, scaleY, translateX, translateY, border, color, glow);
        }

        private void SetErrorOverlayRestingVisuals()
        {
            ErrorOverlayScale.ScaleX = 1;
            ErrorOverlayScale.ScaleY = 1;
            ErrorOverlayTranslation.X = 0;
            ErrorOverlayTranslation.Y = 0;
            ErrorOverlayOuterBorder.BorderThickness = new Thickness(2);
            ErrorOverlayBorderBrush.Color = Color.FromRgb(0x94, 0xA4, 0x8A);
            ErrorOverlayGlowBorder.Opacity = 1;
        }

        private void StartErrorBorderFlash()
        {
            ErrorOverlayBorderBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                new ColorAnimation(
                    Color.FromRgb(0x94, 0xA4, 0x8A),
                    _errorHighlightColor,
                    TimeSpan.FromMilliseconds(500))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase
                    {
                        EasingMode = EasingMode.EaseInOut
                    }
                });
        }

        private void StopErrorBorderFlash()
        {
            ErrorOverlayBorderBrush.BeginAnimation(
                SolidColorBrush.ColorProperty,
                null);
            ErrorOverlayBorderBrush.Color = Color.FromRgb(0x94, 0xA4, 0x8A);
        }

        private void BeginErrorOverlayAnimations(
            DoubleAnimation scaleX, DoubleAnimation scaleY,
            DoubleAnimation translateX, DoubleAnimation translateY,
            ThicknessAnimation border, ColorAnimation color, DoubleAnimation glow)
        {
            ErrorOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            ErrorOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            ErrorOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, translateX);
            ErrorOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, translateY);
            ErrorOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, border);
            ErrorOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, color);
            ErrorOverlayGlowBorder.BeginAnimation(OpacityProperty, glow);
        }

        private void ClearErrorOverlayAnimations()
        {
            ErrorOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ErrorOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ErrorOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            ErrorOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            ErrorOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, null);
            ErrorOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            ErrorOverlayGlowBorder.BeginAnimation(OpacityProperty, null);
        }
    }
}
