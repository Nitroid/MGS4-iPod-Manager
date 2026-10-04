using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace iPodManager
{
    public partial class MainWindow
    {
        private bool _transferOverlayClosing;
        private CancellationTokenSource? _transferCancellation;
        private bool _transferRecoveryRequired;
        private bool _closeAfterTransfer;
        private int _displayedTransferPercentage;

        private const string TransferConfirmationHeader = "SYNC    TO    iPOD?";
        private const string TransferConfirmationBody =
            "This will update Snake's iPod to match your current selection. " +
            "The process may take several minutes.\n\nProceed?";
        private const string TransferProcessingHeader = "SYNCING...";
        private const string TransferProcessingBody = "Preparing sync...";

        private void TransferButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TransferHoverHighlight.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, TimeSpan.Zero));
            e.Handled = true;
        }

        private void TransferButton_Click(object sender, MouseButtonEventArgs e)
        {
            TransferHoverHighlight.BeginAnimation(OpacityProperty, null);
            if (_transferCancellation != null || _transferRecoveryRequired || _deploymentError != null)
            {
                e.Handled = true;
                return;
            }
            if (HasSelectedTracks())
                OpenTransferOverlayAnimated();
            else
                ShowNoTracksSelected();
            e.Handled = true;
        }

        private bool HasSelectedTracks()
        {
            foreach (CategoryItem category in CategoryItems)
            {
                foreach (TrackCollectionItem collection in category.TrackCollections)
                {
                    foreach (TrackItem track in collection.Tracks)
                    {
                        if (track.IsChecked)
                            return true;
                    }
                }
            }

            return false;
        }

        private void OpenTransferOverlayAnimated()
        {
            if (TransferOverlay.Visibility == Visibility.Visible)
                return;

            PlayMenuOpenSound();
            Duration duration = new(TimeSpan.FromMilliseconds(175));
            var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

            const double finalWidth = 384;
            const double finalHeight = 216;
            Point buttonPosition = TransferButton.TranslatePoint(new Point(), MainLayoutRoot);
            double finalLeft = (MainLayoutRoot.ActualWidth - finalWidth) / 2;
            double finalTop = (MainLayoutRoot.ActualHeight - finalHeight) / 2;
            double startScaleX = TransferButton.ActualWidth / finalWidth;
            double startScaleY = TransferButton.ActualHeight / finalHeight;
            double startX = buttonPosition.X + TransferButton.ActualWidth / 2 -
                (finalLeft + finalWidth / 2);
            double startY = buttonPosition.Y + TransferButton.ActualHeight / 2 -
                (finalTop + finalHeight / 2);

            TransferOverlay.Margin = new Thickness(finalLeft, finalTop, 0, 0);
            ResetTransferPresentation();
            SetTransferOverlayRestingVisuals();
            TransferOverlay.Visibility = Visibility.Visible;
            _transferOverlayClosing = false;

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
                if (TransferOverlay.Visibility == Visibility.Visible)
                    TransferInputShield.Focus();
            };
            BeginTransferOverlayAnimations(scaleX, scaleY, translateX, translateY, border, color, glow);
        }

        private void TransferInputShield_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseTransferOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_transferCancellation is { } cancellation)
            {
                cancellation.Cancel();
                TransferCancelButton.IsEnabled = false;
                SetTransferBodyText("Cancelling sync...");
            }
            else
                CloseTransferOverlay();
            e.Handled = true;
        }

        private void CloseTransferOverlay()
        {
            if (TransferOverlay.Visibility != Visibility.Visible || _transferOverlayClosing)
                return;

            _transferOverlayClosing = true;
            PlayMenuCloseSound();
            Duration duration = new(TimeSpan.FromMilliseconds(175));
            var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
            Point buttonPosition = TransferButton.TranslatePoint(new Point(), MainLayoutRoot);
            double targetScaleX = TransferButton.ActualWidth / TransferOverlay.Width;
            double targetScaleY = TransferButton.ActualHeight / TransferOverlay.Height;
            double targetX = buttonPosition.X + TransferButton.ActualWidth / 2 -
                (TransferOverlay.Margin.Left + TransferOverlay.Width / 2);
            double targetY = buttonPosition.Y + TransferButton.ActualHeight / 2 -
                (TransferOverlay.Margin.Top + TransferOverlay.Height / 2);

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
                TransferOverlay.Visibility = Visibility.Collapsed;
                Keyboard.ClearFocus();
                ClearTransferOverlayAnimations();
                SetTransferOverlayRestingVisuals();
                ResetTransferPresentation();
                _transferOverlayClosing = false;
            };
            BeginTransferOverlayAnimations(scaleX, scaleY, translateX, translateY, border, color, glow);
        }

        private void SetTransferOverlayRestingVisuals()
        {
            TransferOverlayScale.ScaleX = 1;
            TransferOverlayScale.ScaleY = 1;
            TransferOverlayTranslation.X = 0;
            TransferOverlayTranslation.Y = 0;
            TransferOverlayOuterBorder.BorderThickness = new Thickness(2);
            TransferOverlayBorderBrush.Color = Color.FromRgb(0x94, 0xA4, 0x8A);
            TransferOverlayGlowBorder.Opacity = 1;
        }

        private void BeginTransferOverlayAnimations(
            DoubleAnimation scaleX, DoubleAnimation scaleY,
            DoubleAnimation translateX, DoubleAnimation translateY,
            ThicknessAnimation border, ColorAnimation color, DoubleAnimation glow)
        {
            TransferOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            TransferOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            TransferOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, translateX);
            TransferOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, translateY);
            TransferOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, border);
            TransferOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, color);
            TransferOverlayGlowBorder.BeginAnimation(OpacityProperty, glow);
        }

        private void ClearTransferOverlayAnimations()
        {
            TransferOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TransferOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TransferOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            TransferOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            TransferOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, null);
            TransferOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            TransferOverlayGlowBorder.BeginAnimation(OpacityProperty, null);
        }

        private static ThicknessAnimation CreateTransferBorderAnimation(
            double from, double to, Duration duration, IEasingFunction easing) =>
            new(new Thickness(from), new Thickness(to), duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };

        private static ColorAnimation CreateTransferBorderColorAnimation(
            Color from, Color to, Duration duration, IEasingFunction easing) =>
            new(from, to, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };

        private static DoubleAnimation CreateTransferGlowAnimation(
            double from, double to, Duration duration, IEasingFunction easing) =>
            new(from, to, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };

        private async void TransferYesButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_transferCancellation != null || _transferRecoveryRequired || _deploymentError != null)
                return;

            TrackItem[] selected = CategoryItems
                .SelectMany(category => category.TrackCollections)
                .SelectMany(collection => collection.Tracks)
                .Where(track => track.IsChecked)
                .ToArray();
            if (selected.Length == 0)
            {
                CloseTransferOverlay();
                ShowNoTracksSelected();
                return;
            }
            Stopwatch syncStopwatch = Stopwatch.StartNew();

            var settings = new ApplicationPreferences
            {
                ConversionQuality = _qualityValue,
                UiSoundsDisabled = _soundEffectsDisabled
            };
            var cancellation = new CancellationTokenSource();
            _transferCancellation = cancellation;
            string? errorMessage = null;
            bool completed = false;
            bool refreshLibrary = true;

            try
            {
                SuspendCategoryWatchers();
                SetTransferUiActive(true);
                TransferYesButton.IsEnabled = false;
                TransferNoButton.IsEnabled = false;
                PlaySopLoadSound();
                AppLog.Info($"Transfer started: {selected.Length} selected tracks.");

                TimeSpan transitionDuration = TimeSpan.FromMilliseconds(500);
                await FadeTransferElementsAsync(
                    1,
                    0,
                    transitionDuration,
                    cancellation.Token,
                    TransferHeaderContainer,
                    TransferBodyContainer,
                    TransferYesButton,
                    TransferNoButton);
                cancellation.Token.ThrowIfCancellationRequested();

                TransferDecisionButtons.Visibility = Visibility.Collapsed;
                TransferCancelButton.Visibility = Visibility.Visible;
                TransferSuccessIndicator.Visibility = Visibility.Collapsed;
                TransferBodyContainer.Margin = new Thickness(0, 5, 0, 0);
                SetTransferHeaderText(TransferProcessingHeader);
                SetTransferBodyText(TransferProcessingBody);
                StartTransferLoadingRotation();

                await FadeTransferElementsAsync(
                    0,
                    1,
                    transitionDuration,
                    cancellation.Token,
                    TransferHeaderContainer,
                    TransferBodyContainer,
                    TransferCancelButton,
                    TransferLoadingIndicator);

                var progress = new Progress<Mgs4IpodService.TransferProgress>(value =>
                {
                    if (ReferenceEquals(_transferCancellation, cancellation) &&
                        !_closeAfterTransfer && !cancellation.IsCancellationRequested)
                        ApplyTransferProgress(value);
                });
                Mgs4IpodService.TransferResult result = await Task.Run(() =>
                    new Mgs4IpodService(_paths).TransferAsync(
                        selected, settings, progress, cancellation.Token), cancellation.Token);
                AppLog.Info($"Transfer completed: {result.TrackCount} tracks; {result.Added} added, {result.Updated} updated, {result.Removed} removed.");
                completed = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AppLog.Info("Transfer cancelled.");
                refreshLibrary = false;
            }
            catch (Mgs4IpodService.TransferException exception)
            {
                AppLog.Error($"Transfer failed: {exception.FailureState}.", exception);
                refreshLibrary = false;
                if (exception.FailureState == Mgs4IpodService.TransferFailureState.RecoveryRequired)
                {
                    _transferRecoveryRequired = true;
                }
                errorMessage = exception.UserMessage ?? TransferFailureMessages.For(exception.FailureState);
            }
            catch (Exception exception)
            {
                AppLog.Error("Transfer failed unexpectedly; deployment state could not be verified.", exception);
                _transferRecoveryRequired = true;
                refreshLibrary = false;
                errorMessage = "Sync did not complete. Deployment state needs attention. Do not retry; check iPodManager.log.";
            }
            finally
            {
                if (refreshLibrary)
                {
                    try
                    {
                        await RefreshLibraryAfterTransferAsync();
                        if (_deploymentError != null)
                        {
                            _transferRecoveryRequired = true;
                            errorMessage = _deploymentError;
                        }
                    }
                    catch (Exception exception)
                    {
                        AppLog.Error("Transfer ended, but the library could not be refreshed.", exception);
                        _transferRecoveryRequired = true;
                        errorMessage = "The sync ended, but the music library could not be refreshed. Check iPodManager.log before retrying.";
                    }
                }

                _transferCancellation = null;
                cancellation.Dispose();
                if (!_transferRecoveryRequired)
                    ResumeCategoryWatchers();
                SetTransferUiActive(false);

                if (_closeAfterTransfer)
                {
                    AppLog.Info("Transfer ended; closing window.");
                    Close();
                }
                else if (completed && errorMessage == null)
                {
                    SetTransferPercentage(100);
                    StopTransferLoadingAnimation();
                    TransferBodyContainer.Margin = new Thickness(0, 5, 0, 0);
                    SetTransferHeaderText("SYNC    COMPLETE");
                    SetTransferBodyText("Snake's iPod has been updated.");
                    TransferSuccessIndicator.Visibility = Visibility.Visible;
                    TransferCancelButton.Content = "CLOSE";
                    TransferCancelButton.IsEnabled = true;
                    syncStopwatch.Stop();
                    PlaySyncCompletionSound(syncStopwatch.Elapsed);
                }
                else
                {
                    HideTransferOverlayImmediately();
                    if (errorMessage != null)
                        ShowError(errorMessage);
                }
            }
        }

        private void ApplyTransferProgress(Mgs4IpodService.TransferProgress progress)
        {
            string text = progress.Stage switch
            {
                Mgs4IpodService.TransferStage.Validating => "Preparing sync...",
                Mgs4IpodService.TransferStage.Converting or Mgs4IpodService.TransferStage.Building when progress.Total > 0 =>
                    $"Preparing track {progress.Current + 1} of {progress.Total}...",
                Mgs4IpodService.TransferStage.Installing or
                Mgs4IpodService.TransferStage.TransactionPrepared or
                Mgs4IpodService.TransferStage.FilesApplied or
                Mgs4IpodService.TransferStage.StatePublished or
                Mgs4IpodService.TransferStage.DeploymentCommitted or
                Mgs4IpodService.TransferStage.CleanupComplete => "Applying changes...",
                Mgs4IpodService.TransferStage.Complete => "Finalizing sync...",
                _ => "Processing sync..."
            };
            if (TransferBodyText.Text != text)
                SetTransferBodyText(text);
            int percentage = progress.Stage switch
            {
                Mgs4IpodService.TransferStage.Validating => 3,
                Mgs4IpodService.TransferStage.Converting or Mgs4IpodService.TransferStage.Building
                    when progress.Total > 0 =>
                    5 + (int)Math.Clamp(
                        ((long)progress.Current * 2 +
                         (progress.Stage == Mgs4IpodService.TransferStage.Building ? 1 : 0)) * 65 /
                        ((long)progress.Total * 2), 0, 65),
                Mgs4IpodService.TransferStage.Installing => 70,
                Mgs4IpodService.TransferStage.TransactionPrepared => 76,
                Mgs4IpodService.TransferStage.FilesApplied => 83,
                Mgs4IpodService.TransferStage.StatePublished => 88,
                Mgs4IpodService.TransferStage.DeploymentCommitted => 92,
                Mgs4IpodService.TransferStage.CleanupComplete => 94,
                Mgs4IpodService.TransferStage.Complete => 95,
                _ => _displayedTransferPercentage
            };
            SetTransferPercentage(percentage);
        }

        private void SetTransferPercentage(int percentage)
        {
            int next = Math.Max(_displayedTransferPercentage, Math.Clamp(percentage, 0, 100));
            if (next == _displayedTransferPercentage)
                return;
            _displayedTransferPercentage = next;
            TransferLoadingProgressText.Text = $"{next}%";
        }

        private void SetTransferUiActive(bool active)
        {
            if (active)
                _analysisCancellation?.Cancel();
            CategoryList.IsHitTestVisible = !active;
            TrackListPanel.IsHitTestVisible = !active;
            SettingsButton.IsEnabled = !active;
            TransferButton.IsEnabled = !active && !_transferRecoveryRequired && _deploymentError == null;
        }

        private async Task RefreshLibraryAfterTransferAsync()
        {
            string? categoryName = (CategoryList.SelectedItem as CategoryItem)?.Name;
            string? tabName = (TrackTabsList.SelectedItem as TrackCollectionItem)?.Name;
            var result = await Task.Run(() =>
            {
                var deployment = LoadDeploymentState();
                CategoryItem[] categories = LibraryDiscovery.Discover(
                    _paths, _defaultCatalogBytes, deployment.Manifest, deployment.Tracks,
                    deployment.Error, CancellationToken.None);
                return (deployment, categories);
            });
            SetTransferPercentage(98);

            _deploymentManifest = result.deployment.Manifest;
            _deployedTracks = result.deployment.Tracks;
            _deploymentError = result.deployment.Error;
            _categoryTabCache.Clear();
            _suppressCategorySelectionSound = true;
            try
            {
                CategoryItems.Clear();
                foreach (CategoryItem category in result.categories)
                    CategoryItems.Add(category);
                CategoriesTotalSizeText = LibraryDiscovery.FormatFileSize(
                    result.categories.Sum(category => category.SizeBytes));
                int index = Array.FindIndex(result.categories,
                    category => category.Name == categoryName);
                CategoryList.SelectedIndex = index >= 0 ? index : 0;
                ShowSelectedCategory();
                if (tabName != null)
                {
                    TrackCollectionItem? tab = GetCachedCategoryTabs(result.categories[CategoryList.SelectedIndex])
                        .FirstOrDefault(candidate => candidate.Name == tabName);
                    if (tab != null) TrackTabsList.SelectedItem = tab;
                }
            }
            finally { _suppressCategorySelectionSound = false; }
            SetTransferPercentage(99);
            AppLog.Info("Library refreshed after transfer.");
        }

        private void HideTransferOverlayImmediately()
        {
            ClearTransferOverlayAnimations();
            TransferOverlay.Visibility = Visibility.Collapsed;
            ResetTransferPresentation();
        }

        private static async Task FadeTransferElementsAsync(
            double from,
            double to,
            TimeSpan duration,
            CancellationToken token,
            params UIElement[] elements)
        {
            foreach (UIElement element in elements)
            {
                element.Opacity = from;
                element.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(from, to, new Duration(duration))
                    {
                        FillBehavior = FillBehavior.Stop
                    });
            }

            await Task.Delay(duration, token);

            foreach (UIElement element in elements)
            {
                element.BeginAnimation(OpacityProperty, null);
                element.Opacity = to;
            }
        }

        private void StartTransferLoadingRotation()
        {
            TransferLoadingIndicator.Visibility = Visibility.Visible;
            TransferLoadingIconRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1100))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        private void StopTransferLoadingAnimation()
        {
            TransferLoadingIndicator.BeginAnimation(OpacityProperty, null);
            TransferLoadingIconRotation.BeginAnimation(
                RotateTransform.AngleProperty,
                null);
            TransferLoadingIconRotation.Angle = 0;
            TransferLoadingIndicator.Opacity = 0;
            TransferLoadingIndicator.Visibility = Visibility.Collapsed;
        }

        private void SetTransferHeaderText(string value)
        {
            TransferHeaderShadowText.ItemsSource = value;
            TransferHeaderText.ItemsSource = value;
        }

        private void SetTransferBodyText(string value)
        {
            TransferBodyShadowText.Text = value;
            TransferBodyText.Text = value;
        }

        private void ResetTransferPresentation()
        {
            StopTransferLoadingAnimation();
            TransferSuccessIndicator.Visibility = Visibility.Collapsed;
            TransferBodyContainer.Margin = new Thickness(0, 17, 0, 0);
            SetTransferHeaderText(TransferConfirmationHeader);
            SetTransferBodyText(TransferConfirmationBody);
            TransferHeaderContainer.BeginAnimation(OpacityProperty, null);
            TransferBodyContainer.BeginAnimation(OpacityProperty, null);
            TransferHeaderContainer.Opacity = 1;
            TransferBodyContainer.Opacity = 1;
            TransferDecisionButtons.Visibility = Visibility.Visible;
            TransferYesButton.IsEnabled = true;
            TransferNoButton.IsEnabled = true;
            TransferYesButton.Opacity = 1;
            TransferNoButton.Opacity = 1;
            TransferYesButton.OpacityMask = null;
            TransferNoButton.OpacityMask = null;
            TransferCancelButton.BeginAnimation(OpacityProperty, null);
            TransferCancelButton.OpacityMask = null;
            TransferCancelButton.Opacity = 0;
            TransferCancelButton.Visibility = Visibility.Collapsed;
            TransferCancelButton.Content = "CANCEL";
            TransferCancelButton.IsEnabled = true;
            TransferLoadingProgressText.Text = "0%";
            _displayedTransferPercentage = 0;
        }
    }
}
