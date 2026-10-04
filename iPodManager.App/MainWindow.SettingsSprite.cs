using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace iPodManager
{
    public partial class MainWindow
    {
        private const int OtaconSpriteFrameWidth = 48;
        private const int OtaconSpriteFrameHeight = 71;

        private static readonly (int Row, int Column)[] OtaconSettingsFrames =
        {
            (3, 9),
            (3, 10),
            (3, 11),
            (3, 12),
            (1, 1)
        };

        private static readonly (int Row, int Column)[] OtaconLaughFrames =
        {
            (4, 3),
            (4, 4),
            (4, 5)
        };

        private static readonly (int Row, int Column)[] OtaconThumbsUpFrames =
        {
            (2, 1),
            (2, 2),
            (2, 3),
            (2, 4)
        };

        private BitmapSource? _otaconSpriteSheet;
        private DispatcherTimer? _otaconSpriteStartTimer;
        private DispatcherTimer? _otaconPopupSoundTimer;
        private DispatcherTimer? _otaconSpriteTimer;
        private DispatcherTimer? _otaconLaughFallbackTimer;
        private DispatcherTimer? _otaconThumbsUpHoldTimer;
        private int _otaconSpriteFrameIndex;
        private bool _otaconLaughing;
        private bool _otaconThumbsUpAnimating;

        private void StartSettingsOtaconAnimation()
        {
            _otaconSpriteSheet ??= LoadOtaconSpriteSheet();
            if (_otaconSpriteSheet == null)
                return;

            StopSettingsOtaconAnimation(hide: true);

            _otaconSpriteStartTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(180),
                DispatcherPriority.Render,
                SettingsOtaconStartTimer_Tick,
                Dispatcher);
            _otaconSpriteStartTimer.Start();
        }

        private void SettingsOtaconStartTimer_Tick(object? sender, EventArgs e)
        {
            _otaconSpriteStartTimer?.Stop();
            _otaconSpriteStartTimer = null;

            if (SettingsOverlay.Visibility != Visibility.Visible || _settingsOverlayClosing)
                return;

            _otaconSpriteFrameIndex = 0;
            ShowSettingsOtaconFrame(OtaconSettingsFrames[0]);
            SettingsOtaconSprite.Visibility = Visibility.Visible;

            _otaconPopupSoundTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(80),
                DispatcherPriority.Background,
                SettingsOtaconPopupSoundTimer_Tick,
                Dispatcher);
            _otaconPopupSoundTimer.Start();

            _otaconSpriteTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(80),
                DispatcherPriority.Render,
                SettingsOtaconTimer_Tick,
                Dispatcher);
            _otaconSpriteTimer.Start();
        }

        private void SettingsOtaconPopupSoundTimer_Tick(object? sender, EventArgs e)
        {
            _otaconPopupSoundTimer?.Stop();
            _otaconPopupSoundTimer = null;

            if (SettingsOverlay.Visibility == Visibility.Visible && !_settingsOverlayClosing)
                PlayOtaconPopupSound();
        }

        private static BitmapSource? LoadOtaconSpriteSheet()
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(
                    "pack://application:,,,/Assets/Images/spritesheet_otacon.png",
                    UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception exception) when (
                exception is System.IO.IOException ||
                exception is NotSupportedException)
            {
                return null;
            }
        }

        private void SettingsOtaconTimer_Tick(object? sender, EventArgs e)
        {
            if (_otaconLaughing)
            {
                _otaconSpriteFrameIndex =
                    (_otaconSpriteFrameIndex + 1) % OtaconLaughFrames.Length;
                ShowSettingsOtaconFrame(OtaconLaughFrames[_otaconSpriteFrameIndex]);
                return;
            }

            if (_otaconThumbsUpAnimating)
            {
                _otaconSpriteFrameIndex++;
                if (_otaconSpriteFrameIndex < OtaconThumbsUpFrames.Length)
                {
                    ShowSettingsOtaconFrame(
                        OtaconThumbsUpFrames[_otaconSpriteFrameIndex]);
                    return;
                }

                _otaconSpriteTimer?.Stop();
                _otaconSpriteTimer = null;
                _otaconThumbsUpHoldTimer = new DispatcherTimer(
                    TimeSpan.FromSeconds(1),
                    DispatcherPriority.Render,
                    SettingsOtaconThumbsUpHoldTimer_Tick,
                    Dispatcher);
                _otaconThumbsUpHoldTimer.Start();
                return;
            }

            _otaconSpriteFrameIndex++;
            if (_otaconSpriteFrameIndex >= OtaconSettingsFrames.Length)
            {
                _otaconSpriteTimer?.Stop();
                _otaconSpriteTimer = null;
                return;
            }

            ShowSettingsOtaconFrame(OtaconSettingsFrames[_otaconSpriteFrameIndex]);
        }

        private void ShowSettingsOtaconFrame((int Row, int Column) frame)
        {
            if (_otaconSpriteSheet == null)
                return;

            // The authored sheet uses fixed 48x71 cells. Its final 10 pixels are
            // transparent padding and are not part of the twelve-column grid.
            int left = (frame.Column - 1) * OtaconSpriteFrameWidth;
            int top = (frame.Row - 1) * OtaconSpriteFrameHeight;

            var frameImage = new CroppedBitmap(
                _otaconSpriteSheet,
                new Int32Rect(
                    left,
                    top,
                    OtaconSpriteFrameWidth,
                    OtaconSpriteFrameHeight));
            frameImage.Freeze();
            SettingsOtaconSprite.Source = frameImage;

            // The poses are not centered identically within their authored
            // cells. Compensate at the 2x display scale so the character stays
            // anchored while the final held pose replaces the emergence frames.
            double sourcePixelOffset = frame switch
            {
                (3, 9) => 1.5,
                (3, 10) => 0.5,
                (4, 3) => -6,
                (4, 4) => -5.5,
                (4, 5) => -5.5,
                (2, 1) => -10,
                (2, 2) => -10,
                (2, 3) => -10,
                (2, 4) => -10,
                (1, 1) => -3,
                _ => 0
            };
            SettingsOtaconSpriteTranslation.X = sourcePixelOffset * 2;
            SettingsOtaconSpriteTranslation.Y = frame.Row switch
            {
                2 => 2,
                4 => -6,
                _ => 0
            };
        }

        private void SettingsOtaconSprite_MouseLeftButtonDown(
            object sender,
            MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left ||
                SettingsOverlay.Visibility != Visibility.Visible ||
                _settingsOverlayClosing)
            {
                return;
            }

            _otaconSpriteTimer?.Stop();
            _otaconLaughFallbackTimer?.Stop();
            _otaconLaughing = true;
            _otaconSpriteFrameIndex = 0;
            ShowSettingsOtaconFrame(OtaconLaughFrames[0]);

            if (!TryPlayOtaconLaughSound())
            {
                _otaconLaughFallbackTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(739),
                    DispatcherPriority.Background,
                    (_, _) => FinishSettingsOtaconLaughAnimation(),
                    Dispatcher);
                _otaconLaughFallbackTimer.Start();
            }

            _otaconSpriteTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(80),
                DispatcherPriority.Render,
                SettingsOtaconTimer_Tick,
                Dispatcher);
            _otaconSpriteTimer.Start();
            e.Handled = true;
        }

        private void FinishSettingsOtaconLaughAnimation()
        {
            if (!_otaconLaughing)
                return;

            _otaconLaughing = false;
            _otaconSpriteTimer?.Stop();
            _otaconSpriteTimer = null;
            _otaconLaughFallbackTimer?.Stop();
            _otaconLaughFallbackTimer = null;

            if (SettingsOverlay.Visibility == Visibility.Visible && !_settingsOverlayClosing)
                ShowSettingsOtaconFrame((1, 1));
        }

        private void StartSettingsOtaconThumbsUpAnimation()
        {
            _otaconSpriteStartTimer?.Stop();
            _otaconSpriteStartTimer = null;
            _otaconPopupSoundTimer?.Stop();
            _otaconPopupSoundTimer = null;
            _otaconSpriteTimer?.Stop();
            _otaconThumbsUpHoldTimer?.Stop();
            StopOtaconThumbsUpSound();

            _otaconLaughing = false;
            _otaconThumbsUpAnimating = true;
            _otaconSpriteFrameIndex = 0;
            ShowSettingsOtaconFrame(OtaconThumbsUpFrames[0]);
            SettingsOtaconSprite.Visibility = Visibility.Visible;
            PlayOtaconThumbsUpSound();

            _otaconSpriteTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(80),
                DispatcherPriority.Render,
                SettingsOtaconTimer_Tick,
                Dispatcher);
            _otaconSpriteTimer.Start();
        }

        private void SettingsOtaconThumbsUpHoldTimer_Tick(object? sender, EventArgs e)
        {
            _otaconThumbsUpHoldTimer?.Stop();
            _otaconThumbsUpHoldTimer = null;
            _otaconThumbsUpAnimating = false;

            if (SettingsOverlay.Visibility == Visibility.Visible && !_settingsOverlayClosing)
                ShowSettingsOtaconFrame((1, 1));
        }

        private void StopSettingsOtaconAnimation(bool hide)
        {
            _otaconSpriteStartTimer?.Stop();
            _otaconSpriteStartTimer = null;
            _otaconPopupSoundTimer?.Stop();
            _otaconPopupSoundTimer = null;
            _otaconSpriteTimer?.Stop();
            _otaconSpriteTimer = null;
            _otaconLaughFallbackTimer?.Stop();
            _otaconLaughFallbackTimer = null;
            _otaconThumbsUpHoldTimer?.Stop();
            _otaconThumbsUpHoldTimer = null;
            _otaconLaughing = false;
            _otaconThumbsUpAnimating = false;
            StopOtaconLaughSound();
            StopOtaconThumbsUpSound();

            if (hide)
            {
                SettingsOtaconSprite.Visibility = Visibility.Collapsed;
                SettingsOtaconSprite.Source = null;
            }
        }
    }
}
