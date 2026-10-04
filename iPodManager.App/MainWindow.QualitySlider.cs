using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace iPodManager
{
    public partial class MainWindow
    {
        private const int MinimumQualityValue = 1;
        private const int MaximumQualityValue = 100;
        private const double QualityEndpointInsetSteps = 5;
        private int _qualityValue = 75;
        private bool _isQualityMarkerDragging;
        private bool _isQualityMarkerAnimating;
        private double _qualityDragPointerOffset;

        private void QualitySliderSurface_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            PositionQualityMarker(_qualityValue);
        }

        private void QualityMarker_PreviewMouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            QualityMarker.BeginAnimation(Canvas.LeftProperty, null);
            _isQualityMarkerAnimating = false;
            _isQualityMarkerDragging = true;
            _qualityDragPointerOffset =
                e.GetPosition(QualityMarker).X - QualityMarker.ActualWidth / 2;
            QualitySliderSurface.CaptureMouse();
            e.Handled = true;
        }

        private void QualityGuide_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement guide &&
                int.TryParse(guide.Tag?.ToString(), out int value))
            {
                AnimateQualityMarkerTo(value);
                e.Handled = true;
            }
        }

        private void AnimateQualityMarkerTo(int value)
        {
            double width = QualitySliderSurface.ActualWidth;
            if (width <= 0)
                return;

            int targetValue = Math.Clamp(value, MinimumQualityValue, MaximumQualityValue);
            double targetLeft = GetQualityLinePosition(width, targetValue) -
                QualityMarker.ActualWidth / 2;
            double currentLeft = Canvas.GetLeft(QualityMarker);
            if (double.IsNaN(currentLeft))
                currentLeft = targetLeft;

            int valueDistance = Math.Abs(targetValue - _qualityValue);
            double animationMilliseconds = 320 +
                Math.Max(0, valueDistance - 50) * 5;
            var animation = new DoubleAnimation(currentLeft, targetLeft,
                TimeSpan.FromMilliseconds(animationMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };

            animation.CurrentTimeInvalidated += (_, _) =>
            {
                double animatedLeft = QualityMarker.GetAnimationBaseValue(Canvas.LeftProperty)
                    is double baseLeft
                    ? baseLeft
                    : Canvas.GetLeft(QualityMarker);
                double currentAnimatedLeft = Canvas.GetLeft(QualityMarker);
                if (!double.IsNaN(currentAnimatedLeft))
                    animatedLeft = currentAnimatedLeft;

                UpdateQualityValueFromPosition(
                    animatedLeft + QualityMarker.ActualWidth / 2);
            };
            animation.Completed += (_, _) =>
            {
                QualityMarker.BeginAnimation(Canvas.LeftProperty, null);
                SetQualityValue(targetValue);
                PositionQualityMarker(targetValue);
                _isQualityMarkerAnimating = false;
                SavePreferences();
                PlayMenuSelectSound();
            };

            Canvas.SetLeft(QualityMarker, targetLeft);
            _isQualityMarkerAnimating = true;
            QualityMarker.BeginAnimation(
                Canvas.LeftProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private void QualitySliderSurface_PreviewMouseMove(
            object sender,
            MouseEventArgs e)
        {
            if (!_isQualityMarkerDragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            double linePosition =
                e.GetPosition(QualitySliderSurface).X - _qualityDragPointerOffset;
            SetQualityFromPosition(linePosition);
            e.Handled = true;
        }

        private void QualitySliderSurface_PreviewMouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            if (!_isQualityMarkerDragging || e.ChangedButton != MouseButton.Left)
                return;

            SetQualityFromPosition(
                e.GetPosition(QualitySliderSurface).X - _qualityDragPointerOffset);
            EndQualityMarkerDrag();
            SavePreferences();
            e.Handled = true;
        }

        private void QualitySliderSurface_LostMouseCapture(
            object sender,
            MouseEventArgs e)
        {
            _isQualityMarkerDragging = false;
        }

        private void SetQualityFromPosition(double linePosition)
        {
            double width = QualitySliderSurface.ActualWidth;
            if (width <= 0)
                return;

            double endpointInset = width * QualityEndpointInsetSteps /
                (MaximumQualityValue - MinimumQualityValue);
            double travelStart = endpointInset;
            double travelEnd = width - endpointInset;
            double clampedPosition = Math.Clamp(linePosition, travelStart, travelEnd);
            double normalizedPosition =
                (clampedPosition - travelStart) / (travelEnd - travelStart);
            int value = (int)Math.Round(
                MinimumQualityValue +
                normalizedPosition * (MaximumQualityValue - MinimumQualityValue),
                MidpointRounding.AwayFromZero);

            SetQualityValue(Math.Clamp(value, MinimumQualityValue, MaximumQualityValue));
            PositionQualityMarker(_qualityValue);
        }

        private void UpdateQualityValueFromPosition(double linePosition)
        {
            double width = QualitySliderSurface.ActualWidth;
            if (width <= 0)
                return;

            double endpointInset = width * QualityEndpointInsetSteps /
                (MaximumQualityValue - MinimumQualityValue);
            double normalizedPosition = Math.Clamp(
                (linePosition - endpointInset) / (width - 2 * endpointInset),
                0,
                1);
            int value = (int)Math.Round(
                MinimumQualityValue +
                normalizedPosition * (MaximumQualityValue - MinimumQualityValue),
                MidpointRounding.AwayFromZero);
            SetQualityValue(value);
        }

        private void SetQualityValue(int value)
        {
            int clampedValue = Math.Clamp(value, MinimumQualityValue, MaximumQualityValue);
            if (clampedValue == _qualityValue)
                return;

            _qualityValue = clampedValue;
            if (!_isQualityMarkerAnimating)
                PlayQualityClickSound();
            UpdateQualityValueText();
        }

        private void PositionQualityMarker(int value)
        {
            if (QualitySliderSurface == null || QualityMarker == null)
                return;

            double width = QualitySliderSurface.ActualWidth;
            if (width <= 0)
                return;

            double linePosition = GetQualityLinePosition(width, value);
            Canvas.SetLeft(
                QualityMarker,
                linePosition - QualityMarker.ActualWidth / 2);
            PositionQualityGuideLines(width);
            UpdateQualityValueText();
        }

        private static double GetQualityLinePosition(double width, int value)
        {
            double normalizedPosition =
                (value - MinimumQualityValue) /
                (double)(MaximumQualityValue - MinimumQualityValue);
            double endpointInset = width * QualityEndpointInsetSteps /
                (MaximumQualityValue - MinimumQualityValue);
            return endpointInset +
                normalizedPosition * (width - 2 * endpointInset);
        }

        private void PositionQualityGuideLines(double width)
        {
            PositionQualityGuide(QualityGuide1, width, 1);
            PositionQualityGuide(QualityGuide25, width, 25);
            PositionQualityGuide(QualityGuide50, width, 50);
            PositionQualityGuide(QualityGuide75, width, 75);
            PositionQualityGuide(QualityGuide100, width, 100);
        }

        private static void PositionQualityGuide(
            FrameworkElement guide,
            double width,
            int value)
        {
            Canvas.SetLeft(
                guide,
                GetQualityLinePosition(width, value) - guide.Width / 2);
        }

        private void UpdateQualityValueText()
        {
            string valueText = _qualityValue.ToString();
            QualityValueShadowText.Text = valueText;
            QualityValueText.Text = valueText;
            QualityTunedIndicator.Visibility = IsPresetQualityValue(_qualityValue)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private static bool IsPresetQualityValue(int value)
        {
            return value is 1 or 25 or 50 or 75 or 100;
        }

        private void EndQualityMarkerDrag()
        {
            _isQualityMarkerDragging = false;
            if (QualitySliderSurface.IsMouseCaptured)
                QualitySliderSurface.ReleaseMouseCapture();
        }
    }
}
