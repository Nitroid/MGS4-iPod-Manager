using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace iPodManager
{
    public partial class MainWindow
    {
        private const double TrackTitleFadeWidth = 12;
        private const double TrackTitleScrollPixelsPerSecond = 18;

        private void TrackTitleViewport_Loaded(object sender, RoutedEventArgs e)
        {
            QueueTrackTitleMarqueeUpdate((Grid)sender);
        }

        private void TrackTitleViewport_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            QueueTrackTitleMarqueeUpdate((Grid)sender);
        }

        private void TrackTitleViewport_Unloaded(object sender, RoutedEventArgs e)
        {
            StopTrackTitleMarquee((Grid)sender);
        }

        private static void QueueTrackTitleMarqueeUpdate(Grid viewport)
        {
            viewport.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => UpdateTrackTitleMarquee(viewport)));
        }

        private static void UpdateTrackTitleMarquee(Grid viewport)
        {
            if (!viewport.IsLoaded || viewport.ActualWidth <= 0)
                return;

            if (viewport.Children.Count == 0 ||
                !(viewport.Children[0] is Canvas canvas) ||
                canvas.Children.Count == 0 ||
                !(canvas.Children[0] is Grid textHost))
            {
                return;
            }

            textHost.Measure(new Size(double.PositiveInfinity, viewport.ActualHeight));
            double textWidth = textHost.DesiredSize.Width;
            double overflow = textWidth - viewport.ActualWidth;

            textHost.BeginAnimation(Canvas.LeftProperty, null);
            Canvas.SetLeft(textHost, 3);

            if (overflow <= 0.5)
            {
                viewport.OpacityMask = null;
                return;
            }

            bool isHoveredFilename = viewport.Name == "HoveredFilenameViewport";
            double leftFadeInset = isHoveredFilename ? 4 : 0;
            double rightFadeInset = isHoveredFilename ? 12 : 0;
            double fadeStart = Math.Max(
                0,
                (viewport.ActualWidth - TrackTitleFadeWidth - rightFadeInset) /
                viewport.ActualWidth);
            double fadeEnd = Math.Min(
                1,
                (TrackTitleFadeWidth + leftFadeInset) / viewport.ActualWidth);

            var leftFadeStop = new GradientStop(
                Colors.White,
                leftFadeInset / viewport.ActualWidth);
            var rightFadeStop = new GradientStop(
                Colors.White,
                (viewport.ActualWidth - rightFadeInset) / viewport.ActualWidth);
            viewport.OpacityMask = new LinearGradientBrush(
                new GradientStopCollection
                {
                    leftFadeStop,
                    new GradientStop(Colors.White, fadeEnd),
                    new GradientStop(Colors.White, fadeStart),
                    rightFadeStop
                },
                new Point(0, 0.5),
                new Point(1, 0.5));

            // Scroll far enough that the final character clears the faded edge.
            double distance = overflow + TrackTitleFadeWidth;
            double scrollPixelsPerSecond = viewport.Name == "TooltipTextViewport"
                ? TrackTitleScrollPixelsPerSecond * 2
                : TrackTitleScrollPixelsPerSecond;
            double scrollSeconds = Math.Max(
                2,
                distance / scrollPixelsPerSecond);
            double pauseSeconds = viewport.Name == "TooltipTextViewport" ? 2 : 3;
            TimeSpan initialPause = TimeSpan.FromSeconds(pauseSeconds);
            TimeSpan scrollEnd = initialPause + TimeSpan.FromSeconds(scrollSeconds);
            TimeSpan finalPauseEnd = scrollEnd + TimeSpan.FromSeconds(pauseSeconds);
            TimeSpan resetTime = finalPauseEnd + TimeSpan.FromMilliseconds(1);

            var animation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(3, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(3, KeyTime.FromTimeSpan(initialPause)));
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(3 - distance, KeyTime.FromTimeSpan(scrollEnd)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(3 - distance, KeyTime.FromTimeSpan(finalPauseEnd)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(3, KeyTime.FromTimeSpan(resetTime)));

            var leftFadeAnimation = new ColorAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            leftFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.White,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
            leftFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(initialPause)));
            leftFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(scrollEnd)));
            leftFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(finalPauseEnd)));
            leftFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.White,
                KeyTime.FromTimeSpan(resetTime)));

            var rightFadeAnimation = new ColorAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            rightFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
            rightFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(initialPause)));
            rightFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.White,
                KeyTime.FromTimeSpan(scrollEnd)));
            rightFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.White,
                KeyTime.FromTimeSpan(finalPauseEnd)));
            rightFadeAnimation.KeyFrames.Add(new DiscreteColorKeyFrame(
                Colors.Transparent,
                KeyTime.FromTimeSpan(resetTime)));

            textHost.BeginAnimation(
                Canvas.LeftProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
            leftFadeStop.BeginAnimation(
                GradientStop.ColorProperty,
                leftFadeAnimation,
                HandoffBehavior.SnapshotAndReplace);
            rightFadeStop.BeginAnimation(
                GradientStop.ColorProperty,
                rightFadeAnimation,
                HandoffBehavior.SnapshotAndReplace);
        }

        private static void StopTrackTitleMarquee(Grid viewport)
        {
            if (viewport.Children.Count > 0 &&
                viewport.Children[0] is Canvas canvas &&
                canvas.Children.Count > 0 &&
                canvas.Children[0] is Grid textHost)
            {
                textHost.BeginAnimation(Canvas.LeftProperty, null);
                Canvas.SetLeft(textHost, 3);
            }
        }
    }
}
