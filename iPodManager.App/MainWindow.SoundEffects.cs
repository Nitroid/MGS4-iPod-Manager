using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;

namespace iPodManager
{
    public partial class MainWindow
    {
        private MediaPlayer? _menuSelectPlayer;
        private MediaPlayer? _menuOpenPlayer;
        private MediaPlayer? _menuClosePlayer;
        private MediaPlayer? _completeShortPlayer;
        private MediaPlayer? _completeLongPlayer;
        private MediaPlayer? _qualityClickPlayer;
        private MediaPlayer? _categorySelectPlayer;
        private MediaPlayer? _trackTogglePlayer;
        private MediaPlayer? _konamiCodePlayer;
        private MediaPlayer? _otaconPopupPlayer;
        private MediaPlayer? _otaconLaughPlayer;
        private MediaPlayer? _otaconThumbsUpPlayer;
        private MediaPlayer? _sopLoadPlayer;
        private MediaPlayer? _alertPlayer;
        private MediaPlayer? _buzzerPlayer;
        private bool _soundEffectsEnabled;
        private bool _soundEffectsDisabled;
        private DispatcherTimer? _startupSoundGateTimer;
        private readonly Dictionary<string, string> _materializedMedia =
            new(StringComparer.OrdinalIgnoreCase);
        private string? _temporaryMediaDirectory;

        private void InitializeSoundEffects()
        {
            // WPF can synthesize MouseEnter events while the window is first
            // appearing beneath a stationary cursor. Keep all playback gated
            // until initial rendering and hit testing have settled.
            ContentRendered += (_, _) => StartStartupSoundGateTimer();

            _menuSelectPlayer = LoadEmbeddedSound("Assets/Audio/menu_select.mp3");
            _menuOpenPlayer = LoadEmbeddedSound("Assets/Audio/window_open.mp3");
            _menuClosePlayer = LoadEmbeddedSound("Assets/Audio/window_close.mp3");
            _completeShortPlayer = LoadEmbeddedSound("Assets/Audio/complete_short.mp3");
            _completeLongPlayer = LoadEmbeddedSound("Assets/Audio/complete_long.mp3");
            _qualityClickPlayer = LoadEmbeddedSound("Assets/Audio/codec_change_frequency.mp3");
            _categorySelectPlayer = LoadEmbeddedSound("Assets/Audio/menu_weapon_equip.mp3");
            _trackTogglePlayer = LoadEmbeddedSound("Assets/Audio/io_button.mp3");
            _konamiCodePlayer = LoadEmbeddedSound("Assets/Audio/konami_code.mp3");
            _otaconPopupPlayer = LoadEmbeddedSound("Assets/Audio/otacon_popup.mp3");
            _otaconLaughPlayer = LoadEmbeddedSound("Assets/Audio/otacon_laugh.mp3");
            _otaconThumbsUpPlayer = LoadEmbeddedSound("Assets/Audio/otacon_thumbs_up.mp3");
            _sopLoadPlayer = LoadEmbeddedSound("Assets/Audio/sop_load.mp3");
            _alertPlayer = LoadEmbeddedSound("Assets/Audio/alert.mp3");
            _buzzerPlayer = LoadEmbeddedSound("Assets/Audio/buzzer.mp3");
            if (_otaconLaughPlayer is not null)
            {
                _otaconLaughPlayer.MediaEnded += (_, _) =>
                    Dispatcher.BeginInvoke(new Action(FinishSettingsOtaconLaughAnimation));
            }
            SetSoundPlayerVolumes(0, 0);

            Closed += (_, _) =>
            {
                _startupSoundGateTimer?.Stop();
                DisposeSoundPlayers();
                CleanupTemporaryMedia();
            };
        }

        private void StartStartupSoundGateTimer()
        {
            if (_soundEffectsEnabled || _startupSoundGateTimer != null)
                return;

            _startupSoundGateTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(750),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    _startupSoundGateTimer?.Stop();
                    _startupSoundGateTimer = null;
                    StopSoundPlayers();
                    SetSoundPlayerVolumes(
                        _soundEffectsDisabled ? 0 : 0.5,
                        _soundEffectsDisabled ? 0 : 0.25);
                    _soundEffectsEnabled = true;
                },
                Dispatcher);
            _startupSoundGateTimer.Start();
        }

        private MediaPlayer? LoadEmbeddedSound(string resourcePath)
        {
            string? soundPath = MaterializeEmbeddedMedia(resourcePath);
            if (soundPath is null)
                return null;

            var player = new MediaPlayer { Volume = 0 };
            player.Open(new Uri(soundPath, UriKind.Absolute));
            return player;
        }

        private MediaPlayer?[] GetSoundPlayers() =>
        [
            _menuSelectPlayer,
            _menuOpenPlayer,
            _menuClosePlayer,
            _completeShortPlayer,
            _completeLongPlayer,
            _qualityClickPlayer,
            _categorySelectPlayer,
            _trackTogglePlayer,
            _konamiCodePlayer,
            _otaconPopupPlayer,
            _otaconLaughPlayer,
            _otaconThumbsUpPlayer,
            _sopLoadPlayer,
            _alertPlayer,
            _buzzerPlayer
        ];

        private void StopSoundPlayers()
        {
            foreach (MediaPlayer? player in GetSoundPlayers())
                player?.Stop();
        }

        private void SetSoundPlayerVolumes(double standardVolume, double completionVolume)
        {
            if (_menuSelectPlayer is not null) _menuSelectPlayer.Volume = standardVolume;
            if (_menuOpenPlayer is not null) _menuOpenPlayer.Volume = standardVolume;
            if (_menuClosePlayer is not null) _menuClosePlayer.Volume = standardVolume;
            if (_completeShortPlayer is not null) _completeShortPlayer.Volume = completionVolume;
            if (_completeLongPlayer is not null) _completeLongPlayer.Volume = completionVolume;
            if (_qualityClickPlayer is not null) _qualityClickPlayer.Volume = standardVolume;
            if (_categorySelectPlayer is not null) _categorySelectPlayer.Volume = standardVolume * 0.5;
            if (_trackTogglePlayer is not null) _trackTogglePlayer.Volume = standardVolume;
            if (_konamiCodePlayer is not null) _konamiCodePlayer.Volume = standardVolume;
            if (_otaconPopupPlayer is not null) _otaconPopupPlayer.Volume = standardVolume;
            if (_otaconLaughPlayer is not null) _otaconLaughPlayer.Volume = standardVolume;
            if (_otaconThumbsUpPlayer is not null) _otaconThumbsUpPlayer.Volume = standardVolume;
            if (_sopLoadPlayer is not null) _sopLoadPlayer.Volume = standardVolume;
            if (_alertPlayer is not null) _alertPlayer.Volume = standardVolume * 0.6;
            if (_buzzerPlayer is not null) _buzzerPlayer.Volume = standardVolume;
        }

        private void DisposeSoundPlayers()
        {
            foreach (MediaPlayer? player in GetSoundPlayers())
            {
                player?.Stop();
                player?.Close();
            }
        }

        private string? MaterializeEmbeddedMedia(string resourcePath)
        {
            if (_materializedMedia.TryGetValue(resourcePath, out string? existingPath))
                return existingPath;

            Stream? resourceStream = Application.GetResourceStream(
                new Uri(resourcePath, UriKind.Relative))?.Stream;
            if (resourceStream is null)
                return null;

            using (resourceStream)
            {
                _temporaryMediaDirectory ??= Path.Combine(
                    Path.GetTempPath(),
                    "iPodManager",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_temporaryMediaDirectory);

                string outputPath = Path.Combine(
                    _temporaryMediaDirectory,
                    Path.GetFileName(resourcePath));
                using FileStream output = File.Create(outputPath);
                resourceStream.CopyTo(output);
                _materializedMedia[resourcePath] = outputPath;
                return outputPath;
            }
        }

        private void CleanupTemporaryMedia()
        {
            if (_temporaryMediaDirectory is null || !Directory.Exists(_temporaryMediaDirectory))
                return;

            try
            {
                Directory.Delete(_temporaryMediaDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The OS will clear its temporary directory if the media subsystem
                // briefly retains a handle during application shutdown.
            }
            catch (UnauthorizedAccessException)
            {
                // Playback should never be allowed to interfere with application exit.
            }
        }

        private void PlayMenuSelectSound()
        {
            TryPlaySound(_menuSelectPlayer);
        }

        private void PlayKonamiCodeSound()
        {
            TryPlaySound(_konamiCodePlayer);
        }

        private void PlayMenuOpenSound()
        {
            TryPlaySound(_menuOpenPlayer);
        }

        private void PlayMenuCloseSound()
        {
            TryPlaySound(_menuClosePlayer);
        }

        private void PlayCompleteShortSound()
        {
            TryPlaySound(_completeShortPlayer);
        }

        private void PlaySyncCompletionSound(TimeSpan elapsed)
        {
            bool useLongSound = elapsed >= TimeSpan.FromMinutes(1);
            try
            {
                TryPlaySound(useLongSound ? _completeLongPlayer : _completeShortPlayer);
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Could not play the {(useLongSound ? "long" : "short")} sync completion sound: {exception}");
            }
        }

        private void PlayQualityClickSound()
        {
            TryPlaySound(_qualityClickPlayer);
        }

        private void PlayCategorySelectSound()
        {
            TryPlaySound(_categorySelectPlayer);
        }

        private void PlayTrackToggleSound()
        {
            TryPlaySound(_trackTogglePlayer);
        }

        private void PlayOtaconPopupSound()
        {
            TryPlaySound(_otaconPopupPlayer);
        }

        private bool TryPlayOtaconLaughSound()
        {
            return TryPlaySound(_otaconLaughPlayer);
        }

        private void StopOtaconLaughSound()
        {
            _otaconLaughPlayer?.Stop();
        }

        private void PlayOtaconThumbsUpSound()
        {
            TryPlaySound(_otaconThumbsUpPlayer);
        }

        private void PlaySopLoadSound()
        {
            TryPlaySound(_sopLoadPlayer);
        }

        private void PlayAlertSound()
        {
            TryPlaySound(_alertPlayer);
        }

        private void PlayBuzzerSound()
        {
            TryPlaySound(_buzzerPlayer);
        }

        private bool TryPlaySound(MediaPlayer? player)
        {
            if (_soundEffectsDisabled || !_soundEffectsEnabled || player is null)
                return false;

            player.Position = TimeSpan.Zero;
            player.Play();
            return true;
        }

        private void StopOtaconThumbsUpSound()
        {
            _otaconThumbsUpPlayer?.Stop();
        }

        private void SoundEffectsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle)
                return;

            bool disableSoundEffects = toggle.IsChecked == true;

            _soundEffectsDisabled = disableSoundEffects;
            if (disableSoundEffects)
                SetSoundPlayerVolumes(0, 0);
            StopSoundPlayers();
            FinishSettingsOtaconLaughAnimation();

            if (!disableSoundEffects)
                SetSoundPlayerVolumes(0.5, 0.25);

            StartSettingsOtaconThumbsUpAnimation();
            SavePreferences();
        }

        private void BackgroundPlaybackToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton toggle)
                return;

            PlayTrackToggleSound();
            _backgroundPlaybackEnabled = toggle.IsChecked == true;
            SavePreferences();
        }

        private void CategoryListItem_MouseEnter(object sender, MouseEventArgs e)
        {
            PlayMenuSelectSound();
        }

        private void MenuSelectSound_MouseEnter(object sender, MouseEventArgs e)
        {
            PlayMenuSelectSound();
        }

        private void PlayMenuSelectSoundForTooltipTarget(object sender)
        {
            if (sender is not FrameworkElement element || element.Tag is not string tag)
                return;

            if (tag is "settings" or "transfer_to_ipod" or "minimize" or "close")
                PlayMenuSelectSound();
        }
    }
}
