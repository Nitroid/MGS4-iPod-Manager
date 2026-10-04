using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace iPodManager
{
    public partial class MainWindow
    {
        private static readonly Key[] KonamiCode =
        {
            Key.Up,
            Key.Up,
            Key.Down,
            Key.Down,
            Key.Left,
            Key.Right,
            Key.Left,
            Key.Right,
            Key.B,
            Key.A
        };

        private int _konamiCodePosition;
        private bool _konamiVideoActive;
        private bool _restartKonamiAfterStop;
        private string? _currentKonamiVideoPath;

        private static readonly Thickness CategoryExpandedMargin = new(4, 4, 2, 53);
        private static readonly Thickness CategoryContractedMargin = new(4, 4, 2, 157);
        private static readonly Duration CategoryResizeDuration = new(TimeSpan.FromMilliseconds(450));

        /// <summary>
        /// Raised whenever the complete Konami Code is entered while this window is active.
        /// </summary>
        public event EventHandler? KonamiCodeEntered;

        private void InitializeKonamiCodeDetection()
        {
            InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
            Closed += (_, _) =>
                InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
        }

        private void InputManager_PreProcessInput(object? sender, PreProcessInputEventArgs e)
        {
            if (!IsActive ||
                SettingsOverlay.Visibility == Visibility.Visible ||
                e.StagingItem.Input is not KeyEventArgs keyEvent ||
                keyEvent.RoutedEvent != Keyboard.KeyDownEvent)
            {
                return;
            }

            // A held key should count as one input rather than filling the sequence via repeats.
            if (keyEvent.IsRepeat)
                return;

            Key pressedKey = keyEvent.Key == Key.System ? keyEvent.SystemKey : keyEvent.Key;
            if (ProcessKonamiCodeKey(pressedKey))
                OnKonamiCodeEntered();
        }

        /// <summary>
        /// Advances the detector and returns true when this key completes the Konami Code.
        /// </summary>
        private bool ProcessKonamiCodeKey(Key pressedKey)
        {
            if (pressedKey == KonamiCode[_konamiCodePosition])
            {
                _konamiCodePosition++;
                if (_konamiCodePosition == KonamiCode.Length)
                {
                    _konamiCodePosition = 0;
                    return true;
                }

                return false;
            }

            // Preserve a possible new start when the mismatched key is the first code key.
            _konamiCodePosition = pressedKey == KonamiCode[0] ? 1 : 0;
            return false;
        }

        private void OnKonamiCodeEntered()
        {
            PlayKonamiCodeSound();
            KonamiCodeEntered?.Invoke(this, EventArgs.Empty);
        }

        private void MainWindow_KonamiCodeEntered(object? sender, EventArgs e)
        {
            if (_konamiVideoActive)
            {
                if (KonamiVideoFrame.Visibility == Visibility.Visible)
                {
                    _restartKonamiAfterStop = true;
                    RemoveKonamiVideo();
                    RestoreCategoryPanel();
                }

                return;
            }

            StartKonamiSequence();
        }

        private void StartKonamiSequence()
        {
            _konamiVideoActive = true;
            AnimateCategoryMargin(CategoryExpandedMargin, CategoryContractedMargin, ShowKonamiVideo);
        }

        private void ShowKonamiVideo()
        {
            var availableVideoPaths = new System.Collections.Generic.List<string>();
            for (int videoNumber = 1; videoNumber <= 13; videoNumber++)
            {
                string resourcePath = $"Assets/Video/sunny_cam_{videoNumber:00}.mp4";
                string? materializedPath = MaterializeEmbeddedMedia(resourcePath);
                if (materializedPath is not null)
                    availableVideoPaths.Add(materializedPath);
            }

            string[] videoPaths = availableVideoPaths.ToArray();

            if (videoPaths.Length == 0)
            {
                RestoreCategoryPanel();
                return;
            }

            string[] alternatePaths = videoPaths.Length > 1 && _currentKonamiVideoPath is not null
                ? Array.FindAll(
                    videoPaths,
                    path => !string.Equals(path, _currentKonamiVideoPath, StringComparison.OrdinalIgnoreCase))
                : videoPaths;

            string videoPath = alternatePaths[Random.Shared.Next(alternatePaths.Length)];
            _currentKonamiVideoPath = videoPath;
            KonamiVideoFrame.Tag = $"sunny_song_{Random.Shared.Next(1, 5)}";
            KonamiVideo.Source = new Uri(videoPath, UriKind.Absolute);
            KonamiVideo.Position = TimeSpan.Zero;
            KonamiVideoFrame.Visibility = Visibility.Visible;
            KonamiVideo.Play();
        }

        private void KonamiVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            RemoveKonamiVideo();
            RestoreCategoryPanel();
        }

        private void KonamiVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            RemoveKonamiVideo();
            RestoreCategoryPanel();
        }

        private void RemoveKonamiVideo()
        {
            KonamiVideo.Stop();
            KonamiVideoFrame.Visibility = Visibility.Collapsed;
            KonamiVideo.Source = null;
        }

        private void KonamiVideoFrame_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Clip the MediaElement and its frame together; MediaElement has no CornerRadius property.
            KonamiVideoFrame.Clip = new RectangleGeometry(
                new Rect(0, 0, KonamiVideoFrame.ActualWidth, KonamiVideoFrame.ActualHeight),
                2,
                2);
        }

        private void RestoreCategoryPanel()
        {
            AnimateCategoryMargin(CategoryContractedMargin, CategoryExpandedMargin, () =>
            {
                _konamiVideoActive = false;

                if (_restartKonamiAfterStop)
                {
                    _restartKonamiAfterStop = false;
                    StartKonamiSequence();
                }
            });
        }

        private void AnimateCategoryMargin(Thickness from, Thickness to, Action completed)
        {
            var softAnimation = new ThicknessAnimation(from, to, CategoryResizeDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            var crispAnimation = softAnimation.Clone();
            crispAnimation.Completed += (_, _) => completed();

            CategoryPanelSoftBorder.BeginAnimation(MarginProperty, softAnimation);
            CategoryPanelCrispBorder.BeginAnimation(MarginProperty, crispAnimation);
        }
    }
}
