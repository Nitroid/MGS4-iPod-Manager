using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace iPodManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _settingsOverlayClosing;
        private bool _analysisClosed;
        private bool _libraryClosed;
        private readonly CancellationTokenSource _libraryCancellation = new();
        private byte[] _defaultCatalogBytes = [];
        private CancellationTokenSource? _analysisCancellation;
        private readonly HashSet<CancellationTokenSource> _analysisRequests = new();
        private readonly HashSet<Task> _analysisWorkers = new();
        private bool _startupTimingActive;
        private bool _startupLibraryResultsApplied;
        private bool _startupLibraryReadyLogged;

        private async Task<T> RunAnalysisWorkerAsync<T>(
            Func<Task<T>> work, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<T> worker = Task.Run(work, cancellationToken);
            _analysisWorkers.Add(worker);
            try
            {
                return await worker;
            }
            finally
            {
                _analysisWorkers.Remove(worker);
            }
        }

        private async Task AnalyzeHoveredTrackAsync(TrackItem track)
        {
            if (_analysisClosed)
                return;

            _analysisCancellation?.Cancel();
            var request = new CancellationTokenSource();
            _analysisCancellation = request;
            _analysisRequests.Add(request);
            CancellationToken token = request.Token;
            try
            {
                await Task.WhenAll(
                    ObserveAnalysisAsync(ShowWaveformAsync(track.FilePath, token), token),
                    ObserveAnalysisAsync(
                        ShowTrackAudioMetadataAsync(track.FilePath, track.Annotation, token), token));
            }
            finally
            {
                if (ReferenceEquals(_analysisCancellation, request))
                    _analysisCancellation = null;
                _analysisRequests.Remove(request);
                request.Dispose();
            }
        }

        private async Task ObserveAnalysisAsync(Task analysis, CancellationToken token)
        {
            try
            {
                await analysis;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Superseded requests and window shutdown are expected cancellation.
            }
            catch (Exception exception)
            {
                if (!_analysisClosed && !token.IsCancellationRequested)
                {
                    AppLog.Error("Could not analyze the selected audio track.", exception);
                    ShowError("Could not analyze this audio track. Check that the file is readable.");
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopSpecialThanksAnimation();
            _libraryClosed = true;
            _libraryCancellation.Cancel();
            _libraryCancellation.Dispose();
            _analysisClosed = true;
            ++_waveformDisplayVersion;
            foreach (CancellationTokenSource request in _analysisRequests)
                request.Cancel();

            try
            {
                // Only wait for worker tasks, never dispatcher-bound presentation.
                // Workers finish decoder/stream cleanup before completing.
                Task.WhenAll(_analysisWorkers).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal shutdown path.
            }
            catch (Exception exception)
            {
                AppLog.Warn($"Analysis shutdown: {exception}");
            }
            finally
            {
                foreach (CancellationTokenSource request in _analysisRequests)
                    request.Dispose();
                _analysisRequests.Clear();
                _analysisWorkers.Clear();
                _analysisCancellation = null;
                base.OnClosed(e);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_transferCancellation is { } cancellation)
            {
                AppLog.Info("Window close requested during transfer; cancelling first.");
                e.Cancel = true;
                _closeAfterTransfer = true;
                cancellation.Cancel();
                TransferCancelButton.IsEnabled = false;
                SetTransferBodyText("Cancelling sync...");
                return;
            }
            base.OnClosing(e);
        }

        public static readonly DependencyProperty HoveredTrackFileNameProperty =
            DependencyProperty.Register(
                nameof(HoveredTrackFileName),
                typeof(string),
                typeof(MainWindow),
                new PropertyMetadata(string.Empty));

        public string HoveredTrackFileName
        {
            get => (string)GetValue(HoveredTrackFileNameProperty);
            set => SetValue(HoveredTrackFileNameProperty, value);
        }

        public static readonly DependencyProperty HoveredTrackDurationProperty =
            DependencyProperty.Register(
                nameof(HoveredTrackDuration),
                typeof(string),
                typeof(MainWindow),
                new PropertyMetadata(string.Empty));

        public string HoveredTrackDuration
        {
            get => (string)GetValue(HoveredTrackDurationProperty);
            set => SetValue(HoveredTrackDurationProperty, value);
        }

        public static readonly DependencyProperty CategoriesTotalSizeTextProperty =
            DependencyProperty.Register(
                nameof(CategoriesTotalSizeText),
                typeof(string),
                typeof(MainWindow),
                new PropertyMetadata("0.00 MB"));

        public string CategoriesTotalSizeText
        {
            get => (string)GetValue(CategoriesTotalSizeTextProperty);
            set => SetValue(CategoriesTotalSizeTextProperty, value);
        }

        public MainWindow()
        {
            var constructorTimer = Stopwatch.StartNew();
            var xamlTimer = Stopwatch.StartNew();
            InitializeComponent();
            StartStartupLoadingState();
            AppLog.Info($"startup.window.xaml elapsed_ms={xamlTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
            InitializeApplicationState();
            DisableSoundEffectsToggle.IsChecked = _soundEffectsDisabled;
            BackgroundPlaybackToggle.IsChecked = _backgroundPlaybackEnabled;
            InitializeSoundEffects();
            InitializeKonamiCodeDetection();
            KonamiCodeEntered += MainWindow_KonamiCodeEntered;
            LoadTooltipCatalog();
            CategoryList.IsEnabled = false;
            TrackTabsList.IsEnabled = false;
            TrackItemsList.IsEnabled = false;
            TransferButton.IsEnabled = false;
            Loaded += MainWindow_Loaded;
            AppLog.Info($"startup.window.constructor elapsed_ms={constructorTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            await InitializeLibraryAsync();
        }

        private async Task InitializeLibraryAsync()
        {
            _startupTimingActive = true;
            AppLog.Info("Library discovery started.");
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var catalogTimer = Stopwatch.StartNew();
                var catalogUri = new Uri("pack://application:,,,/Assets/Data/default_tracks.json");
                using (Stream stream = Application.GetResourceStream(catalogUri).Stream)
                using (var catalog = new MemoryStream())
                {
                    stream.CopyTo(catalog);
                    _defaultCatalogBytes = catalog.ToArray();
                }
                AppLog.Info($"startup.catalog_resource bytes={_defaultCatalogBytes.Length} elapsed_ms={catalogTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
                _startupProgress?.SetupComplete();

                CancellationToken token = _libraryCancellation.Token;
                StartupProgress? startupProgress = _startupProgress;
                var result = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    var deploymentTimer = Stopwatch.StartNew();
                    var deployment = ReadDeploymentState((completed, total) =>
                        startupProgress?.Deployment(completed, total));
                    AppLog.Info($"startup.deployment_state records={deployment.Tracks.Count} elapsed_ms={deploymentTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
                    var discoveryTimer = Stopwatch.StartNew();
                    CategoryItem[] categories = LibraryDiscovery.Discover(
                        _paths, _defaultCatalogBytes, deployment.Manifest, deployment.Tracks,
                        deployment.Error, token, (completed, total) =>
                            startupProgress?.Discovery(completed, total));
                    AppLog.Info($"startup.library.discovery categories={categories.Length} elapsed_ms={discoveryTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
                    return (deployment, categories);
                }, token);

                if (_libraryClosed || token.IsCancellationRequested)
                    return;

                _deploymentManifest = result.deployment.Manifest;
                _deployedTracks = result.deployment.Tracks;
                _deploymentError = result.deployment.Error;
                var populationTimer = Stopwatch.StartNew();
                foreach (CategoryItem category in result.categories)
                    CategoryItems.Add(category);
                _startupProgress?.CategoriesBuilt();
                _startupLibraryResultsApplied = true;
                int totalTracks = result.categories.Sum(category =>
                    category.TrackCollections.Sum(collection => collection.Tracks.Count));
                AppLog.Info($"startup.ui.categories count={result.categories.Length} tracks={totalTracks} elapsed_ms={populationTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
                CategoriesTotalSizeText = LibraryDiscovery.FormatFileSize(
                    result.categories.Sum(category => category.SizeBytes));

                var selectionTimer = Stopwatch.StartNew();
                _suppressCategorySelectionSound = true;
                try { CategoryList.SelectedIndex = 0; }
                finally { _suppressCategorySelectionSound = false; }
                AppLog.Info($"startup.ui.selection_restore elapsed_ms={selectionTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");

                CategoryList.IsEnabled = true;
                TrackTabsList.IsEnabled = true;
                TrackItemsList.IsEnabled = true;
                TransferButton.IsEnabled = _deploymentError == null;
                InitializeCategoryCachingAndWatchers();
                _startupProgress?.InitialBindingComplete();

                int[] counts = result.categories.Select(category =>
                    category.TrackCollections.Sum(collection => collection.Tracks.Count)).ToArray();
                AppLog.Info($"Library discovery completed: Default={counts[0]}, Podcasts={counts[1]}, Custom={counts[2]} ({stopwatch.ElapsedMilliseconds} ms).");
                if (_deploymentError is string deploymentError)
                    ShowError(deploymentError);
            }
            catch (OperationCanceledException) when (_libraryCancellation.IsCancellationRequested)
            {
                // Closing the window leaves the worker result unapplied.
            }
            catch (Exception exception)
            {
                if (!_libraryClosed)
                {
                    StopStartupLoadingState();
                    AppLog.Error("Library discovery failed.", exception);
                    ShowError("The music library could not be loaded. Check iPodManager.log for details.");
                }
            }
        }
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void SettingsButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            SettingsHoverHighlight.BeginAnimation(
                OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.Zero));
            e.Handled = true;
        }

        private void SettingsButton_Click(object sender, MouseButtonEventArgs e)
        {
            SettingsHoverHighlight.BeginAnimation(OpacityProperty, null);
            OpenSettingsOverlayAnimated();
            e.Handled = true;
        }

        private void OpenSettingsOverlayAnimated()
        {
            PlayMenuOpenSound();

            const double finalWidth = 384;
            const double finalHeight = 216;
            var duration = new Duration(TimeSpan.FromMilliseconds(175));
            var easing = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            };

            Point buttonPosition = SettingsButton.TranslatePoint(new Point(0, 0), MainLayoutRoot);
            double finalLeft = (MainLayoutRoot.ActualWidth - finalWidth) / 2;
            double finalTop = (MainLayoutRoot.ActualHeight - finalHeight) / 2;
            double startScaleX = SettingsButton.ActualWidth / finalWidth;
            double startScaleY = SettingsButton.ActualHeight / finalHeight;
            double startTranslationX = buttonPosition.X + SettingsButton.ActualWidth / 2 -
                (finalLeft + finalWidth / 2);
            double startTranslationY = buttonPosition.Y + SettingsButton.ActualHeight / 2 -
                (finalTop + finalHeight / 2);

            SettingsOverlay.Width = finalWidth;
            SettingsOverlay.Height = finalHeight;
            SettingsOverlay.Margin = new Thickness(finalLeft, finalTop, 0, 0);
            SettingsOverlayScale.ScaleX = 1;
            SettingsOverlayScale.ScaleY = 1;
            SettingsOverlayTranslation.X = 0;
            SettingsOverlayTranslation.Y = 0;
            SettingsOverlayOuterBorder.BorderThickness = new Thickness(2);
            SettingsOverlayBorderBrush.Color = Color.FromRgb(0x94, 0xA4, 0x8A);
            SettingsOverlayGlowBorder.Opacity = 1;
            SettingsOverlay.Visibility = Visibility.Visible;
            _settingsOverlayClosing = false;

            var scaleXAnimation = CreateSettingsTransformAnimation(startScaleX, 1, duration, easing);
            var scaleYAnimation = CreateSettingsTransformAnimation(startScaleY, 1, duration, easing);
            var translateXAnimation = CreateSettingsTransformAnimation(startTranslationX, 0, duration, easing);
            var translateYAnimation = CreateSettingsTransformAnimation(startTranslationY, 0, duration, easing);
            var borderAnimation = new System.Windows.Media.Animation.ThicknessAnimation(
                new Thickness(1),
                new Thickness(2),
                duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            var borderColorAnimation = new System.Windows.Media.Animation.ColorAnimation(
                Color.FromArgb(0xCC, 0, 0, 0),
                Color.FromRgb(0x94, 0xA4, 0x8A),
                duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            var glowAnimation = new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };

            scaleXAnimation.Completed += (_, _) =>
            {
                if (SettingsOverlay.Visibility == Visibility.Visible)
                {
                    SettingsInputShield.Focus();
                    StartSpecialThanksAnimation();
                    StartSettingsOtaconAnimation();
                }
            };

            SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
            SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
            SettingsOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, translateXAnimation);
            SettingsOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, translateYAnimation);
            SettingsOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, borderAnimation);
            SettingsOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderColorAnimation);
            SettingsOverlayGlowBorder.BeginAnimation(OpacityProperty, glowAnimation);
        }

        private void SettingsInputShield_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            CloseSettingsOverlay();
            e.Handled = true;
        }

        private void SettingsInputShield_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
        }

        private void CloseSettingsOverlay_Click(object sender, RoutedEventArgs e)
        {
            CloseSettingsOverlay();
            e.Handled = true;
        }

        private void CloseSettingsOverlay()
        {
            if (SettingsOverlay.Visibility != Visibility.Visible || _settingsOverlayClosing)
                return;

            _settingsOverlayClosing = true;
            StopSpecialThanksAnimation();
            StopSettingsOtaconAnimation(hide: false);
            PlayMenuCloseSound();

            var duration = new Duration(TimeSpan.FromMilliseconds(175));
            var easing = new System.Windows.Media.Animation.SineEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
            };

            Point buttonPosition = SettingsButton.TranslatePoint(new Point(0, 0), MainLayoutRoot);
            double targetScaleX = SettingsButton.ActualWidth / SettingsOverlay.Width;
            double targetScaleY = SettingsButton.ActualHeight / SettingsOverlay.Height;
            double overlayLeft = SettingsOverlay.Margin.Left;
            double overlayTop = SettingsOverlay.Margin.Top;
            double targetTranslationX = buttonPosition.X + SettingsButton.ActualWidth / 2 -
                (overlayLeft + SettingsOverlay.Width / 2);
            double targetTranslationY = buttonPosition.Y + SettingsButton.ActualHeight / 2 -
                (overlayTop + SettingsOverlay.Height / 2);

            SettingsOverlayScale.ScaleX = targetScaleX;
            SettingsOverlayScale.ScaleY = targetScaleY;
            SettingsOverlayTranslation.X = targetTranslationX;
            SettingsOverlayTranslation.Y = targetTranslationY;
            SettingsOverlayOuterBorder.BorderThickness = new Thickness(1);
            SettingsOverlayBorderBrush.Color = Color.FromArgb(0xCC, 0, 0, 0);
            SettingsOverlayGlowBorder.Opacity = 0;

            var scaleXAnimation = CreateSettingsTransformAnimation(1, targetScaleX, duration, easing);
            var scaleYAnimation = CreateSettingsTransformAnimation(1, targetScaleY, duration, easing);
            var translateXAnimation = CreateSettingsTransformAnimation(0, targetTranslationX, duration, easing);
            var translateYAnimation = CreateSettingsTransformAnimation(0, targetTranslationY, duration, easing);
            var borderAnimation = new System.Windows.Media.Animation.ThicknessAnimation(
                new Thickness(2),
                new Thickness(1),
                duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            var borderColorAnimation = new System.Windows.Media.Animation.ColorAnimation(
                Color.FromRgb(0x94, 0xA4, 0x8A),
                Color.FromArgb(0xCC, 0, 0, 0),
                duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
            var glowAnimation = new System.Windows.Media.Animation.DoubleAnimation(1, 0, duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };

            scaleXAnimation.Completed += (_, _) =>
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                StopSettingsOtaconAnimation(hide: true);
                Keyboard.ClearFocus();

                SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                SettingsOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, null);
                SettingsOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                SettingsOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, null);
                SettingsOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                SettingsOverlayGlowBorder.BeginAnimation(OpacityProperty, null);
                SettingsOverlayScale.ScaleX = 1;
                SettingsOverlayScale.ScaleY = 1;
                SettingsOverlayTranslation.X = 0;
                SettingsOverlayTranslation.Y = 0;
                SettingsOverlayOuterBorder.BorderThickness = new Thickness(2);
                SettingsOverlayBorderBrush.Color = Color.FromRgb(0x94, 0xA4, 0x8A);
                SettingsOverlayGlowBorder.Opacity = 1;
                _settingsOverlayClosing = false;
            };

            SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnimation);
            SettingsOverlayScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnimation);
            SettingsOverlayTranslation.BeginAnimation(TranslateTransform.XProperty, translateXAnimation);
            SettingsOverlayTranslation.BeginAnimation(TranslateTransform.YProperty, translateYAnimation);
            SettingsOverlayOuterBorder.BeginAnimation(BorderThicknessProperty, borderAnimation);
            SettingsOverlayBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderColorAnimation);
            SettingsOverlayGlowBorder.BeginAnimation(OpacityProperty, glowAnimation);
        }

        private void StartSpecialThanksAnimation()
        {
            StopSpecialThanksAnimation();
            SpecialThanksScrollContent.Height = double.NaN;
            double contentHeight = 0;
            double trailingSpace = 0;
            foreach (UIElement row in SpecialThanksScrollContent.Children)
            {
                row.Measure(new Size(
                    SpecialThanksScrollViewport.ActualWidth,
                    double.PositiveInfinity));
                contentHeight += row.DesiredSize.Height;
                trailingSpace = row.DesiredSize.Height;
            }
            SpecialThanksScrollContent.Height = contentHeight;
            SpecialThanksScrollContent.Measure(new Size(
                SpecialThanksScrollViewport.ActualWidth,
                contentHeight));
            SpecialThanksScrollContent.UpdateLayout();
            double scrollDistance = Math.Max(
                0,
                contentHeight - SpecialThanksScrollViewport.ActualHeight + trailingSpace);

            if (scrollDistance <= 0)
                return;

            TimeSpan startPause = TimeSpan.FromSeconds(2.5);
            TimeSpan scrollTime = TimeSpan.FromSeconds(scrollDistance / 13.0);
            TimeSpan endPause = TimeSpan.FromSeconds(2.5);
            TimeSpan totalTime = startPause + scrollTime + endPause;
            var animation = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(totalTime),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            animation.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(
                0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(
                0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(startPause)));
            animation.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(
                -scrollDistance, System.Windows.Media.Animation.KeyTime.FromTimeSpan(startPause + scrollTime)));
            animation.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(
                -scrollDistance, System.Windows.Media.Animation.KeyTime.FromTimeSpan(totalTime)));
            SpecialThanksScrollTranslation.BeginAnimation(
                TranslateTransform.YProperty,
                animation,
                System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
        }

        private void StopSpecialThanksAnimation()
        {
            SpecialThanksScrollTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            SpecialThanksScrollTranslation.Y = 0;
        }

        private static System.Windows.Media.Animation.DoubleAnimation CreateSettingsTransformAnimation(
            double from,
            double to,
            Duration duration,
            System.Windows.Media.Animation.IEasingFunction easing)
        {
            return new System.Windows.Media.Animation.DoubleAnimation(from, to, duration)
            {
                EasingFunction = easing,
                FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
            };
        }

        private async void TrackListItem_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_analysisClosed)
                return;

            PlayMenuSelectSound();

            if (sender is FrameworkElement element &&
                element.DataContext is TrackItem track &&
                !string.IsNullOrWhiteSpace(track.FilePath))
            {
                if (IsActiveWaveform(track.FilePath))
                    return;

                HoveredTrackFileName = System.IO.Path.GetFileName(track.FilePath);
                HoveredTrackDuration = string.Empty;
                WaveformNoDataMessage.Visibility = Visibility.Collapsed;
                DurationValueGrid.Visibility = Visibility.Collapsed;
                QueueTrackTitleMarqueeUpdate(HoveredFilenameViewport);
                await AnalyzeHoveredTrackAsync(track);
            }
        }
    }
}
