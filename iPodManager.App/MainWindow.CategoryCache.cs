using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace iPodManager
{
    public partial class MainWindow
    {
        private static readonly TimeSpan CategoryRefreshDebounce =
            TimeSpan.FromMilliseconds(750);

        private readonly Dictionary<CategoryItem, IReadOnlyList<TrackCollectionItem>>
            _categoryTabCache = new();
        private readonly Dictionary<string, FileSystemWatcher> _categoryWatchers =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _categoryRefreshTokens =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _categoryRefreshGate = new(1, 1);
        private bool _suppressCategorySelectionSound;

        private void InitializeCategoryCachingAndWatchers()
        {
            var tabsTimer = Stopwatch.StartNew();
            foreach (CategoryItem category in CategoryItems)
                _categoryTabCache[category] = BuildCategoryTabs(category);
            int tabCount = _categoryTabCache.Values.Sum(tabs => tabs.Count);
            AppLog.Info($"startup.ui.tabs count={tabCount} elapsed_ms={tabsTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");

            var watchersTimer = Stopwatch.StartNew();
            RegisterCategoryWatcher("DEFAULT", LibraryDiscovery.FindDefaultBankDirectory(_paths),
                () => LibraryDiscovery.LoadDefaultCategory(
                    _paths, _defaultCatalogBytes, _deploymentManifest, _deploymentError));

            RegisterCategoryWatcher(
                "PODCASTS",
                _paths.PodcastContent,
                () => LibraryDiscovery.LoadFileBackedCategory(
                    _paths, _deployedTracks, "PODCASTS", "podcasts",
                    "Assets/Images/icon_signal_intercepter.png"));
            RegisterCategoryWatcher(
                "CUSTOM",
                _paths.CustomContent,
                () => LibraryDiscovery.LoadFileBackedCategory(
                    _paths, _deployedTracks, "CUSTOM", "custom",
                    "Assets/Images/icon_syringe.png"));

            Closed += (_, _) => DisposeCategoryWatchers();
            AppLog.Info($"startup.library.watchers count={_categoryWatchers.Count} elapsed_ms={watchersTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
        }

        private IReadOnlyList<TrackCollectionItem> GetCachedCategoryTabs(CategoryItem category)
        {
            if (!_categoryTabCache.TryGetValue(category, out IReadOnlyList<TrackCollectionItem>? tabs))
            {
                tabs = BuildCategoryTabs(category);
                _categoryTabCache[category] = tabs;
            }

            return tabs;
        }

        private static IReadOnlyList<TrackCollectionItem> BuildCategoryTabs(CategoryItem category)
        {
            var allTracks = new TrackCollectionItem("ALL");
            IEnumerable<TrackItem> tracks = category.TrackCollections
                .SelectMany(collection => collection.Tracks)
                .OrderBy(track => track.Name, TrackDisplayOrder.NaturalTitle);
            foreach (TrackItem track in tracks)
                allTracks.Tracks.Add(track);

            var tabs = new List<TrackCollectionItem>(category.TrackCollections.Count + 1)
            {
                allTracks
            };
            foreach (TrackCollectionItem collection in category.TrackCollections)
            {
                var album = new TrackCollectionItem(collection.Name);
                foreach (TrackItem track in TrackDisplayOrder.Album(collection.Tracks,
                    track => track.DiscNumber, track => track.TrackNumber, track => track.Name))
                    album.Tracks.Add(track);
                tabs.Add(album);
            }
            return tabs;
        }

        private void RegisterCategoryWatcher(
            string categoryName,
            string? directoryPath,
            Func<CategoryItem> loader)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                return;

            var watcher = new FileSystemWatcher(directoryPath)
            {
                IncludeSubdirectories = true,
                Filter = "*",
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.Size |
                               NotifyFilters.LastWrite |
                               NotifyFilters.CreationTime
            };

            FileSystemEventHandler changed = (_, _) =>
                QueueCategoryRefresh(categoryName, loader);
            RenamedEventHandler renamed = (_, _) =>
                QueueCategoryRefresh(categoryName, loader);

            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += renamed;
            watcher.Error += (_, _) => QueueCategoryRefresh(categoryName, loader);
            watcher.EnableRaisingEvents = true;
            _categoryWatchers[categoryName] = watcher;
        }

        private void QueueCategoryRefresh(string categoryName, Func<CategoryItem> loader)
        {
            if (_transferCancellation != null || _transferRecoveryRequired ||
                Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
                ScheduleCategoryRefresh(categoryName, loader)));
        }

        private void ScheduleCategoryRefresh(string categoryName, Func<CategoryItem> loader)
        {
            if (_transferCancellation != null || _transferRecoveryRequired)
                return;
            if (_categoryRefreshTokens.Remove(categoryName, out CancellationTokenSource? prior))
            {
                prior.Cancel();
                prior.Dispose();
            }

            var current = new CancellationTokenSource();
            _categoryRefreshTokens[categoryName] = current;
            _ = RefreshCategoryAfterQuietPeriodAsync(categoryName, loader, current);
        }

        private async Task RefreshCategoryAfterQuietPeriodAsync(
            string categoryName,
            Func<CategoryItem> loader,
            CancellationTokenSource refreshToken)
        {
            try
            {
                await Task.Delay(CategoryRefreshDebounce, refreshToken.Token);
                await _categoryRefreshGate.WaitAsync(refreshToken.Token);
                CategoryItem refreshed;
                try { refreshed = await Task.Run(loader, refreshToken.Token); }
                finally { _categoryRefreshGate.Release(); }
                await Dispatcher.InvokeAsync(() =>
                    ApplyRefreshedCategory(categoryName, refreshed, refreshToken));
            }
            catch (OperationCanceledException)
            {
                // A newer event restarted the quiet-period timer.
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException)
            {
                AppLog.Warn($"Library refresh for {categoryName} failed: {exception}");
                // Files can still be locked briefly after the watcher reports a
                // completed write. A later file event will schedule another pass.
            }
        }

        private void ApplyRefreshedCategory(
            string categoryName,
            CategoryItem refreshed,
            CancellationTokenSource refreshToken)
        {
            if (_transferCancellation != null || _transferRecoveryRequired)
                return;
            if (!_categoryRefreshTokens.TryGetValue(categoryName, out CancellationTokenSource? active) ||
                active != refreshToken)
            {
                return;
            }

            _categoryRefreshTokens.Remove(categoryName);
            refreshToken.Dispose();

            int index = -1;
            for (int candidateIndex = 0; candidateIndex < CategoryItems.Count; candidateIndex++)
            {
                if (string.Equals(
                    CategoryItems[candidateIndex].Name,
                    categoryName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    index = candidateIndex;
                    break;
                }
            }
            if (index < 0)
                return;

            CategoryItem previous = CategoryItems[index];
            PreserveTrackSelections(previous, refreshed);
            bool wasSelected = ReferenceEquals(CategoryList.SelectedItem, previous);

            _categoryTabCache.Remove(previous);
            _categoryTabCache[refreshed] = BuildCategoryTabs(refreshed);

            _suppressCategorySelectionSound = true;
            try
            {
                CategoryItems[index] = refreshed;
                if (wasSelected)
                {
                    CategoryList.SelectedIndex = index;
                    ShowSelectedCategory();
                }
            }
            finally
            {
                _suppressCategorySelectionSound = false;
            }

            CategoriesTotalSizeText = LibraryDiscovery.FormatFileSize(CategoryItems.Sum(category => category.SizeBytes));
        }

        private static void PreserveTrackSelections(CategoryItem previous, CategoryItem refreshed)
        {
            var selectedKeys = new HashSet<string>(
                previous.TrackCollections
                    .SelectMany(collection => collection.Tracks)
                    .Where(track => track.IsChecked && !string.IsNullOrWhiteSpace(track.StableKey))
                    .Select(track => track.StableKey),
                StringComparer.OrdinalIgnoreCase);

            var uncheckedKeys = new HashSet<string>(
                previous.TrackCollections.SelectMany(collection => collection.Tracks)
                    .Where(track => !track.IsChecked && !string.IsNullOrWhiteSpace(track.StableKey))
                    .Select(track => track.StableKey), StringComparer.OrdinalIgnoreCase);

            foreach (TrackItem track in refreshed.TrackCollections.SelectMany(collection => collection.Tracks))
            {
                if (selectedKeys.Contains(track.StableKey)) track.IsChecked = true;
                else if (uncheckedKeys.Contains(track.StableKey)) track.IsChecked = false;
            }
        }

        private void DisposeCategoryWatchers()
        {
            foreach (CancellationTokenSource token in _categoryRefreshTokens.Values)
            {
                token.Cancel();
                token.Dispose();
            }
            _categoryRefreshTokens.Clear();

            foreach (FileSystemWatcher watcher in _categoryWatchers.Values)
                watcher.Dispose();
            _categoryWatchers.Clear();
            _categoryRefreshGate.Dispose();
        }

        private void SuspendCategoryWatchers()
        {
            foreach (FileSystemWatcher watcher in _categoryWatchers.Values)
                watcher.EnableRaisingEvents = false;
            foreach (CancellationTokenSource token in _categoryRefreshTokens.Values)
            {
                token.Cancel();
                token.Dispose();
            }
            _categoryRefreshTokens.Clear();
        }

        private void ResumeCategoryWatchers()
        {
            foreach (FileSystemWatcher watcher in _categoryWatchers.Values)
                watcher.EnableRaisingEvents = true;
        }
    }
}
