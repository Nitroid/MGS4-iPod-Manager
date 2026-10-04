using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace iPodManager
{
    public partial class MainWindow
    {
        private bool _trackListPanelInitialized;
        private ScrollViewer? _trackTabsScrollViewer;
        private bool _trackTabsPointerDown;
        private bool _trackTabsDidDrag;
        private Point _trackTabsDragStartPoint;
        private double _trackTabsDragStartOffset;
        private ListBoxItem? _trackTabsPressedItem;
        private double _trackTabsDragVelocity;
        private double _trackTabsLastDragX;
        private DateTime _trackTabsLastDragTime;
        private DispatcherTimer? _trackTabsInertiaTimer;
        private double _trackTabsInertiaVelocity;
        private DateTime _trackTabsInertiaLastTick;
        private DispatcherTimer? _trackTabsEdgeScrollTimer;
        private double _trackTabsEdgeScrollVelocity;
        private DateTime _trackTabsEdgeScrollLastTick;
        private bool _suppressTrackTabsEdgeAutoScroll;

        private bool _trackItemsPointerDown;
        private bool _trackItemsDidDrag;
        private Point _trackItemsDragStartPoint;
        private double _trackItemsDragStartOffset;
        private TrackItem? _trackItemsPressedItem;
        private int _trackListLayoutVersion;
        private bool _suppressTrackTabSelectionChanged;
        private int _startupTrackBindingCount;

        private void TrackListPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (_trackListPanelInitialized)
                return;

            _trackListPanelInitialized = true;

            // Keep the divider gap synchronized with layout changes and horizontal tab scrolling.
            TrackTabsDividerCrisp.SizeChanged += TrackTabsDivider_SizeChanged;
            TrackTabsList.AddHandler(
                ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(TrackTabsList_ScrollChanged));

            if (CategoryList.Items.Count > 0)
            {
                // Selecting DEFAULT is application initialization, not a user
                // action, so it must not trigger the category-selection sound.
                bool selectionWillChange = CategoryList.SelectedIndex != 0;
                _suppressCategorySelectionSound = true;
                try
                {
                    CategoryList.SelectedIndex = 0;
                }
                finally
                {
                    _suppressCategorySelectionSound = false;
                }

                // A changed selection invokes ShowSelectedCategory through the
                // handler synchronously. If DEFAULT was already selected before
                // Loaded, initialize its list explicitly instead.
                if (!selectionWillChange)
                    ShowSelectedCategory();
            }
            else
            {
                ShowSelectedCategory();
            }
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_trackListPanelInitialized)
                return;

            if (!_suppressCategorySelectionSound)
                PlayCategorySelectSound();
            ShowSelectedCategory();
        }

        private void ShowSelectedCategory()
        {
            var bindingTimer = Stopwatch.StartNew();

            if (!(CategoryList.SelectedItem is CategoryItem category))
            {
                TrackItemsList.ItemsSource = null;
                NoTrackDataMessage.Visibility = Visibility.Visible;
                QueueFinalTrackListLayout();
                return;
            }

            IReadOnlyList<TrackCollectionItem> tabs = GetCachedCategoryTabs(category);
            _suppressTrackTabSelectionChanged = true;
            try
            {
                TrackTabsList.ItemsSource = tabs;
                TrackTabsList.SelectedIndex = tabs.Count > 0 ? 0 : -1;
            }
            finally
            {
                _suppressTrackTabSelectionChanged = false;
            }

            if (tabs.Count > 0)
            {
                TrackItemsList.ItemsSource = tabs[0].Tracks;
                NoTrackDataMessage.Visibility = tabs[0].Tracks.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                TrackItemsList.ItemsSource = null;
                NoTrackDataMessage.Visibility = Visibility.Visible;
            }

            TrackItemsScrollViewer?.ScrollToVerticalOffset(0);

            _trackTabsScrollViewer = GetTrackTabsScrollViewer();
            _trackTabsScrollViewer?.ScrollToHorizontalOffset(0);

            QueueTrackTabVisualUpdate();
            QueueFinalTrackListLayout();
            if (_startupTimingActive)
            {
                _startupTrackBindingCount++;
                int trackCount = tabs.Count > 0 ? tabs[0].Tracks.Count : 0;
                AppLog.Info($"startup.ui.track_binding pass={_startupTrackBindingCount} tabs={tabs.Count} tracks={trackCount} elapsed_ms={bindingTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
            }
        }

        private void TrackTabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrackItemsList == null || _suppressTrackTabSelectionChanged)
                return;


            if (TrackTabsList.SelectedItem is TrackCollectionItem collection)
            {
                TrackItemsList.ItemsSource = collection.Tracks;
                NoTrackDataMessage.Visibility = collection.Tracks.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                TrackItemsList.ItemsSource = null;
                NoTrackDataMessage.Visibility = Visibility.Visible;
            }

            // Each tab opens at the top of its own track list.
            TrackItemsScrollViewer?.ScrollToVerticalOffset(0);

            QueueFinalTrackListLayout();
        }

        private void QueueFinalTrackListLayout()
        {
            int layoutVersion = ++_trackListLayoutVersion;
            var queueTimer = Stopwatch.StartNew();

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (layoutVersion != _trackListLayoutVersion || TrackListPanel == null)
                        return;

                    TrackListPanel.UpdateLayout();
                    UpdateTrackItemEdgeFade();
                    UpdateTrackTabDividerClip();
                    UpdateTrackTabEdgeFade();
                    TrackListPanel.Opacity = 1;
                    if (_startupTimingActive && _startupLibraryResultsApplied &&
                        !_startupLibraryReadyLogged)
                    {
                        _startupProgress?.FinalLayoutPending();
                        _startupProgress?.Ready();
                        _startupLibraryReadyLogged = true;
                        _startupTimingActive = false;
                        AppLog.Info($"startup.ui.track_layout elapsed_ms={queueTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
                        AppLog.Info($"startup.library.ready total_ms={App.StartupStopwatch.ElapsedMilliseconds} categories={CategoryItems.Count} tabs={TrackTabsList.Items.Count} tracks={TrackItemsList.Items.Count} thread={Environment.CurrentManagedThreadId}");
                        Dispatcher.BeginInvoke(
                            new Action(StopStartupLoadingState),
                            DispatcherPriority.Background);
                    }
                }),
                DispatcherPriority.Loaded);
        }

        private void SelectAllTracks_Click(object sender, RoutedEventArgs e)
        {
            PlayTrackToggleSound();
            SetSelectedTrackChecks(true);
        }

        private void SelectNoTracks_Click(object sender, RoutedEventArgs e)
        {
            PlayTrackToggleSound();
            SetSelectedTrackChecks(false);
        }

        private void SetSelectedTrackChecks(bool isChecked)
        {
            if (!(TrackTabsList.SelectedItem is TrackCollectionItem collection))
                return;

            foreach (TrackItem track in collection.Tracks)
                track.IsChecked = isChecked;
        }

        private void TrackTabsDivider_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueTrackTabVisualUpdate();
        }

        private void TrackItemsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 || e.ViewportHeightChange != 0 || e.ExtentHeightChange != 0)
                QueueTrackItemEdgeFadeUpdate();
        }

        private void QueueTrackItemEdgeFadeUpdate()
        {
            Dispatcher.BeginInvoke(
                new Action(UpdateTrackItemEdgeFade),
                DispatcherPriority.Loaded);
        }

        private void UpdateTrackItemEdgeFade()
        {
            if (TrackItemsScrollViewer == null)
                return;

            // Apply the edge fade only to the viewport content so the scrollbar
            // and its rail remain fully opaque from top to bottom.
            ScrollContentPresenter? contentPresenter =
                FindVisualChild<ScrollContentPresenter>(TrackItemsScrollViewer);

            if (contentPresenter == null)
                return;

            // The default ScrollViewer template reserves a wider viewport boundary
            // than the custom scrollbar requires. Extend into that reserved area
            // only while the scrollbar is visible; otherwise the negative margin
            // would push track rows beyond the panel's right border.
            bool verticalScrollbarVisible =
                TrackItemsScrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible;

            contentPresenter.Margin = verticalScrollbarVisible
                ? new Thickness(0, 0, -6, 0)
                : new Thickness(0);

            const double edgeTolerance = 0.5;

            double hiddenHeightAbove = TrackItemsScrollViewer.VerticalOffset;
            double hiddenHeightBelow =
                TrackItemsScrollViewer.ScrollableHeight - TrackItemsScrollViewer.VerticalOffset;

            // Track rows have a 5px top margin. Do not show the top fade merely
            // because that empty spacing has moved out of view; an actual track
            // must be clipped above the viewport.
            bool hasHiddenTracksAbove = hiddenHeightAbove > edgeTolerance;
            bool hasHiddenTracksBelow = hiddenHeightBelow > edgeTolerance;

            if (!hasHiddenTracksAbove && !hasHiddenTracksBelow)
            {
                contentPresenter.OpacityMask = null;
                TrackItemsList.OpacityMask = null;
                return;
            }

            double viewportHeight = TrackItemsScrollViewer.ViewportHeight;
            if (viewportHeight <= 0 ||
                double.IsNaN(viewportHeight) ||
                double.IsInfinity(viewportHeight))
            {
                viewportHeight = TrackItemsScrollViewer.ActualHeight;
            }

            const double fadeEdgeInset = 10;
            double maskedViewportHeight = Math.Max(
                1,
                viewportHeight - fadeEdgeInset * 2);
            double fadeRatio = Math.Min(0.15, 24 / maskedViewportHeight);
            var fade = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(
                    0,
                    TrackItemsScrollViewer.VerticalOffset + fadeEdgeInset),
                EndPoint = new Point(
                    0,
                    TrackItemsScrollViewer.VerticalOffset +
                        viewportHeight -
                        fadeEdgeInset)
            };

            if (hasHiddenTracksAbove)
            {
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.00));
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), fadeRatio * 0.35));
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF), fadeRatio * 0.7));
                fade.GradientStops.Add(new GradientStop(Colors.White, fadeRatio));
            }
            else
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 0.00));
            }

            if (hasHiddenTracksBelow)
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 1 - fadeRatio));
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF), 1 - fadeRatio * 0.7));
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1 - fadeRatio * 0.35));
                fade.GradientStops.Add(new GradientStop(
                    Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
            }
            else
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 1.00));
            }

            contentPresenter.OpacityMask = null;
            TrackItemsList.OpacityMask = fade;
        }

        private void TrackItemsScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            // Let interactive controls handle their own clicks and dragging.
            if (e.OriginalSource is DependencyObject source &&
                (FindVisualParent<ScrollBar>(source) != null ||
                 FindVisualParent<ToggleButton>(source) != null))
            {
                return;
            }

            TrackItem? pressedItem = e.OriginalSource is DependencyObject pressedSource
                ? FindTrackItem(pressedSource)
                : null;
            if (pressedItem == null)
            {
                DragMove();
                e.Handled = true;
                return;
            }

            _trackItemsPointerDown = true;
            _trackItemsDidDrag = false;
            _trackItemsDragStartPoint = e.GetPosition(TrackItemsScrollViewer);
            _trackItemsDragStartOffset = TrackItemsScrollViewer.VerticalOffset;
            _trackItemsPressedItem = pressedItem;

            TrackItemsScrollViewer.CaptureMouse();

            // Prevent the window-level drag handler from taking over this gesture.
            e.Handled = true;
        }

        private void TrackItemsScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_trackItemsPointerDown || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point currentPoint = e.GetPosition(TrackItemsScrollViewer);
            double verticalDelta = currentPoint.Y - _trackItemsDragStartPoint.Y;

            if (!_trackItemsDidDrag &&
                Math.Abs(verticalDelta) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _trackItemsDidDrag = true;
            }

            if (!_trackItemsDidDrag)
                return;

            TrackItemsScrollViewer.ScrollToVerticalOffset(
                _trackItemsDragStartOffset - verticalDelta);

            e.Handled = true;
        }

        private void TrackItemsScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_trackItemsPointerDown || e.ChangedButton != MouseButton.Left)
                return;

            TrackItem? pressedItem = _trackItemsPressedItem;
            bool shouldToggle = !_trackItemsDidDrag && pressedItem != null;

            ResetTrackItemsDragState();

            if (shouldToggle)
            {
                pressedItem!.IsChecked = !pressedItem.IsChecked;
                PlayTrackToggleSound();
            }

            e.Handled = true;
        }

        private void TrackCheckToggle_Click(object sender, RoutedEventArgs e)
        {
            PlayTrackToggleSound();
        }

        private void TrackItemsScrollViewer_LostMouseCapture(object sender, MouseEventArgs e)
        {
            ResetTrackItemsDragState(releaseCapture: false);
        }

        private void ResetTrackItemsDragState(bool releaseCapture = true)
        {
            _trackItemsPointerDown = false;
            _trackItemsDidDrag = false;
            _trackItemsPressedItem = null;

            if (releaseCapture && TrackItemsScrollViewer.IsMouseCaptured)
                TrackItemsScrollViewer.ReleaseMouseCapture();
        }

        private void TrackTabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            StopTrackTabsInertia();
            StopTrackTabsEdgeAutoScroll();
            _suppressTrackTabsEdgeAutoScroll = true;
            _trackTabsScrollViewer = GetTrackTabsScrollViewer();
            _trackTabsPointerDown = true;
            _trackTabsDidDrag = false;
            _trackTabsDragStartPoint = e.GetPosition(TrackTabsList);
            _trackTabsDragStartOffset = _trackTabsScrollViewer?.HorizontalOffset ?? 0;
            _trackTabsDragVelocity = 0;
            _trackTabsLastDragX = _trackTabsDragStartPoint.X;
            _trackTabsLastDragTime = DateTime.UtcNow;
            _trackTabsPressedItem =
                ItemsControl.ContainerFromElement(TrackTabsList, e.OriginalSource as DependencyObject)
                as ListBoxItem;

            TrackTabsList.CaptureMouse();

            // Suppress the ListBox's default mouse-down selection. A normal click is
            // applied manually on mouse-up; a drag pans the tab strip without
            // accidentally changing the selected tab.
            e.Handled = true;
        }

        private void TrackTabsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            Point currentPoint = e.GetPosition(TrackTabsList);

            if (!_trackTabsPointerDown)
                UpdateTrackTabsEdgeAutoScroll(currentPoint);

            if (!_trackTabsPointerDown || e.LeftButton != MouseButtonState.Pressed)
                return;

            double horizontalDelta = currentPoint.X - _trackTabsDragStartPoint.X;

            if (!_trackTabsDidDrag &&
                Math.Abs(horizontalDelta) >= SystemParameters.MinimumHorizontalDragDistance)
            {
                _trackTabsDidDrag = true;
            }

            if (!_trackTabsDidDrag)
                return;

            _trackTabsScrollViewer = GetTrackTabsScrollViewer();

            if (_trackTabsScrollViewer != null)
            {
                _trackTabsScrollViewer.ScrollToHorizontalOffset(
                    _trackTabsDragStartOffset - horizontalDelta);
            }

            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - _trackTabsLastDragTime).TotalSeconds;
            if (elapsedSeconds > 0.001)
            {
                double instantaneousVelocity =
                    -((currentPoint.X - _trackTabsLastDragX) / elapsedSeconds);
                instantaneousVelocity = Math.Clamp(
                    instantaneousVelocity,
                    -3000,
                    3000);
                _trackTabsDragVelocity =
                    _trackTabsDragVelocity * 0.65 + instantaneousVelocity * 0.35;
                _trackTabsLastDragX = currentPoint.X;
                _trackTabsLastDragTime = now;
            }

            e.Handled = true;
        }

        private void UpdateTrackTabsEdgeAutoScroll(Point pointerPosition)
        {
            if (_suppressTrackTabsEdgeAutoScroll ||
                _trackTabsInertiaTimer?.IsEnabled == true)
            {
                StopTrackTabsEdgeAutoScroll();
                return;
            }

            ScrollViewer? scrollViewer = GetTrackTabsScrollViewer();
            if (scrollViewer == null ||
                scrollViewer.ScrollableWidth <= 0.5 ||
                TrackTabsList.ActualWidth <= 0)
            {
                StopTrackTabsEdgeAutoScroll();
                return;
            }

            const double edgeZoneWidth = 120;
            const double maximumPixelsPerSecond = 280;
            double velocity = 0;

            if (pointerPosition.X >= 0 && pointerPosition.X < edgeZoneWidth &&
                scrollViewer.HorizontalOffset > 0)
            {
                double strength = 1 - pointerPosition.X / edgeZoneWidth;
                velocity = -maximumPixelsPerSecond * strength;
            }
            else if (pointerPosition.X <= TrackTabsList.ActualWidth &&
                     pointerPosition.X > TrackTabsList.ActualWidth - edgeZoneWidth &&
                     scrollViewer.HorizontalOffset < scrollViewer.ScrollableWidth)
            {
                double distanceFromRight = TrackTabsList.ActualWidth - pointerPosition.X;
                double strength = 1 - distanceFromRight / edgeZoneWidth;
                velocity = maximumPixelsPerSecond * strength;
            }

            if (Math.Abs(velocity) < 0.5)
            {
                StopTrackTabsEdgeAutoScroll();
                return;
            }

            StopTrackTabsInertia();
            _trackTabsEdgeScrollVelocity = velocity;
            _trackTabsEdgeScrollLastTick = DateTime.UtcNow;

            if (_trackTabsEdgeScrollTimer == null)
            {
                _trackTabsEdgeScrollTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(16),
                    DispatcherPriority.Render,
                    TrackTabsEdgeScrollTimer_Tick,
                    Dispatcher);
                _trackTabsEdgeScrollTimer.Stop();
            }

            if (!_trackTabsEdgeScrollTimer.IsEnabled)
                _trackTabsEdgeScrollTimer.Start();
        }

        private void TrackTabsEdgeScrollTimer_Tick(object? sender, EventArgs e)
        {
            if (!TrackTabsList.IsMouseOver || _trackTabsPointerDown)
            {
                StopTrackTabsEdgeAutoScroll();
                return;
            }

            ScrollViewer? scrollViewer = GetTrackTabsScrollViewer();
            if (scrollViewer == null)
            {
                StopTrackTabsEdgeAutoScroll();
                return;
            }

            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = Math.Min(
                0.1,
                (now - _trackTabsEdgeScrollLastTick).TotalSeconds);
            _trackTabsEdgeScrollLastTick = now;

            double targetOffset = Math.Clamp(
                scrollViewer.HorizontalOffset +
                    _trackTabsEdgeScrollVelocity * elapsedSeconds,
                0,
                scrollViewer.ScrollableWidth);
            scrollViewer.ScrollToHorizontalOffset(targetOffset);

            if ((_trackTabsEdgeScrollVelocity < 0 && targetOffset <= 0) ||
                (_trackTabsEdgeScrollVelocity > 0 &&
                 targetOffset >= scrollViewer.ScrollableWidth))
            {
                StopTrackTabsEdgeAutoScroll();
            }
        }

        private void StopTrackTabsEdgeAutoScroll()
        {
            _trackTabsEdgeScrollVelocity = 0;
            _trackTabsEdgeScrollTimer?.Stop();
        }

        private void TrackTabsList_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_trackTabsPointerDown &&
                _trackTabsInertiaTimer?.IsEnabled != true)
            {
                _suppressTrackTabsEdgeAutoScroll = false;
            }

            StopTrackTabsEdgeAutoScroll();
            TooltipTarget_MouseLeave(sender, e);
        }

        private void TrackTabsList_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_trackTabsPointerDown &&
                _trackTabsInertiaTimer?.IsEnabled != true)
            {
                _suppressTrackTabsEdgeAutoScroll = false;
            }

            TooltipTarget_MouseEnter(sender, e);
        }

        private void TrackTabsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_trackTabsPointerDown || e.ChangedButton != MouseButton.Left)
                return;

            bool wasDrag = _trackTabsDidDrag;
            double releaseVelocity = _trackTabsDragVelocity;
            ListBoxItem? pressedItem = _trackTabsPressedItem;

            ResetTrackTabsDragState();

            if (!wasDrag && pressedItem != null)
            {
                object item = TrackTabsList.ItemContainerGenerator.ItemFromContainer(pressedItem);

                if (item != DependencyProperty.UnsetValue)
                {
                    TrackTabsList.SelectedItem = item;
                    PlayCompleteShortSound();
                }
            }
            else if (wasDrag)
            {
                StartTrackTabsInertia(releaseVelocity);
            }

            e.Handled = true;
        }

        private void TrackTabsList_LostMouseCapture(object sender, MouseEventArgs e)
        {
            ResetTrackTabsDragState(releaseCapture: false);
        }

        private void ResetTrackTabsDragState(bool releaseCapture = true)
        {
            _trackTabsPointerDown = false;
            _trackTabsDidDrag = false;
            _trackTabsPressedItem = null;

            if (releaseCapture && TrackTabsList.IsMouseCaptured)
                TrackTabsList.ReleaseMouseCapture();
        }

        private void StartTrackTabsInertia(double releaseVelocity)
        {
            if (Math.Abs(releaseVelocity) < 80)
                return;

            _trackTabsInertiaVelocity = releaseVelocity;
            _trackTabsInertiaLastTick = DateTime.UtcNow;

            if (_trackTabsInertiaTimer == null)
            {
                _trackTabsInertiaTimer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(16),
                    DispatcherPriority.Render,
                    TrackTabsInertiaTimer_Tick,
                    Dispatcher);
                _trackTabsInertiaTimer.Stop();
            }

            _trackTabsInertiaTimer.Start();
        }

        private void TrackTabsInertiaTimer_Tick(object? sender, EventArgs e)
        {
            ScrollViewer? scrollViewer = GetTrackTabsScrollViewer();
            if (scrollViewer == null || _trackTabsPointerDown)
            {
                StopTrackTabsInertia();
                return;
            }

            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = Math.Min(
                0.05,
                (now - _trackTabsInertiaLastTick).TotalSeconds);
            _trackTabsInertiaLastTick = now;

            double targetOffset = Math.Clamp(
                scrollViewer.HorizontalOffset +
                    _trackTabsInertiaVelocity * elapsedSeconds,
                0,
                scrollViewer.ScrollableWidth);
            scrollViewer.ScrollToHorizontalOffset(targetOffset);

            // Frame-rate-independent kinetic friction gives a quick flick useful
            // travel while retaining a smooth, controllable deceleration.
            _trackTabsInertiaVelocity *= Math.Pow(0.95, elapsedSeconds * 60);

            bool reachedEdge =
                (_trackTabsInertiaVelocity < 0 && targetOffset <= 0) ||
                (_trackTabsInertiaVelocity > 0 &&
                 targetOffset >= scrollViewer.ScrollableWidth);
            if (reachedEdge || Math.Abs(_trackTabsInertiaVelocity) < 20)
                StopTrackTabsInertia();
        }

        private void StopTrackTabsInertia()
        {
            _trackTabsInertiaVelocity = 0;
            _trackTabsInertiaTimer?.Stop();
        }

        private ScrollViewer? GetTrackTabsScrollViewer()
        {
            if (_trackTabsScrollViewer != null)
                return _trackTabsScrollViewer;

            TrackTabsList.ApplyTemplate();
            _trackTabsScrollViewer = FindVisualChild<ScrollViewer>(TrackTabsList);
            return _trackTabsScrollViewer;
        }

        private static T? FindVisualParent<T>(DependencyObject? child)
            where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match)
                    return match;

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private static TrackItem? FindTrackItem(DependencyObject? child)
        {
            while (child != null)
            {
                if (child is FrameworkElement element &&
                    element.DataContext is TrackItem trackItem)
                {
                    return trackItem;
                }

                child = VisualTreeHelper.GetParent(child);
            }

            return null;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent)
            where T : DependencyObject
        {
            if (parent == null)
                return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is T match)
                    return match;

                T? nestedMatch = FindVisualChild<T>(child);

                if (nestedMatch != null)
                    return nestedMatch;
            }

            return null;
        }

        private void TrackTabsList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.HorizontalChange != 0 || e.ViewportWidthChange != 0 || e.ExtentWidthChange != 0)
                QueueTrackTabVisualUpdate();
        }

        private void QueueTrackTabVisualUpdate()
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    UpdateTrackTabDividerClip();
                    UpdateTrackTabEdgeFade();
                }),
                DispatcherPriority.Loaded);
        }

        private void UpdateTrackTabEdgeFade()
        {
            if (TrackTabsList == null)
                return;

            ScrollViewer? scrollViewer = GetTrackTabsScrollViewer();

            if (scrollViewer == null || scrollViewer.ScrollableWidth <= 0.5)
            {
                TrackTabsList.OpacityMask = null;
                return;
            }

            const double edgeTolerance = 0.5;

            // The tab style carries a 4px trailing margin. Do not show a fade merely
            // because that empty spacing extends past the viewport; a tab itself must
            // actually be clipped on that side.
            double firstTabLeftMargin = 0;
            double lastTabRightMargin = 4;

            if (TrackTabsList.Items.Count > 0)
            {
                if (TrackTabsList.ItemContainerGenerator.ContainerFromIndex(0)
                    is FrameworkElement firstTab)
                {
                    firstTabLeftMargin = firstTab.Margin.Left;
                }

                if (TrackTabsList.ItemContainerGenerator.ContainerFromIndex(TrackTabsList.Items.Count - 1)
                    is FrameworkElement lastTab)
                {
                    lastTabRightMargin = lastTab.Margin.Right;
                }
            }

            double hiddenWidthOnLeft = scrollViewer.HorizontalOffset;
            double hiddenWidthOnRight = scrollViewer.ScrollableWidth - scrollViewer.HorizontalOffset;

            bool hasHiddenTabsOnLeft =
                hiddenWidthOnLeft > firstTabLeftMargin + edgeTolerance;
            bool hasHiddenTabsOnRight =
                hiddenWidthOnRight > lastTabRightMargin + edgeTolerance;

            if (!hasHiddenTabsOnLeft && !hasHiddenTabsOnRight)
            {
                TrackTabsList.OpacityMask = null;
                return;
            }

            var fade = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            // Mirror the approved strong right-edge fade on the left whenever
            // scrolling has obscured tabs beyond that side of the viewport.
            if (hasHiddenTabsOnLeft)
            {
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.00));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.07));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 0.14));
                fade.GradientStops.Add(new GradientStop(Colors.White, 0.22));
            }
            else
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 0.00));
            }

            if (hasHiddenTabsOnRight)
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 0.78));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 0.86));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 0.93));
                fade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
            }
            else
            {
                fade.GradientStops.Add(new GradientStop(Colors.White, 1.00));
            }

            TrackTabsList.OpacityMask = fade;
        }

        private void QueueTrackTabDividerClipUpdate()
        {
            Dispatcher.BeginInvoke(
                new Action(UpdateTrackTabDividerClip),
                DispatcherPriority.Loaded);
        }

        private void UpdateTrackTabDividerClip()
        {
            if (TrackTabsDividerCrisp == null || TrackTabsDividerSoft == null || TrackTabsList == null)
                return;

            if (!(TrackTabsList.ItemContainerGenerator.ContainerFromItem(TrackTabsList.SelectedItem)
                  is FrameworkElement selectedTab))
            {
                TrackTabsDividerCrisp.Clip = null;
                TrackTabsDividerSoft.Clip = null;
                return;
            }

            double dividerWidth = TrackTabsDividerCrisp.ActualWidth;
            double dividerHeight = TrackTabsDividerCrisp.ActualHeight;

            if (dividerWidth <= 0 || dividerHeight <= 0)
                return;

            Point selectedOrigin;

            try
            {
                selectedOrigin = selectedTab.TransformToVisual(TrackTabsDividerCrisp)
                                            .Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                // The tab container can briefly be detached while the category is rebuilding.
                QueueTrackTabDividerClipUpdate();
                return;
            }

            // The ListBoxItem's ActualWidth excludes its 4px right margin, so the
            // divider remains visible in the gap between tabs and is hidden only
            // beneath the active tab itself.
            double gapLeft = Math.Max(0, selectedOrigin.X);
            double gapRight = Math.Min(dividerWidth, selectedOrigin.X + selectedTab.ActualWidth);

            if (gapRight <= gapLeft)
            {
                TrackTabsDividerCrisp.Clip = null;
                TrackTabsDividerSoft.Clip = null;
                return;
            }

            Geometry clip = CreateDividerClip(dividerWidth, dividerHeight, gapLeft, gapRight);
            TrackTabsDividerCrisp.Clip = clip;
            TrackTabsDividerSoft.Clip = clip.Clone();
        }

        private static Geometry CreateDividerClip(
            double width,
            double height,
            double gapLeft,
            double gapRight)
        {
            var geometry = new GeometryGroup();

            if (gapLeft > 0)
                geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, gapLeft, height)));

            if (gapRight < width)
            {
                geometry.Children.Add(
                    new RectangleGeometry(
                        new Rect(gapRight, 0, width - gapRight, height)));
            }

            return geometry;
        }
    }
}
