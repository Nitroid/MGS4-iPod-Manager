using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace iPodManager;

internal static class LibraryDiscovery
{
    internal static CategoryItem[] Discover(
        GamePaths paths, byte[] catalogBytes, DeploymentManifest? manifest,
        IReadOnlyList<DeployedTrack> deployedTracks, string? deploymentError,
        CancellationToken cancellationToken, Action<int, int>? progress = null)
    {
        var totalTimer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.Cache);
        SourceHashCache sourceHashes = SourceHashCache.Load(paths.SourceHashCache);
        CategoryItem stock = LoadDefaultCategory(paths, catalogBytes, manifest, deploymentError);
        string[] podcastFiles = GetCandidateAudioFiles(paths.PodcastContent);
        string[] customFiles = GetCandidateAudioFiles(paths.CustomContent);
        int totalFiles = podcastFiles.Length + customFiles.Length;
        int processedFiles = 0;
        void FileProcessed() => progress?.Invoke(++processedFiles, totalFiles);
        if (totalFiles == 0)
            progress?.Invoke(0, 0);
        CategoryItem podcasts = LoadFileBackedCategory(paths, deployedTracks,
            "PODCASTS", "podcasts", "Assets/Images/icon_signal_intercepter.png",
            sourceHashes, cancellationToken, podcastFiles, FileProcessed);
        CategoryItem custom = LoadFileBackedCategory(paths, deployedTracks,
            "CUSTOM", "custom", "Assets/Images/icon_syringe.png",
            sourceHashes, cancellationToken, customFiles, FileProcessed);
        sourceHashes.RemoveMissingEntries();
        sourceHashes.Save();
        AppLog.Info($"startup.sources.hash cache_hits={sourceHashes.CacheHits} cache_misses={sourceHashes.CacheMisses} files_hashed={sourceHashes.FilesHashed}");
        AppLog.Info($"startup.sources.metadata cache_hits={sourceHashes.MetadataCacheHits} cache_misses={sourceHashes.MetadataCacheMisses} files_read={sourceHashes.MetadataFilesRead} elapsed_ms={sourceHashes.MetadataReadElapsedMilliseconds}");
        CategoryItem[] result = [stock, podcasts, custom];
        int totalTracks = result.Sum(category =>
            category.TrackCollections.Sum(collection => collection.Tracks.Count));
        AppLog.Info($"startup.library.sources tracks={totalTracks} elapsed_ms={totalTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
        return result;
    }

    internal static CategoryItem LoadDefaultCategory(GamePaths paths, byte[] catalogBytes, DeploymentManifest? manifest, string? deploymentError)
    {
        var timer = Stopwatch.StartNew();
        var category = new CategoryItem(
            "DEFAULT",
            "0.00 MB",
            "Assets/Images/icon_ipod.png");
        string? bankDirectory = FindDefaultBankDirectory(paths);

        if (bankDirectory == null)
            return category;

        long categorySize = 0;
        var albums = new Dictionary<string, TrackCollectionItem>(
            StringComparer.OrdinalIgnoreCase);

        try
        {
            using Stream catalogStream = new MemoryStream(catalogBytes, writable: false);
            DefaultTrackCatalog? catalog =
                JsonSerializer.Deserialize<DefaultTrackCatalog>(catalogStream);

            if (catalog?.Tracks == null)
                return category;

            catalog.Tracks.Sort((left, right) =>
                left.CatalogIndex.CompareTo(right.CatalogIndex));

            foreach (DefaultTrackMetadata metadata in catalog.Tracks)
            {
                if (string.IsNullOrWhiteSpace(metadata.Filename))
                    continue;

                string bankPath = Path.Combine(bankDirectory, metadata.Filename);
                if (!File.Exists(bankPath))
                    continue;

                var bankFile = new FileInfo(bankPath);
                categorySize += bankFile.Length;

                string albumName = string.IsNullOrWhiteSpace(metadata.AlbumName)
                    ? "DEFAULT"
                    : metadata.AlbumName.Trim();
                string trackName = string.IsNullOrWhiteSpace(metadata.TrackName)
                    ? Path.GetFileNameWithoutExtension(bankFile.Name)
                    : metadata.TrackName.Trim();
                string artistName = metadata.ArtistName?.Trim() ?? string.Empty;

                if (!albums.TryGetValue(albumName, out TrackCollectionItem? album))
                {
                    album = new TrackCollectionItem(albumName);
                    albums.Add(albumName, album);
                }

                bool manifestUsable = deploymentError == null;
                bool desired = manifest == null ||
                    (manifest.StockEnabled[metadata.CatalogIndex] == 1);
                album.Tracks.Add(new TrackItem(
                    trackName,
                    FormatFileSize(bankFile.Length),
                    bankFile.FullName,
                    artistName,
                    metadata.Annotation?.Trim() ?? string.Empty)
                {
                    StableKey = $"stock:{metadata.CatalogIndex}",
                    Category = LibraryCategory.Default,
                    StockIndex = metadata.CatalogIndex,
                    IsChecked = manifestUsable && desired,
                    DeploymentHealth = manifestUsable ? DeploymentHealth.Healthy : DeploymentHealth.ManifestInvalid
                });
            }
        }
        catch (IOException exception)
        {
            AppLog.Warn($"Default catalog could not be read: {exception}");
            // Keep startup usable if the catalog or BANK folder cannot be read.
        }
        catch (UnauthorizedAccessException exception)
        {
            AppLog.Warn($"Default catalog could not be read: {exception}");
            // Inaccessible game data is represented by the normal empty state.
        }
        catch (JsonException exception)
        {
            AppLog.Warn($"Default catalog is malformed: {exception}");
            // A malformed catalog is represented by the normal empty state.
        }

        var albumNames = new List<string>(albums.Keys);
        albumNames.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string albumName in albumNames)
            category.TrackCollections.Add(albums[albumName]);

        category.SizeBytes = categorySize;
        category.SizeText = FormatFileSize(categorySize);
        int trackCount = category.TrackCollections.Sum(collection => collection.Tracks.Count);
        AppLog.Info($"startup.library.stock count={trackCount} albums={category.TrackCollections.Count} elapsed_ms={timer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
        return category;
    }

    internal static string? FindDefaultBankDirectory(GamePaths paths) =>
        Directory.Exists(paths.DefaultBanks) ? paths.DefaultBanks : null;

    private sealed class DefaultTrackCatalog
    {
        [JsonPropertyName("tracks")]
        public List<DefaultTrackMetadata> Tracks { get; set; } = new();
    }

    private sealed class DefaultTrackMetadata
    {
        [JsonPropertyName("catalog_index")]
        public int CatalogIndex { get; set; }

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("track_name")]
        public string TrackName { get; set; } = string.Empty;

        [JsonPropertyName("album_name")]
        public string AlbumName { get; set; } = string.Empty;

        [JsonPropertyName("artist_name")]
        public string ArtistName { get; set; } = string.Empty;

        [JsonPropertyName("annotation")]
        public string Annotation { get; set; } = string.Empty;
    }

    internal static CategoryItem LoadFileBackedCategory(
        GamePaths paths, IReadOnlyList<DeployedTrack> deployedTracks,
        string categoryName,
        string folderName,
        string iconSource,
        CancellationToken cancellationToken = default)
    {
        SourceHashCache sourceHashes = SourceHashCache.Load(paths.SourceHashCache);
        CategoryItem category = LoadFileBackedCategory(paths, deployedTracks, categoryName,
            folderName, iconSource, sourceHashes, cancellationToken);
        sourceHashes.RemoveMissingEntries();
        sourceHashes.Save();
        return category;
    }

    private static CategoryItem LoadFileBackedCategory(
        GamePaths paths, IReadOnlyList<DeployedTrack> deployedTracks,
        string categoryName,
        string folderName,
        string iconSource,
        SourceHashCache sourceHashes,
        CancellationToken cancellationToken,
        string[]? candidateFiles = null,
        Action? fileProcessed = null)
    {
        var category = new CategoryItem(categoryName, "0.00 MB", iconSource);
        string categoryPath = string.Equals(folderName, "podcasts", StringComparison.OrdinalIgnoreCase)
            ? paths.PodcastContent : paths.CustomContent;
        long categorySize = 0;

        Directory.CreateDirectory(categoryPath);
        AddMetadataBackedTracks(category, categoryPath, categoryName,
            deployedTracks, sourceHashes, ref categorySize, cancellationToken,
            candidateFiles, fileProcessed);

        category.SizeBytes = categorySize;
        category.SizeText = FormatFileSize(categorySize);
        return category;
    }

    private static void AddMetadataBackedTracks(
        CategoryItem category,
        string categoryPath,
        string fallbackAlbumName,
        IReadOnlyList<DeployedTrack> deployedTracks,
        SourceHashCache sourceHashes,
        ref long totalSize, CancellationToken cancellationToken,
        string[]? candidateFiles = null, Action? fileProcessed = null)
    {
        var totalTimer = Stopwatch.StartNew();
        var enumerationTimer = Stopwatch.StartNew();
        string[] files = candidateFiles ?? GetCandidateAudioFiles(categoryPath);
        enumerationTimer.Stop();
        var metadataTimer = new Stopwatch();
        var hashTimer = new Stopwatch();
        var trackItemTimer = new Stopwatch();

        LibraryCategory libraryCategory = string.Equals(fallbackAlbumName, "PODCASTS", StringComparison.OrdinalIgnoreCase)
            ? LibraryCategory.Podcast : LibraryCategory.Custom;
        var discovered = new List<DiscoveredSource>();

        foreach (string filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(filePath);
            totalSize += file.Length;

            string title = Path.GetFileNameWithoutExtension(file.Name);
            string artist = string.Empty;
            string comment = string.Empty;
            uint trackNumber = 0, discNumber = 0;
            string albumName = GetFallbackAlbumName(
                file.DirectoryName,
                categoryPath,
                fallbackAlbumName);

            bool metadataRead = false;
            TimeSpan metadataReadElapsed = TimeSpan.Zero;
            if (sourceHashes.TryGetMetadata(file, out SourceHashCache.SourceMetadata cachedMetadata))
            {
                title = cachedMetadata.Title;
                artist = cachedMetadata.Artist;
                albumName = cachedMetadata.Album;
                comment = cachedMetadata.Comment;
                trackNumber = cachedMetadata.TrackNumber;
                discNumber = cachedMetadata.DiscNumber;
            }
            else
            {
                try
                {
                    var fileMetadataTimer = Stopwatch.StartNew();
                    metadataTimer.Start();
                    using TagLib.File taggedFile = TagLib.File.Create(file.FullName);

                    if (!string.IsNullOrWhiteSpace(taggedFile.Tag.Title))
                        title = taggedFile.Tag.Title.Trim();

                    if (!string.IsNullOrWhiteSpace(taggedFile.Tag.Album))
                        albumName = taggedFile.Tag.Album.Trim();

                    artist = string.Join(", ", taggedFile.Tag.Performers).Trim();
                    comment = SourceAnnotation.Read(taggedFile);
                    trackNumber = taggedFile.Tag.Track;
                    discNumber = taggedFile.Tag.Disc;
                    metadataTimer.Stop();
                    metadataReadElapsed = fileMetadataTimer.Elapsed;
                    metadataRead = true;
                }
                catch (Exception exception) when (
                    exception is TagLib.CorruptFileException ||
                    exception is TagLib.UnsupportedFormatException ||
                    exception is IOException ||
                    exception is UnauthorizedAccessException)
                {
                    metadataTimer.Stop();
                    AppLog.Warn($"Audio tags could not be read for {file.FullName}: {exception.Message}");
                }
            }

            string categoryPrefix = libraryCategory == LibraryCategory.Podcast ? "podcasts" : "custom";
            string relative = DeploymentContract.NormalizeRelative(
                Path.Combine(categoryPrefix, Path.GetRelativePath(categoryPath, file.FullName)));
            hashTimer.Start();
            byte[] sourceHash = sourceHashes.GetHash(file);
            if (metadataRead)
                sourceHashes.StoreMetadata(file,
                    new(title, artist, albumName, comment, trackNumber, discNumber), metadataReadElapsed);
            discovered.Add(new(libraryCategory, relative, file.FullName, sourceHash,
                file.Length, title, artist, albumName, comment, trackNumber, discNumber));
            hashTimer.Stop();
            fileProcessed?.Invoke();
        }

        IReadOnlyList<DeployedTrack> deployed = deployedTracks.Count != 0
            ? deployedTracks.Where(track =>
                (libraryCategory == LibraryCategory.Podcast) == (track.Classification == TrackClassification.Podcast)).ToArray()
            : Array.Empty<DeployedTrack>();
        IReadOnlyList<ReconciledSource> reconciled = LibraryReconciler.Reconcile(discovered, deployed);
        var albums = new Dictionary<string, TrackCollectionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (ReconciledSource item in reconciled)
        {
            if (item.Source is not DiscoveredSource source)
                continue;

            string albumName = source.Album;
            if (string.IsNullOrWhiteSpace(albumName)) albumName = fallbackAlbumName;
            if (!albums.TryGetValue(albumName, out TrackCollectionItem? album))
                albums.Add(albumName, album = new TrackCollectionItem(albumName));
            trackItemTimer.Start();
            album.Tracks.Add(new TrackItem(
                source.Title,
                FormatFileSize(source.Size), source.FullPath,
                source.Artist,
                source.Comment)
            {
                StableKey = item.StableKey,
                Album = albumName,
                TrackNumber = source.TrackNumber,
                DiscNumber = source.DiscNumber,
                Category = libraryCategory,
                SourceId = item.Deployed?.SourceId,
                RelativeSourcePath = source.RelativePath,
                SourceSha256 = source.Sha256,
                DeployedState = item.Deployed,
                SourceState = item.SourceState,
                DeploymentHealth = item.Health,
                IsChecked = item.Deployed != null
            });
            trackItemTimer.Stop();
        }

        var albumNames = new List<string>(albums.Keys);
        albumNames.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string albumName in albumNames)
            category.TrackCollections.Add(albums[albumName]);
        AppLog.Info($"startup.library.{libraryCategory.ToString().ToLowerInvariant()} files={files.Length} tracks={discovered.Count} albums={albums.Count} enumerate_ms={enumerationTimer.ElapsedMilliseconds} metadata_ms={metadataTimer.ElapsedMilliseconds} hash_ms={hashTimer.ElapsedMilliseconds} track_items_ms={trackItemTimer.ElapsedMilliseconds} elapsed_ms={totalTimer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId}");
    }

    private static string[] GetCandidateAudioFiles(string categoryPath)
    {
        Directory.CreateDirectory(categoryPath);
        string[] files = Directory.GetFiles(categoryPath, "*", SearchOption.AllDirectories)
            .Where(LibraryFileFilter.IsSupportedAudio)
            .ToArray();
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static string GetFallbackAlbumName(
        string? directoryPath,
        string categoryPath,
        string categoryName)
    {
        if (string.IsNullOrEmpty(directoryPath) ||
            string.Equals(directoryPath, categoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return categoryName;
        }

        return Path.GetFileName(directoryPath);
    }

    internal static string FormatFileSize(long byteCount)
    {
        double megabytes = byteCount / (1024d * 1024d);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.00} MB", megabytes);
    }
}
